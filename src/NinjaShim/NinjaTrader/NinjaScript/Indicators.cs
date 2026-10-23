/* MIT License
Copyright (c) 2025 Quantrosoft Pte. Ltd.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System;
using NinjaTrader.Data;

namespace NinjaTrader.NinjaScript.Indicators
{
   public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
   {
      public new Series<double> Value => Values[0];

      private ISeries<double> _input;
      public override ISeries<double> Input { get => _input ?? Close; set => _input = value; }

      // Used by auto-gen trailer cache lookups: returns true when this cached
      // indicator instance was created with the same input series.
      public bool EqualsInput(ISeries<double> input) => object.ReferenceEquals(Input, input);

      public double this[int barsAgo] { get { EnsureUpdated(); return Value != null ? Value[barsAgo] : 0; } }

      // NT8 indicators do `Update()` to force a recomputation on the current bar.
      public void Update() => EnsureUpdated();

      public override bool IsValidDataPoint(int barsAgo) { EnsureUpdated(); return base.IsValidDataPoint(barsAgo); }

      public int BarsRequiredToPlot { get; set; }

      public LineCollection Lines { get; } = new LineCollection();
      // `Plots` lives on IndicatorBase — ONE collection for both views
      // (see BaseClasses.cs). A second one here would be
      // exactly the duplication that made NT8's export run into nothing.

      protected override int Bip => _hostIdx >= 0 && _hostIdx < BarsArray.Length ? _hostIdx : 0;

      // ---- Lazy per-bar hosting (set by ShimRuntime.RegisterHostedIndicator) ----
      internal int _hostIdx;

      /// <summary>Index of the bar series this hosted indicator runs on (0 = primary), as resolved by
      /// ShimRuntime.RegisterHostedIndicator from its Input; -1 when it could not be resolved. Lets code outside
      /// the shim key the per-series events of ShimRuntime (SeriesTickConsolidated, SeriesBarClosed,
      /// SeriesTickDispatched) to the indicator's own series.</summary>
      public int HostSeriesIndex => _hostIdx;

      internal int _lastDrivenBar = -1;
      private bool _driving;
      internal readonly System.Collections.Generic.List<IAdvanceableSeries> _ownedSeries
         = new System.Collections.Generic.List<IAdvanceableSeries>();

      // Advance this indicator to CurrentBars[_hostIdx] (its host series' bar
      // count), running OnBarUpdate once per new bar. Triggered lazily on value
      // access — mirrors NT8's on-demand indicator update. Each bar appends a
      // fresh (invalid) slot to every Values plot; OnBarUpdate overwrites
      // Values[i][0] for the plots it computes, marking them valid.
      // ---- Indicator probe -------------------------------------------------
      // OFF by default. Switched on via the environment variable
      //     NINJASHIM_TRACE_INDI=<TypeName>[,<TypeName>...]
      // it writes, for the first bars of each indicator, WHAT it sees and what
      // it makes of it. Reason: an indicator that computes without an exception but never
      // delivers a signal cannot be told apart (empty input or wrong maths) without
      // its numbers.
      private static readonly string[] _traceIndi =
         (System.Environment.GetEnvironmentVariable("NINJASHIM_TRACE_INDI") ?? "")
            .Split(new[] { ',' }, System.StringSplitOptions.RemoveEmptyEntries);
      private int _traced;

      private void TraceBar()
      {
         if (_traceIndi.Length == 0 || _traced >= 40) return;
         string me = GetType().Name;
         bool hit = false;
         foreach (var t in _traceIndi) if (me == t.Trim()) { hit = true; break; }
         if (!hit) return;
         _traced++;
         double i0 = 0, iP = 0, v0 = 0, v1 = 0, p1 = 0;
         int icnt = 0, p1cnt = -1;
         try { icnt = Input != null ? Input.Count : -1; } catch { icnt = -2; }
         try { i0 = Input[0]; } catch { i0 = double.NaN; }
         try { iP = Input[System.Math.Min(2, System.Math.Max(icnt - 1, 0))]; } catch { iP = double.NaN; }
         try { v0 = Value[0]; } catch { v0 = double.NaN; }
         try { v1 = CurrentBar > 0 ? Value[1] : double.NaN; } catch { v1 = double.NaN; }
         try { p1cnt = Values != null && Values.Length > 1 && Values[1] != null ? Values[1].Count : -1; } catch { p1cnt = -2; }
         try { p1 = Values != null && Values.Length > 1 ? Values[1][0] : double.NaN; } catch { p1 = double.NaN; }
         // Print the [FORMING-BLIND] decision values too: without them the probe
         // does not show whether the drive ran clamped and anchored.
         var _tbHost = (BarsArray != null && _hostIdx >= 0 && _hostIdx < BarsArray.Length)
                       ? BarsArray[_hostIdx] : null;
         _runtime?.WriteLine?.Invoke(
            $"[SHIM][SONDE][{me}] bar={CurrentBar} hostIdx={_hostIdx} " +
            $"inCount={icnt} in[0]={i0:F2} in[back]={iP:F2} val[0]={v0:F2} val[1]={v1:F2} " +
            $"plot1[0]={p1:F2} plot1.Count={p1cnt} calc={Calculate} " +
            $"hostBars={(_tbHost == null ? "null" : "ok")} forming={_tbHost?.FormingSlotMirrored.ToString() ?? "?"}");
      }

      internal void EnsureUpdated()
      {
         if (_runtime == null || _driving) return;
         int target = (CurrentBars != null && _hostIdx >= 0 && _hostIdx < CurrentBars.Length)
                        ? CurrentBars[_hostIdx] : CurrentBar;
         if (_runtime.DriveIndicatorInputFirst && Input is Indicator inputIndicator)
            inputIndicator.EnsureUpdated();
         if (Input != null && Input.Count - 1 < target)
            target = Input.Count - 1;
         var hostBars = (BarsArray != null && _hostIdx >= 0 && _hostIdx < BarsArray.Length)
                        ? BarsArray[_hostIdx] : null;
         bool followStrategy = _runtime != null && _runtime.FormingViewForAllHosted;
         if (followStrategy && Calculate != NinjaTrader.NinjaScript.Calculate.OnEachTick)
            Calculate = NinjaTrader.NinjaScript.Calculate.OnEachTick;
         bool formingBlind = hostBars != null && hostBars.FormingSlotMirrored
                             && Calculate != NinjaTrader.NinjaScript.Calculate.OnEachTick
                             && !followStrategy;
         if (formingBlind && target > hostBars.Count - 2)
            target = hostBars.Count - 2;
         bool needDrive = _lastDrivenBar < target;
         if (_formingSlot && (needDrive || !formingBlind))
         {
            for (int i = 0; i < Values.Length; i++) Values[i]?.RemoveFormingSlot();
            for (int i = 0; i < _ownedSeries.Count; i++) _ownedSeries[i].RemoveFormingSlot();
            _formingSlot = false;
         }
         if (!needDrive)
         {
            if (formingBlind && !_formingSlot && _lastDrivenBar == hostBars.Count - 2)
            {
               for (int i = 0; i < Values.Length; i++) Values[i]?.AppendFormingSlot();
               for (int i = 0; i < _ownedSeries.Count; i++) _ownedSeries[i].AppendFormingSlot();
               _formingSlot = true;
            }
            if (followStrategy && hostBars != null && hostBars.FormingSlotMirrored
                && _lastDrivenBar == hostBars.Count - 1 && _lastDrivenBar >= 0)
            {
               _driving = true;
               var anchorableRedrive = Input as IAnchorableSeries;
               try
               {
                  BarsInProgress = 0;
                  CurrentBar     = _lastDrivenBar;
                  anchorableRedrive?.SetAnchor(_lastDrivenBar);
                  IsFirstTickOfBar = false;   // [HOSTED-CALCULATE-VISIBLE] a later tick of the same bar
                  try { OnBarUpdate(); }
                  catch (System.Exception ex) { _runtime?.LogShimError(this, "Indicator.OnBarUpdate(forming re-drive)", ex); }
               }
               finally
               {
                  anchorableRedrive?.ClearAnchor();
                  _driving = false;
               }
            }
            return;
         }
         _driving = true;
         try
         {
            var anchorable = Input as IAnchorableSeries;
            try
            {
               while (_lastDrivenBar < target)
               {
                  _lastDrivenBar++;
                  for (int i = 0; i < Values.Length; i++) Values[i]?.AppendDefault();
                  for (int i = 0; i < _ownedSeries.Count; i++) _ownedSeries[i].AppendDefault();
                  // NT8 view of the indicator: ITS series is index 0 (the binding
                  // to the series sits in `Bip`, see above). `_hostIdx` used to
                  // stand here — with that, `BarsInProgress != 0` was true for
                  // every indicator on a secondary series, and the usual NT8
                  // guard condition never let it compute.
                  BarsInProgress = 0;
                  CurrentBar     = _lastDrivenBar;
                  anchorable?.SetAnchor(_lastDrivenBar);
                  if (formingBlind) hostBars.SetSeriesAnchors(_lastDrivenBar);   // [FORMING-BLIND]
                  IsFirstTickOfBar = true;    // [HOSTED-CALCULATE-VISIBLE] first drive of this bar
                  try { OnBarUpdate(); }
                  catch (System.Exception ex) { _runtime?.LogShimError(this, "Indicator.OnBarUpdate", ex); }
                  TraceBar();
               }
            }
            finally
            {
               anchorable?.ClearAnchor();
               if (formingBlind) hostBars.ClearSeriesAnchors();   // [FORMING-BLIND]
            }
            // [INDICATOR-FORMING-SLOT] current up to the last finished bar and
            // a forming bar is mirrored: give it its slot (see above).
            if (formingBlind && !_formingSlot && _lastDrivenBar == hostBars.Count - 2)
            {
               for (int i = 0; i < Values.Length; i++) Values[i]?.AppendFormingSlot();
               for (int i = 0; i < _ownedSeries.Count; i++) _ownedSeries[i].AppendFormingSlot();
               _formingSlot = true;
            }
         }
         finally { _driving = false; }
      }
      private bool _formingSlot;
   }

   public class Line
   {
      public double Value { get; set; }
      public System.Windows.Media.Brush Brush { get; set; }
      public string Name { get; set; }
      public NinjaTrader.Gui.Stroke Stroke { get; set; }
   }

   public class Plot
   {
      public double Value { get; set; }
      public System.Windows.Media.Brush Brush { get; set; }
      public string Name { get; set; }
      public NinjaTrader.NinjaScript.PlotStyle PlotStyle { get; set; }
      public int Width { get; set; } = 1;
      public SharpDX.Direct2D1.Brush BrushDX { get; set; }
      public SharpDX.Direct2D1.StrokeStyle StrokeStyle { get; set; }
   }

   public class LineCollection : System.Collections.Generic.List<Line>
   {
      public LineCollection() { for (int i = 0; i < 4; i++) Add(new Line()); }
      public new Line this[int index]
      {
         get { EnsureSize(index + 1); return ((System.Collections.Generic.List<Line>)this)[index]; }
         set { EnsureSize(index + 1); ((System.Collections.Generic.List<Line>)this)[index] = value; }
      }
      private void EnsureSize(int n) { while (Count < n) Add(new Line()); }
   }
   public class PlotCollection : System.Collections.Generic.List<Plot>
   {
      public PlotCollection() { }
      public int Length => Count;
      public new Plot this[int index]
      {
         get { EnsureSize(index + 1); return ((System.Collections.Generic.List<Plot>)this)[index]; }
         set { EnsureSize(index + 1); ((System.Collections.Generic.List<Plot>)this)[index] = value; }
      }
      private void EnsureSize(int n) { while (Count < n) Add(new Plot()); }
   }

   public class PriceSeries : ISeries<double>
   {
      private readonly System.Func<int, double> _f;
      private readonly System.Func<int> _count;
      private readonly System.Func<int, bool> _valid;
      protected PriceSeries(System.Func<int, double> f, System.Func<int> count,
                            System.Func<int, bool> valid)
      { _f = f; _count = count; _valid = valid; }
      public int Count => _count();
      public double this[int barsAgo] => _f(barsAgo);
      public double GetValueAt(int barIndex) => _f(Count - 1 - barIndex);
      public bool IsValidDataPoint(int barsAgo) => _valid(barsAgo);
   }

   public sealed class DerivedSeries : PriceSeries
   {
      // Inherits from PriceSeries so that `Input is PriceSeries` (@Swing.cs:96)
      // hits for Typical/Median/Weighted — they ARE price series of the
      // instrument. No own buffer: it is computed from High/Low/Close on each
      // access, so that no second truth arises beside the base series.
      public DerivedSeries(NinjaScriptBase o, System.Func<int, double> f)
         : base(f, () => o.Close.Count, i => o.Close.IsValidDataPoint(i)) { }
   }

   public class Resource { }

   namespace Custom
   {
      public static partial class Resource
      {

         public const string NinjaScriptIndicatorDescriptionDoubleStochastics = "Double Stochastics";
         public const string NinjaScriptIndicatorNameDoubleStochastics        = "Double Stochastics";
         public const string StochasticsK = "%K";
         public const string StochasticsD = "%D";
         public const string StochasticsPeriod = "Period";
         public const string NinjaScriptIndicatorUpper = "Upper";
         public const string NinjaScriptIndicatorLower = "Lower";
         public const string NinjaScriptIndicatorMiddle = "Middle";
         public const string NinjaScriptIndicatorDescriptionRegressionChannel = "Regression Channel";
         public const string NinjaScriptIndicatorNameRegressionChannel = "Regression Channel";
         public const string NinjaScriptIndicatorOverbought = "Overbought";
         public const string NinjaScriptIndicatorOversold   = "Oversold";
         public const string NinjaScriptIndicatorDescriptionChoppinessIndex = "Choppiness Index";
         public const string NinjaScriptIndicatorNameChoppinessIndex        = "Choppiness Index";
         public const string NinjaScriptIndicatorStrong                     = "Strong";
         public const string NinjaScriptIndicatorChoppy                     = "Choppy";

         public const string NinjaScriptIndicatorDescriptionATR = "Average True Range";
         public const string NinjaScriptIndicatorNameATR        = "ATR";
         public const string NinjaScriptIndicatorDescriptionMAX = "Maximum";
         public const string NinjaScriptIndicatorNameMAX        = "MAX";
         public const string NinjaScriptIndicatorDescriptionMIN = "Minimum";
         public const string NinjaScriptIndicatorNameMIN        = "MIN";
         public const string NinjaScriptIndicatorDescriptionDonchianChannel = "Donchian Channel";
         public const string NinjaScriptIndicatorNameDonchianChannel        = "Donchian Channel";
         public const string DonchianChannelMean                            = "Mean";
         public const string NinjaScriptIndicatorDescriptionLinReg = "Linear Regression";
         public const string NinjaScriptIndicatorNameLinReg        = "LinReg";
         public const string NinjaScriptIndicatorDescriptionDM  = "Directional Movement";
         public const string NinjaScriptIndicatorNameDM         = "DM";
         public const string NinjaScriptIndicatorDescriptionVolumeUpDown = "Volume Up Down";
         public const string NinjaScriptIndicatorNameVolumeUpDown        = "Volume Up Down";
         public const string NinjaScriptIndicatorDescriptionADX = "Average Directional Index";
         public const string NinjaScriptIndicatorNameADX        = "ADX";
         public const string NinjaScriptIndicatorDescriptionSMA = "Simple Moving Average";
         public const string NinjaScriptIndicatorNameSMA        = "SMA";
         public const string NinjaScriptIndicatorDescriptionEMA = "Exponential Moving Average";
         public const string NinjaScriptIndicatorNameEMA        = "EMA";
         public const string NinjaScriptIndicatorDescriptionWMA = "Weighted Moving Average";
         public const string NinjaScriptIndicatorNameWMA        = "WMA";
         public const string NinjaScriptIndicatorDescriptionHMA = "Hull Moving Average";
         public const string NinjaScriptIndicatorNameHMA        = "HMA";
         public const string NinjaScriptIndicatorDescriptionTEMA = "Triple Exponential Moving Average";
         public const string NinjaScriptIndicatorNameTEMA        = "TEMA";
         public const string NinjaScriptIndicatorDescriptionDEMA = "Double Exponential Moving Average";
         public const string NinjaScriptIndicatorNameDEMA        = "DEMA";
         public const string NinjaScriptIndicatorDescriptionRSI = "Relative Strength Index";
         public const string NinjaScriptIndicatorNameRSI        = "RSI";
         public const string NinjaScriptIndicatorDescriptionMACD = "MACD";
         public const string NinjaScriptIndicatorNameMACD        = "MACD";
         public const string NinjaScriptIndicatorDescriptionBollinger = "Bollinger Bands";
         public const string NinjaScriptIndicatorNameBollinger        = "Bollinger";
         public const string NinjaScriptIndicatorDescriptionCCI = "Commodity Channel Index";
         public const string NinjaScriptIndicatorNameCCI        = "CCI";
         public const string NinjaScriptIndicatorDescriptionMFI = "Money Flow Index";
         public const string NinjaScriptIndicatorNameMFI        = "MFI";
         public const string NinjaScriptIndicatorDescriptionOBV = "On Balance Volume";
         public const string NinjaScriptIndicatorNameOBV        = "OBV";
         public const string NinjaScriptIndicatorDescriptionStdDev = "Standard Deviation";
         public const string NinjaScriptIndicatorNameStdDev        = "StdDev";
         public const string NinjaScriptIndicatorDescriptionStochastics = "Stochastics";
         public const string NinjaScriptIndicatorNameStochastics        = "Stochastics";
         public const string NinjaScriptIndicatorDescriptionStochasticsFast = "Stochastics Fast";
         public const string NinjaScriptIndicatorNameStochasticsFast        = "Stochastics Fast";
         public const string NinjaScriptIndicatorDescriptionSwing = "Swing";
         public const string NinjaScriptIndicatorNameSwing        = "Swing";
         public const string NinjaScriptIndicatorDescriptionSUM = "Sum";
         public const string NinjaScriptIndicatorNameSUM        = "SUM";
         public const string NinjaScriptIndicatorDescriptionRange = "Range";
         public const string NinjaScriptIndicatorNameRange        = "Range";
         public const string NinjaScriptIndicatorDescriptionMomentum = "Momentum";
         public const string NinjaScriptIndicatorNameMomentum        = "Momentum";
         public const string NinjaScriptIndicatorDescriptionTSI = "True Strength Index";
         public const string NinjaScriptIndicatorNameTSI        = "TSI";
         public const string NinjaScriptIndicatorDescriptionVOL = "Volume";
         public const string NinjaScriptIndicatorNameVOL        = "VOL";

         // Plot/line labels that @DM.cs and @VolumeUpDown.cs pull in on top of
         // Name+Description. These too are pure attribute strings — they label,
         // they do not compute.
         public const string DMPlusDI  = "+DI";
         public const string DMMinusDI = "-DI";
         public const string VolumeUp   = "Volume up";
         public const string VolumeDown = "Volume down";
         public const string NinjaScriptIndicatorStrongTrend     = "Strong Trend";
         public const string NinjaScriptIndicatorVeryStrongTrend = "Very Strong Trend";
         public const string NinjaScriptIndicatorZeroLine        = "Zero Line";
         public const string NinjaScriptOnPriceChangeError       = "OnPriceChange error";

         // Plot/level/error-text labels of the remaining linked originals
         // (@Bollinger, @CCI, @MACD, @Range, @Swing, @VOL).
         // These too are pure attribute or message strings.
         public const string BollingerUpperBand  = "Upper band";
         public const string BollingerMiddleBand = "Middle band";
         public const string BollingerLowerBand  = "Lower band";
         public const string CCILevel1       = "Level 1";
         public const string CCILevel2       = "Level 2";
         public const string CCILevelMinus1  = "Level -1";
         public const string CCILevelMinus2  = "Level -2";
         public const string NinjaScriptIndicatorAvg  = "Avg";
         public const string NinjaScriptIndicatorDiff = "Diff";
         public const string RangeValue = "Range";
         public const string VOLVolume  = "Volume";
         public const string SwingHigh = "Swing high";
         public const string SwingLow  = "Swing low";
         public const string SwingSwingHighBarBarsAgoGreaterEqual = "SwingHighBar: barsAgo muss >= 0 sein";
         public const string SwingSwingHighBarBarsAgoOutOfRange   = "SwingHighBar: barsAgo liegt ausserhalb des gueltigen Bereichs";
         public const string SwingSwingHighBarInstanceGreaterEqual= "SwingHighBar: instance muss >= 1 sein";
         public const string SwingSwingLowBarBarsAgoGreaterEqual  = "SwingLowBar: barsAgo muss >= 0 sein";
         public const string SwingSwingLowBarBarsAgoOutOfRange    = "SwingLowBar: barsAgo liegt ausserhalb des gueltigen Bereichs";
         public const string SwingSwingLowBarInstanceGreaterEqual = "SwingLowBar: instance muss >= 1 sein";
      }
   }

   internal interface IAdvanceableSeries
   {
      void AppendDefault();
      void AppendFormingSlot();
      void RemoveFormingSlot();
   }

   internal interface IAnchorableSeries { void SetAnchor(int absIndex); void ClearAnchor(); }

   public class Series<T> : ISeries<T>, IAdvanceableSeries, IAnchorableSeries
   {
      private readonly System.Collections.Generic.List<T> _buf;
      private readonly System.Collections.Generic.List<bool> _valid;
      // [CATCHUP-ANCHOR] -1 = no anchor (read head = end of series). Set only
      // by EnsureUpdated while catching up a hosted indicator.
      private int _anchor = -1;
      public void SetAnchor(int absIndex) { _anchor = absIndex; }
      public void ClearAnchor() { _anchor = -1; }
      private int Head => _anchor >= 0 ? _anchor : _buf.Count - 1;
      public Series() { _buf = new System.Collections.Generic.List<T>(); _valid = new System.Collections.Generic.List<bool>(); }
      public void Reset()
      {
         if (_valid.Count > 0) _valid[_valid.Count - 1] = false;
      }
      public void AppendFormingSlot()
      {
         _buf.Add(_buf.Count > 0 ? _buf[_buf.Count - 1] : default);
         _valid.Add(false);
      }
      public void RemoveFormingSlot()
      {
         if (_buf.Count == 0) return;
         _buf.RemoveAt(_buf.Count - 1);
         _valid.RemoveAt(_valid.Count - 1);
      }

      /// <summary>NT8's `Series.Reset(barsAgo)` — same semantics, only not on the
      /// current bar but on a past one. `barsAgo = 0` is identical to the
      /// parameterless form.</summary>
      public void Reset(int barsAgo)
      {
         int idx = _valid.Count - 1 - barsAgo;
         if (idx >= 0 && idx < _valid.Count) _valid[idx] = false;
      }

      public Series(NinjaScriptBase owner) : this()
      {
         if (owner is Indicator ind) ind._ownedSeries.Add(this);
      }

      public Series(NinjaScriptBase owner, MaximumBarsLookBack maxBarsLookBack) : this(owner) { }

      public T GetValueAt(int barIndex)
         => (barIndex >= 0 && barIndex < _buf.Count) ? _buf[barIndex] : default;

      public T this[int barsAgo]
      {
         get
         {
            // [CATCHUP-ANCHOR] read head = anchor (catch-up phase) or end of series.
            int idx = Head - barsAgo;
            if (idx < 0 || idx >= _buf.Count)
               throw new System.ArgumentOutOfRangeException(nameof(barsAgo), barsAgo,
                  $"Lookback outside the series: [{barsAgo}] with {_buf.Count} " +
                  $"values present (read head {Head}).");
            return _buf[idx];
         }
         set
         {
            // NT8 indicators write `Value[0] = computed` to publish the
            // current-bar output → marks that slot valid.
            int idx = _buf.Count - 1 - barsAgo;
            if (idx >= 0 && idx < _buf.Count) { _buf[idx] = value; _valid[idx] = true; }
            else if (barsAgo == 0)            { _buf.Add(value);   _valid.Add(true); }
            // barsAgo > Count or < 0: silently drop (NT8 also no-ops past extent)
         }
      }

      public int Count => _buf.Count;
      // Valid only if the slot was explicitly WRITTEN (NT8 semantics) — slots
      // auto-advanced via AppendDefault read back invalid, so an indicator's
      // IsValidDataPoint warmup gate stays false until it actually computes.
      public bool IsValidDataPoint(int barsAgo)
      {
         int idx = Head - barsAgo;   // [CATCHUP-ANCHOR] like the indexer
         return idx >= 0 && idx < _buf.Count && _valid[idx];
      }

      // Internal mutators — ShimRuntime / Bars / indicator hosting write here.
      // (Public so BarStream in MyLean.NinjaShim can append without reflection.)
      public void Append(T value) { _buf.Add(value); _valid.Add(true); }    // real value (Bars OHLC)
      public void RemoveLast()
      {
         if (_buf.Count == 0)
            throw new System.InvalidOperationException(
               "[SERIES] RemoveLast on an empty series. No fallback.");
         _buf.RemoveAt(_buf.Count - 1);
         _valid.RemoveAt(_valid.Count - 1);
      }
      public void AppendDefault()  { _buf.Add(default); _valid.Add(false); } // per-bar advance, invalid until written
      internal void Set(int barsAgo, T value)
      {
         int idx = _buf.Count - 1 - barsAgo;
         if (idx >= 0 && idx < _buf.Count) { _buf[idx] = value; _valid[idx] = true; }
      }
      internal void Clear() { _buf.Clear(); _valid.Clear(); }
   }

   public class DataSeries : Series<double> { }
}

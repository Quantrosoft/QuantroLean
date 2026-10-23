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
using System.Collections.Generic;
using NinjaTrader.Data;

namespace NinjaTrader.NinjaScript
{
   public enum State
   {
      SetDefaults,
      Configure,
      Active,
      DataLoaded,
      Historical,
      Transition,
      Realtime,
      Terminated,
      Finalized,
   }

   public enum Calculate
   {
      OnEachTick,
      OnPriceChange,
      OnBarClose,
   }

   public abstract partial class NinjaScriptBase
   {
      // Wired by ShimRuntime when the strategy is hosted.
      internal MyLean.NinjaShim.ShimRuntime _runtime;

      // ---- Lifecycle -------------------------------------------------------
      public State State { get; internal set; } = State.SetDefaults;

      protected virtual void OnStateChange() { }
      protected virtual void OnBarUpdate() { }
      protected virtual void OnMarketData(MarketDataEventArgs marketDataUpdate) { }
      protected virtual void OnConnectionStatusUpdate(ConnectionStatusEventArgs connectionStatusUpdate) { }
      protected virtual void OnFundamentalData(FundamentalDataEventArgs fundamentalDataUpdate) { }
      protected virtual void OnMarketDepth(MarketDepthEventArgs marketDepthUpdate) { }

      // ---- Universal config (set during SetDefaults / Configure) ----------
      public string Name { get; set; }
      public string Description { get; set; }
      public string InstrumentOrInstrumentList { get; set; }
      public Calculate Calculate { get; set; } = Calculate.OnBarClose;
      public int Displacement { get; set; }
      public int BarsRequiredToTrade { get; set; } = 20;

      public TimeZoneInfo PlatformTimeZoneInfo { get; set; } = TimeZoneInfo.Utc;
      public bool IsOverlay { get; set; }
      public bool IsAutoScale { get; set; } = true;
      public bool DisplayInDataBox { get; set; } = true;
      public bool DrawOnPricePanel { get; set; }
      public bool IsSuspendedWhileInactive { get; set; }
      public bool IsInstantiatedOnEachOptimizationIteration { get; set; } = true;
      public bool BarsRequired { get; set; }
      // Note: `MaximumBarsLookBack` (enum) lives on the partial in
      // NinjaScriptExtensions.cs — declaring an int here would shadow it.

      public Bars Bars { get; internal set; }
      public Bars[] BarsArray { get; internal set; } = Array.Empty<Bars>();
      public int BarsInProgress { get; internal set; }
      public int CurrentBar { get; internal set; }
      public int[] CurrentBars { get; internal set; } = Array.Empty<int>();
      public BarsPeriod[] BarsPeriods
      {
         get
         {
            var a = new BarsPeriod[BarsArray.Length];
            for (int i = 0; i < a.Length; i++) a[i] = BarsArray[i]?.BarsPeriod;
            return a;
         }
      }

      public DateTime From => BarsArray.Length > 0 ? BarsArray[0].FromDate : DateTime.MinValue;
      public DateTime To   => BarsArray.Length > 0 ? BarsArray[0].ToDate   : DateTime.MaxValue;
      public bool IsFirstTickOfBar { get; internal set; }

      public NinjaTrader.NinjaScript.Indicators.Series<double>[] Values { get; internal set; }
         = new NinjaTrader.NinjaScript.Indicators.Series<double>[32];

      /// <summary>NT8 convention: the validity test goes against the first
      /// value series. `Indicator` overrides it in order to update first
      /// (shim peculiarity: indicators run lazily).</summary>
      public virtual bool IsValidDataPoint(int barsAgo)
         => Values != null && Values.Length > 0 && Values[0] != null
            && Values[0].IsValidDataPoint(barsAgo);

      public ISeriesCollection<double>   Closes  => new ISeriesCollection<double>(BarsArray, b => b.Close);
      public ISeriesCollection<double>   Opens   => new ISeriesCollection<double>(BarsArray, b => b.Open);
      public ISeriesCollection<double>   Highs   => new ISeriesCollection<double>(BarsArray, b => b.High);
      public ISeriesCollection<double>   Lows    => new ISeriesCollection<double>(BarsArray, b => b.Low);
      public ISeriesCollection<double>   Volumes => new ISeriesCollection<double>(BarsArray, b => b.Volume);
      public ISeriesCollection<DateTime> Times   => new ISeriesCollection<DateTime>(BarsArray, StrategyTimes);

      private ISeries<DateTime> StrategyTimes(Bars b)
         => b == null ? null
          : (_runtime != null && _runtime.Nt8LocalBarTimes) ? new LocalWallTimeSeries(b.Time)
          : b.Time;

      protected virtual int Bip => BarsInProgress >= 0 && BarsInProgress < BarsArray.Length
                                   ? BarsInProgress : 0;
      public ISeries<double>   Close => BarsArray.Length > 0 ? BarsArray[Bip].Close : null;
      public ISeries<double>   Open  => BarsArray.Length > 0 ? BarsArray[Bip].Open  : null;
      public ISeries<double>   High  => BarsArray.Length > 0 ? BarsArray[Bip].High  : null;
      public ISeries<double>   Low   => BarsArray.Length > 0 ? BarsArray[Bip].Low   : null;
      public ISeries<double>   Volume => BarsArray.Length > 0 ? BarsArray[Bip].Volume : null;
      public ISeries<DateTime> Time  => BarsArray.Length > 0 ? StrategyTimes(BarsArray[Bip]) : null;

      public NinjaTrader.Cbi.Instrument Instrument =>
         BarsArray.Length > 0
            ? BarsArray[0].Instrument
            : _runtime?.DefaultInstrument;

      // ---- Output ---------------------------------------------------------
      public void Print(object msg) => _runtime?.Print(this, msg?.ToString() ?? "");
      public void Print(string format, params object[] args) =>
         _runtime?.Print(this, string.Format(format, args));
      public void Log(string msg, NinjaTrader.Cbi.LogLevel level = NinjaTrader.Cbi.LogLevel.Information) =>
         _runtime?.Log(this, msg, level);
      public void ClearOutputWindow() { /* no-op */ }

      // ---- Data subscriptions (called during State.Configure) -------------
      public void AddDataSeries(BarsPeriodType periodType, int value) =>
         _runtime?.AddDataSeries(null, periodType, value, MarketDataType.Last);

      public void AddDataSeries(string instrumentName, BarsPeriodType periodType, int value) =>
         _runtime?.AddDataSeries(instrumentName, periodType, value, MarketDataType.Last);

      public void AddDataSeries(string instrumentName, BarsPeriodType periodType, int value,
                                MarketDataType marketDataType) =>
         _runtime?.AddDataSeries(instrumentName, periodType, value, marketDataType);

      public void AddDataSeries(string instrumentName, BarsPeriod barsPeriod, int barsToLoad,
                                MarketDataType marketDataType, string tradingHoursName) =>
         _runtime?.AddDataSeries(instrumentName, barsPeriod.BarsPeriodType, barsPeriod.Value, marketDataType);

      public void AddRenko(string instrumentName, int brickSize, MarketDataType marketDataType) =>
         _runtime?.AddDataSeries(instrumentName, BarsPeriodType.Renko, brickSize, marketDataType);

      public void AddHeikenAshi(string instrumentName, BarsPeriodType basePeriodType,
                                int value, MarketDataType marketDataType) =>
         _runtime?.AddDataSeries(instrumentName, BarsPeriodType.HeikenAshi, value, marketDataType);

      public void AddDataSeries(BarsPeriod barsPeriod) =>
         _runtime?.AddDataSeries(null, barsPeriod.BarsPeriodType, barsPeriod.Value, MarketDataType.Last);

      protected void AddLine(System.Windows.Media.Brush brush, double value, string name) { /* no-op */ }
      protected void AddLine(NinjaTrader.Gui.Stroke stroke, double value, string name) { /* no-op */ }

      // ---- Misc -----------------------------------------------------------
      public DateTime Now => _runtime?.Now ?? DateTime.MinValue;
      public bool IsConnected => _runtime?.IsConnected ?? false;
      public bool IsInStrategyAnalyzer => _runtime?.IsBacktest ?? true;

      public virtual ISeries<double> Input { get => Close; set { /* base no-op */ } }

      public ISeries<double> Typical => _typical ??= new NinjaTrader.NinjaScript.Indicators.DerivedSeries(this,
         i => (High[i] + Low[i] + Close[i]) / 3.0);
      private ISeries<double> _typical;

      public ISeries<double> Median => _median ??= new NinjaTrader.NinjaScript.Indicators.DerivedSeries(this,
         i => (High[i] + Low[i]) / 2.0);
      private ISeries<double> _median;

      public ISeries<double> Weighted => _weighted ??= new NinjaTrader.NinjaScript.Indicators.DerivedSeries(this,
         i => (High[i] + Low[i] + 2.0 * Close[i]) / 4.0);
      private ISeries<double> _weighted;

      public ISeries<double>[] Inputs => _inputs ??= new[] { Input };
      private ISeries<double>[] _inputs;

      public virtual string DisplayName => GetType().Name;

      protected NinjaTrader.NinjaScript.Indicators.Indicator indicator
      {
         get
         {
            if (_indicatorFactory == null)
            {
               _indicatorFactory = new NinjaTrader.NinjaScript.Indicators.Indicator();
               _indicatorFactory._runtime  = this._runtime;
            }
            // Refresh each access — BarsArray/CurrentBars may have grown via
            // AddDataSeries since the factory was first created.
            _indicatorFactory.Bars        = this.Bars;
            _indicatorFactory.BarsArray   = this.BarsArray;
            _indicatorFactory.CurrentBars = this.CurrentBars;
            return _indicatorFactory;
         }
      }
      private NinjaTrader.NinjaScript.Indicators.Indicator _indicatorFactory;

      protected T CacheIndicator<T>(T newInstance, ISeries<double> input, ref T[] cache)
         where T : NinjaTrader.NinjaScript.Indicators.Indicator
      {
         if (cache == null) cache = new T[1] { newInstance };
         else
         {
            var grown = new T[cache.Length + 1];
            System.Array.Copy(cache, grown, cache.Length);
            grown[cache.Length] = newInstance;
            cache = grown;
         }
         newInstance.Input = input;
         _runtime?.RegisterHostedIndicator(newInstance, input, this);
         return newInstance;
      }

      internal void DispatchStateChange(State newState)
      {
         State = newState;
         try { OnStateChange(); }
         catch (Exception ex) { _runtime?.LogShimError(this, "OnStateChange", ex); }
      }

      internal void DispatchBarUpdate()
      {
         _runtime?.NotifyBarUpdateCalled(this);
         try { OnBarUpdate(); }
         catch (Exception ex) { _runtime?.LogShimError(this, "OnBarUpdate", ex); }
      }

      internal void DispatchMarketData(MarketDataEventArgs ev)
      {
         try { OnMarketData(ev); }
         catch (Exception ex) { _runtime?.LogShimError(this, "OnMarketData", ex); }
      }
   }

   // ---- Series / time-series base interfaces ------------------------------
   public interface ISeries<T>
   {
      T this[int barsAgo] { get; }
      int Count { get; }
      bool IsValidDataPoint(int barsAgo);
   }

   // Indexable wrapper for the per-bars-stream series accessors (Closes,
   // Opens, etc.). `Closes[1]` returns the secondary stream's close series.
   public class ISeriesCollection<T>
   {
      private readonly Bars[] _bars;
      private readonly System.Func<Bars, ISeries<T>> _selector;
      internal ISeriesCollection(Bars[] bars, System.Func<Bars, ISeries<T>> selector)
      { _bars = bars; _selector = selector; }

      public ISeries<T> this[int barsArrayIdx] =>
         (barsArrayIdx >= 0 && barsArrayIdx < _bars.Length) ? _selector(_bars[barsArrayIdx]) : null;
   }

   public sealed class LocalWallTimeSeries : ISeries<DateTime>
   {
      private readonly ISeries<DateTime> _utc;
      public LocalWallTimeSeries(ISeries<DateTime> utc) { _utc = utc; }

      public static DateTime ToLocalWall(DateTime utc)
         => utc == default
            ? utc
            : DateTime.SpecifyKind(
                 TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), TimeZoneInfo.Local),
                 DateTimeKind.Unspecified);

      public DateTime this[int barsAgo] => ToLocalWall(_utc[barsAgo]);
      public int Count => _utc.Count;
      public bool IsValidDataPoint(int barsAgo) => _utc.IsValidDataPoint(barsAgo);
   }

   // (No extension methods needed — Bars.Close etc. are already typed
   //  Series<double> which implements ISeries<double> directly.)

   // ---- Plot stubs (drawing — no-op in Lean) ------------------------------
   // Note: `Stroke` lives in NinjaTrader.Gui (GraphicsStubs.cs) — having a
   // duplicate here in NinjaTrader.NinjaScript would create CS0104 ambiguity
   // with linked indicator source that does `using NinjaTrader.Gui;`.

   public enum PlotStyle { Line, Dot, Bar, Hash, Square, TriangleDown, TriangleUp, HLine }

   public enum SerieDataType { OHLC, BidAsk }
   public enum SubplotPriority { Low, Medium, High }

   public enum InputPriceType { Default, Bid, Ask, Last }

   public class FundamentalDataEventArgs : EventArgs
   {
      public NinjaTrader.Cbi.Instrument Instrument { get; set; }
   }
}

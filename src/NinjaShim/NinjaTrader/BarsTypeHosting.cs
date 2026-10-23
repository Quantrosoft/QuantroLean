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
using NinjaTrader.NinjaScript.Indicators;   // Series<T>

namespace NinjaTrader.NinjaScript
{
   public abstract partial class BarsType : NinjaScriptBase
   {
      // ---- Configuration the hosted types set in OnStateChange -----------------
      public NinjaTrader.Data.BarsPeriod     BarsPeriod        { get; set; } = new NinjaTrader.Data.BarsPeriod();
      public NinjaTrader.Data.BarsPeriodType BuiltFrom         { get; set; } = NinjaTrader.Data.BarsPeriodType.Tick;
      public int                             DaysToLoad        { get; set; } = 3;
      public NinjaTrader.Gui.Chart.ChartStyleType DefaultChartStyle { get; set; }
      public bool                            IsIntraday        { get; set; } = true;
      public bool                            IsTimeBased       { get; set; }
      public virtual bool                    IsRemoveLastBarSupported { get; set; } = true;

      /// <summary>NT8: the bar type creates it itself on the first data point
      /// (`SessionIterator ??= new SessionIterator(bars)`).</summary>
      public NinjaTrader.Data.SessionIterator SessionIterator { get; set; }

      // ---- Overridable NT8 surface ------------------------------------------
      public virtual void ApplyDefaultBasePeriodValue(NinjaTrader.Data.BarsPeriod period) { }
      public virtual void ApplyDefaultValue(NinjaTrader.Data.BarsPeriod period) { }
      public virtual string ChartLabel(DateTime time) => time.ToString("T");
      public virtual int GetInitialLookBackDays(NinjaTrader.Data.BarsPeriod period,
                                                NinjaTrader.Data.TradingHours tradingHours,
                                                int barsBack) => 1;
      public virtual double GetPercentComplete(NinjaTrader.Data.Bars bars, DateTime now) => 0;

      /// <summary>One data point (here: one tick) for bar formation.
      /// The implementation calls AddBar/UpdateBar/RemoveLastBar.</summary>
      protected abstract void OnDataPoint(NinjaTrader.Data.Bars bars,
                                          double open, double high, double low, double close,
                                          DateTime time, long volume, bool isBar,
                                          double bid, double ask);

      /// <summary>Entry point for the shim (OnDataPoint is protected).</summary>
      internal void DispatchDataPoint(NinjaTrader.Data.Bars bars,
                                      double open, double high, double low, double close,
                                      DateTime time, long volume, bool isBar,
                                      double bid, double ask)
         => OnDataPoint(bars, open, high, low, close, time, volume, isBar, bid, ask);

      // ---- Cosmetics from OnStateChange (NT8 property grid) -----------------
      // Headless there is no property grid; the calls only have to compile
      // and must do nothing.
      public PropertyBag Properties { get; } = new PropertyBag();
      public void SetPropertyName(string propertyName, string displayName) { }

      protected void AddBar(NinjaTrader.Data.Bars bars, double open, double high, double low,
                            double close, DateTime time, long volume)
      {
         ((Series<double>)bars.Open) .Append(open);
         ((Series<double>)bars.High) .Append(high);
         ((Series<double>)bars.Low)  .Append(low);
         ((Series<double>)bars.Close).Append(close);
         ((Series<double>)bars.Volume).Append(volume);
         ((Series<DateTime>)bars.Time).Append(time);
         bars.Count       = bars.Open.Count;
         bars.LastBarTime = time;
      }

      protected void AddBar(NinjaTrader.Data.Bars bars, double open, double high, double low,
                            double close, DateTime time, long volume, double bid, double ask)
         => AddBar(bars, open, high, low, close, time, volume);

      /// <summary>NT8 UpdateBar: WIDEN high/low, set close and time,
      /// add up volume. For the derivation see the file header.</summary>
      protected void UpdateBar(NinjaTrader.Data.Bars bars, double high, double low, double close,
                               DateTime time, long volume)
      {
         if (bars.Count <= 0)
            throw new InvalidOperationException(
               "[BARSTYPE] UpdateBar on an empty series - the hosted bars type "
               + "updated before its first AddBar. No fallback.");
         var h = (Series<double>)bars.High;
         var l = (Series<double>)bars.Low;
         if (high > h[0]) h[0] = high;
         if (low  < l[0]) l[0] = low;
         ((Series<double>)bars.Close)[0]  = close;
         ((Series<DateTime>)bars.Time)[0] = time;
         ((Series<double>)bars.Volume)[0]   = bars.Volume[0] + volume;
         bars.LastBarTime = time;
      }

      protected void RemoveLastBar(NinjaTrader.Data.Bars bars)
      {
         if (bars.Count <= 0)
            throw new InvalidOperationException(
               "[BARSTYPE] RemoveLastBar on an empty series. No fallback.");
         bars.OnBeforeRemoveLastBar?.Invoke(bars.Count - 1);
         ((Series<double>)bars.Open) .RemoveLast();
         ((Series<double>)bars.High) .RemoveLast();
         ((Series<double>)bars.Low)  .RemoveLast();
         ((Series<double>)bars.Close).RemoveLast();
         ((Series<double>)bars.Volume).RemoveLast();
         ((Series<DateTime>)bars.Time).RemoveLast();
         bars.Count = bars.Open.Count;
      }
   }

   /// <summary>Replacement for NT8's PropertyDescriptorCollection in the
   /// OnStateChange cosmetics block (`Properties.Remove(Properties.Find(name, true))`).
   /// Headless there is no property grid — Find returns the name back,
   /// Remove drops it. Deliberately without effect, not merely unimplemented.</summary>
   public sealed class PropertyBag
   {
      public object Find(string name, bool ignoreCase) => name;
      public void Remove(object descriptor) { }
   }

   // NT8 provides a `Custom.Resource` container with localized display texts for
   // every NinjaScript namespace. For the indicators it already exists
   // (NinjaScript\Indicators.cs); the bar types resolve `Custom.Resource` through
   // the enclosing namespace NinjaTrader.NinjaScript and therefore need their
   // own. Separate key sets, no overlap.
   namespace Custom
   {
      public static class Resource
      {
         public const string NinjaScriptBarsTypeRenko          = "Renko";
         public const string NinjaScriptBarsTypeRenkoBrickSize = "Brick size";
         public const string DataBarsTypeRenko                 = "Renko {0}";
         public const string NinjaScriptBarsTypeHeikenAshi     = "Heiken Ashi";
         public const string DataBarsTypeHeikenAshi            = "Heiken Ashi {0} {1}";
         public const string NinjaScriptBarsTypeTick           = "Tick";
         public const string DataBarsTypeTick                  = "Tick {0}{1}";
         public const string NinjaScriptBarsTypeRange          = "Range";
         public const string DataBarsTypeRange                 = "Range {0}{1}";
         public const string NinjaScriptBarsTypeVolume         = "Volume";
         public const string DataBarsTypeVolume                = "Volume {0}{1}";
         public const string NinjaScriptBarsTypeSecond         = "Second";
         public const string DataBarsTypeSecond                = "Second {0}";
         public const string NinjaScriptBarsTypeMinute         = "Minute";
         public const string DataBarsTypeMinute                = "Minute {0}{1}";
         public const string NinjaScriptBarsTypeDay            = "Day";
         public const string DataBarsTypeDaily                 = "Daily";
         public const string DataBarsTypeDay                   = "Day {0}";
         public const string NinjaScriptBarsTypeWeek           = "Week";
         public const string DataBarsTypeWeekly                = "Weekly";
         public const string DataBarsTypeWeek                  = "Week {0}";
         public const string NinjaScriptBarsTypeMonth          = "Month";
         public const string DataBarsTypeMonthly               = "Monthly";
         public const string DataBarsTypeMonth                 = "Month {0}";
         public const string BarsPeriodTypeNameYear            = "Year";
         public const string DataBarsTypeYearly                = "Yearly";
         public const string DataBarsTypeYear                  = "Year {0}";
         public const string NinjaScriptBarsTypeKagi           = "Kagi";
         public const string NinjaScriptBarsTypeKagiReversal   = "Reversal";
         public const string NinjaScriptBarsTypeLineBreak      = "Line Break";
         public const string NinjaScriptBarsTypeLineBreakLineBreaks = "Line breaks";
         public const string NinjaScriptBarsTypePointAndFigure = "Point and Figure";
         public const string NinjaScriptBarsTypePointAndFigureBoxSize  = "Box size";
         public const string NinjaScriptBarsTypePointAndFigureReversal = "Reversal";
      }
   }
}

namespace NinjaTrader.Gui.Chart
{
   public enum ChartStyleType
   {
      Box, CandleStick, HiLoBars, LineOnClose, OHLC, OpenClose, PointAndFigure, Custom0,
      KagiLine,
   }
}

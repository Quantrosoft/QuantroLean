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
using System.Xml.Serialization;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript
{
   public partial class NinjaScriptBase
   {
      // With Nt8SecondGrid the book is the one at the end of the dispatched second's batch (GridBid/GridAsk) only while
      // ShimRuntime.Nt8SecondGridStrategyBookAtBatchEnd is true (the default); false = the dispatched event's book.
      public double GetCurrentBid()            => _runtime == null ? (Close?[0] ?? 0)
         : (_runtime.IsBacktest && !_runtime.PlaybackMode)
            ? (_runtime.LastPrice > 0 ? _runtime.LastPrice : (Close?[0] ?? 0))
            : (_runtime.Nt8SecondGrid && _runtime.Nt8SecondGridStrategyBookAtBatchEnd && _runtime.GridBid > 0) ? _runtime.GridBid
            : (_runtime.LastBid > 0 ? _runtime.LastBid : (Close?[0] ?? 0));
      public double GetCurrentAsk()            => _runtime == null ? (Close?[0] ?? 0)
         : (_runtime.IsBacktest && !_runtime.PlaybackMode)
            ? (_runtime.LastPrice > 0 ? _runtime.LastPrice : (Close?[0] ?? 0))
            : (_runtime.Nt8SecondGrid && _runtime.Nt8SecondGridStrategyBookAtBatchEnd && _runtime.GridAsk > 0) ? _runtime.GridAsk
            : (_runtime.LastAsk > 0 ? _runtime.LastAsk : (Close?[0] ?? 0));
      public double GetCurrentBid(int bip)     => GetCurrentBid();
      public double GetCurrentAsk(int bip)     => GetCurrentAsk();

      // ---- Tick / instrument convenience ----
      public double TickSize => Instrument?.MasterInstrument?.TickSize ?? 0.01;
      public bool   IsTickReplay { get; set; }

      public void AddChartIndicator(NinjaTrader.NinjaScript.Indicators.Indicator indicator)
      {
         if (indicator == null) return;
         indicator.Bars = Bars;
         indicator.BarsArray = BarsArray;
         // Some indicator stubs expose their own _runtime back-reference
         // (StandardIndicators.cs path). Wire it so per-tick callbacks work.
         var rt = _runtime;
         if (rt != null)
         {
            var f = indicator.GetType().GetField("_runtime",
               System.Reflection.BindingFlags.Instance |
               System.Reflection.BindingFlags.NonPublic);
            f?.SetValue(indicator, rt);
         }
      }
      public void RemoveDrawObject(string tag) { /* no-op */ }
      public void RemoveDrawObject(object drawingTool) { /* no-op */ }

      public void VendorLicense(int vendorId) { /* no-op */ }

      // ---- Connection (NT8 broker-connectivity helper, no-op in Lean) ----
      // NT8 source uses `Connection.PlaybackConnection.Status == ConnectionStatus.Connected`
      // — so Connection is an OBJECT with sub-properties, not the enum itself.
      public NinjaTrader.Data.ConnectionInfo Connection { get; set; } = new NinjaTrader.Data.ConnectionInfo();

      // ---- Common indicator-display defaults referenced by NT8 source ----
      public bool PaintPriceMarkers       { get; set; }
      public bool DrawHorizontalGridLines { get; set; } = true;
      public bool DrawVerticalGridLines   { get; set; } = true;
      public bool ArePlotsConfigurable    { get; set; } = true;
      public NinjaTrader.Gui.Chart.ScaleJustification ScaleJustification { get; set; } = NinjaTrader.Gui.Chart.ScaleJustification.Right;
      public MaximumBarsLookBack MaximumBarsLookBack { get; set; } = MaximumBarsLookBack.TwoHundredFiftySix;

      public BarsPeriod BarsPeriod => Bars?.BarsPeriod ?? new BarsPeriod();

      // ---- Alerts (no-op stubs) ----
      public void Alert(string id, Priority priority, string text, string wav,
                        int rearmSeconds, System.Windows.Media.Brush bg, System.Windows.Media.Brush fg) { }
   }

   public enum Priority { Low, Medium, High }
   public enum MaximumBarsLookBack { TwoHundredFiftySix, Infinite }
}

namespace NinjaTrader.Gui.Chart
{
   public enum ScaleJustification { Right, Left, Overlay }
}

namespace NinjaTrader.NinjaScript.Strategies
{
   public partial class Strategy
   {
      public Order EnterLongLimit(double limitPrice)                                        => _runtime?.EnterLongLimit(this, 0, (int)DefaultQuantity, limitPrice, "");
      public Order EnterLongLimit(int quantity, double limitPrice, string signalName)       => _runtime?.EnterLongLimit(this, 0, quantity, limitPrice, signalName);
      public Order EnterLongLimit(int barsInProgress, bool isLiveUntilCancelled,
                                  int quantity, double limitPrice, string signalName)      => _runtime?.EnterLongLimit(this, barsInProgress, quantity, limitPrice, signalName);
      public Order EnterShortLimit(double limitPrice)                                       => _runtime?.EnterShortLimit(this, 0, (int)DefaultQuantity, limitPrice, "");
      public Order EnterShortLimit(int quantity, double limitPrice, string signalName)      => _runtime?.EnterShortLimit(this, 0, quantity, limitPrice, signalName);
      public Order EnterShortLimit(int barsInProgress, bool isLiveUntilCancelled,
                                   int quantity, double limitPrice, string signalName)     => _runtime?.EnterShortLimit(this, barsInProgress, quantity, limitPrice, signalName);

      public Order EnterLongStopMarket (int quantity, double stopPrice, string signalName)  => _runtime?.EnterLong(this, 0, quantity, signalName);
      public Order EnterShortStopMarket(int quantity, double stopPrice, string signalName)  => _runtime?.EnterShort(this, 0, quantity, signalName);

      // Note: `Serialize` is NOT a method here — NT8 uses `Serialize` as a
      // static helper class (NinjaTrader.NinjaScript.Serialize.BrushToString,
      // .StringToBrush, etc.). Defined in NinjaScriptExtensions.cs as a static class.

      // ---- Performance metrics container (NT8 strategy backtest stats) ----
      public NinjaTrader.NinjaScript.SystemPerformance SystemPerformance { get; }
         = new NinjaTrader.NinjaScript.SystemPerformance();
   }
}

namespace NinjaTrader.NinjaScript
{
   // NT8 static helper: `Serialize.BrushToString(brush)` / `.StringToBrush(s)`,
   // used in `*Serializable` proxy properties on strategies/indicators to
   // round-trip WPF brushes through XML.
   public static class Serialize
   {
      public static string BrushToString(System.Windows.Media.Brush brush) => "";
      public static System.Windows.Media.Brush StringToBrush(string s)
         => System.Windows.Media.Brushes.Transparent;
      public static string StrokeToString(NinjaTrader.Gui.Stroke s) => "";
      public static NinjaTrader.Gui.Stroke StringToStroke(string s) => new NinjaTrader.Gui.Stroke();
   }
}

namespace NinjaTrader.NinjaScript
{
   public class SystemPerformance
   {
      // Populated one level deep with real collections - see the rationale at
      // TradeCollection.WinningTrades.
      private static TradeCollection Mit() => new TradeCollection
      {
         WinningTrades = new TradeCollection(),
         LosingTrades  = new TradeCollection(),
      };

      public TradeCollection AllTrades      { get; set; } = Mit();
      public TradeCollection LongTrades     { get; set; } = Mit();
      public TradeCollection ShortTrades    { get; set; } = Mit();
      public TradeCollection RealtimeTrades { get; set; } = Mit();
   }

   public class TradeCollection : List<Trade>
   {
      public TradePerformance TradesPerformance { get; set; } = new TradePerformance();

      public int TradesCount { get; set; }

      public TradeCollection WinningTrades { get; set; } = null;
      public TradeCollection LosingTrades  { get; set; } = null;
   }

   public class Trade
   {
      public DateTime EntryTime  { get; set; }
      public DateTime ExitTime   { get; set; }
      public double   EntryPrice { get; set; }
      public double   ExitPrice  { get; set; }
      public double   ProfitPoints   { get; set; }
      public double   ProfitCurrency { get; set; }
      public int      Quantity { get; set; }

      // NT8 trade-entry/exit aggregate views (used in performance summary).
      public TradeEntry Entry { get; set; } = new TradeEntry();
      public TradeEntry Exit  { get; set; } = new TradeEntry();
      public NinjaTrader.Cbi.Commission Commission { get; set; } = new NinjaTrader.Cbi.Commission();

      public double ProfitTicks { get; set; }
      public int    TradeNumber { get; set; }
   }

   public class TradeEntry
   {
      public DateTime Time     { get; set; }
      public double   Price    { get; set; }
      public int      Quantity { get; set; }
      public string   Name     { get; set; }
      public NinjaTrader.Cbi.MarketPosition MarketPosition { get; set; }
   }

   public class TradePerformance
   {
      public TradePerformanceValues Currency { get; set; } = new TradePerformanceValues();
      public TradePerformanceValues Points   { get; set; } = new TradePerformanceValues();
      public TradePerformanceValues Ticks    { get; set; } = new TradePerformanceValues();
      public TradePerformanceValues Percent  { get; set; } = new TradePerformanceValues();
      public TradePerformanceValues Pips     { get; set; } = new TradePerformanceValues();

      public double GrossProfit { get; set; }
      public double GrossLoss   { get; set; }
      public double NetProfit   { get; set; }
      public int    TradesCount { get; set; }
   }

   public class TradePerformanceValues
   {
      public double CumProfit    { get; set; }
      public double Profit       { get; set; }
      public double Loss         { get; set; }
      public double MaxDrawDown  { get; set; }
      public double Drawdown     { get; set; }
      public double AverageProfit{ get; set; }
      public double StandardDev  { get; set; }
   }
}

namespace NinjaTrader.Cbi
{
   public partial class Order
   {
      public bool IsLong   => OrderAction == OrderAction.Buy || OrderAction == OrderAction.BuyToCover;
      public bool IsShort  => OrderAction == OrderAction.Sell || OrderAction == OrderAction.SellShort;
      public bool IsMarket => OrderType   == OrderType.Market;
      public bool IsLimit  => OrderType   == OrderType.Limit;
      public bool IsStop   => OrderType   == OrderType.Stop;
   }

   public partial class Execution
   {
      // NT8 sets these flags during OnExecutionUpdate (entry vs exit, strategy-
      // vs hand-placed). Stub-default to false.
      public bool IsEntry         { get; set; }
      public bool IsExit          { get; set; }
      public bool IsEntryStrategy { get; set; }
      public bool IsExitStrategy  { get; set; }
   }

   public partial class Instrument
   {
      public static Instrument HostDefault { get; set; }

      public static Instrument GetInstrument(string name)
      {
         var host = HostDefault;
         if (host?.MasterInstrument == null)
            throw new System.InvalidOperationException(
               "[SHIM-HOSTING] Instrument.GetInstrument('" + name + "') before a host "
               + "published ShimRuntime.DefaultInstrument. Returning a blank contract "
               + "here would silently hand out TickSize=0.01/PointValue=1. "
               + "Set DefaultInstrument first.");

         // The shim hosts exactly ONE contract (see Hosting/ShimInstrumentSpec.cs),
         // so every name resolves to it. The MasterInstrument is SHARED, not copied:
         // one source of truth for TickSize/PointValue.
         return new Instrument
         {
            FullName              = name,
            MasterInstrument_Name = host.MasterInstrument_Name,
            MasterInstrument      = host.MasterInstrument,
            Exchange              = host.Exchange,
         };
      }

      public static Instrument GetInstrument(string name, string tradingHoursName) =>
         GetInstrument(name);
      public static Instrument GetInstrument(string name, bool create) =>
         GetInstrument(name);
   }
}

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

using System.ComponentModel;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript
{
   public abstract class IndicatorBase : NinjaScriptBase
   {
      public NinjaTrader.NinjaScript.Indicators.PlotCollection Plots { get; }
         = new NinjaTrader.NinjaScript.Indicators.PlotCollection();

      /// <summary>NT8: every call appends a plot and thereby also determines
      /// `Values.Length` (the shim picks that up in
      /// `ShimRuntime.RegisterHostedIndicator`).</summary>
      protected void AddPlot(System.Windows.Media.Brush brush, string name)
         => Plots.Add(new NinjaTrader.NinjaScript.Indicators.Plot
            { Name = name ?? "", Brush = brush });

      protected void AddPlot(NinjaTrader.Gui.Stroke stroke, PlotStyle style, string name)
         => Plots.Add(new NinjaTrader.NinjaScript.Indicators.Plot
            { Name = name ?? "", Brush = stroke?.Brush, PlotStyle = style,
              Width = stroke != null ? (int)stroke.Width : 1 });
   }

   public abstract class StrategyBase : NinjaScriptBase
   {
      // Account is on StrategyBase (not Strategy) so a strategy subclass can call
      // `base.Account` — the C# `base` keyword steps ONE level up the
      // inheritance chain (Strategy → StrategyRenderBase → StrategyBase).
      public NinjaTrader.Cbi.Account Account { get; protected set; } = new NinjaTrader.Cbi.Account();

      // Order/execution/position lifecycle hooks live on StrategyBase so
      // the partial `Strategy` class (extended by strategy sources)
      // can `override` them — a partial cannot override its own virtual.

      protected virtual void OnExecutionUpdate(
         NinjaTrader.Cbi.Execution execution, string executionId, double price,
         int quantity, NinjaTrader.Cbi.MarketPosition marketPosition,
         string orderId, System.DateTime time) { }

      protected virtual void OnOrderUpdate(
         NinjaTrader.Cbi.Order order, double limitPrice, double stopPrice,
         int quantity, int filled, double averageFillPrice,
         NinjaTrader.Cbi.OrderState orderState, System.DateTime time,
         NinjaTrader.Cbi.ErrorCode error, string nativeError) { }

      protected virtual void OnPositionUpdate(
         NinjaTrader.Cbi.Position position, double averagePrice, int quantity,
         NinjaTrader.Cbi.MarketPosition marketPosition) { }

      public void RaiseExecutionUpdate(
         NinjaTrader.Cbi.Execution execution, string executionId, double price,
         int quantity, NinjaTrader.Cbi.MarketPosition marketPosition,
         string orderId, System.DateTime time)
         => OnExecutionUpdate(execution, executionId, price, quantity,
                              marketPosition, orderId, time);

      public void RaiseOrderUpdate(
         NinjaTrader.Cbi.Order order, double limitPrice, double stopPrice,
         int quantity, int filled, double averageFillPrice,
         NinjaTrader.Cbi.OrderState orderState, System.DateTime time,
         NinjaTrader.Cbi.ErrorCode error, string nativeError)
         => OnOrderUpdate(order, limitPrice, stopPrice, quantity, filled,
                          averageFillPrice, orderState, time, error, nativeError);

      public void RaisePositionUpdate(
         NinjaTrader.Cbi.Position position, double averagePrice, int quantity,
         NinjaTrader.Cbi.MarketPosition marketPosition)
         => OnPositionUpdate(position, averagePrice, quantity, marketPosition);
   }

   public abstract class MarketAnalyzerColumnBase : NinjaScriptBase { }
   public abstract class SuperDomColumnBase       : NinjaScriptBase { }

   // ---- TypeConverters used by NT8 designer-time serialization ------------
   public class IndicatorBaseConverter : TypeConverter { }
   public class StrategyBaseConverter  : TypeConverter { }
   public class NinjaScriptConverter   : TypeConverter { }
}

namespace NinjaTrader.Gui.NinjaScript
{

   public abstract class IndicatorRenderBase : NinjaTrader.NinjaScript.IndicatorBase
   {
      public virtual void OnRenderTargetChanged() { }
      protected virtual void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl,
                                      NinjaTrader.Gui.Chart.ChartScale chartScale) { }

      public NinjaTrader.Gui.Chart.ChartBars   ChartBars   { get; set; }
      public NinjaTrader.Gui.Chart.ChartPanel  ChartPanel  { get; set; }
      public SharpDX.Direct2D1.RenderTarget    RenderTarget { get; set; }
   }

   public abstract class StrategyRenderBase : NinjaTrader.NinjaScript.StrategyBase
   {
      public virtual void OnRenderTargetChanged() { }
      protected virtual void OnRender(NinjaTrader.Gui.Chart.ChartControl chartControl,
                                      NinjaTrader.Gui.Chart.ChartScale chartScale) { }

      public NinjaTrader.Gui.Chart.ChartBars   ChartBars   { get; set; }
      public NinjaTrader.Gui.Chart.ChartPanel  ChartPanel  { get; set; }
      public SharpDX.Direct2D1.RenderTarget    RenderTarget { get; set; }
   }

   public abstract class MarketAnalyzerColumnRenderBase : NinjaTrader.NinjaScript.MarketAnalyzerColumnBase { }
   public abstract class SuperDomColumnRenderBase       : NinjaTrader.NinjaScript.SuperDomColumnBase       { }
}

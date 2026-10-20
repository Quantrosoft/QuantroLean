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

namespace NinjaTrader.NinjaScript
{
   public partial class NinjaScriptBase
   {
      public void SetState(State state) => DispatchStateChange(state);

      public void RemoveDrawObjects() { }
      public System.Windows.Media.Brush BackBrush { get; set; }

      public void TriggerCustomEvent(System.Action<object> customEvent, object state)
         => customEvent?.Invoke(state);
      public void TriggerCustomEvent(System.Action<object> customEvent, int barsSeriesIndex, object state)
         => customEvent?.Invoke(state);
   }
}

namespace NinjaTrader.NinjaScript.Strategies
{
   public partial class Strategy
   {
      public bool IncludeTradeHistoryInBacktest { get; set; } = true;

      public NinjaTrader.Cbi.Order GetRealtimeOrder(NinjaTrader.Cbi.Order historicalOrder)
         => historicalOrder;

   }
}

namespace NinjaTrader.Cbi
{
   public partial class Order
   {
      // NT8 (reflection): bool IsBacktestOrder, read-only - true for orders
      // created in State.Historical that have a realtime counterpart. The shim
      // never splits an order into a historical and a realtime object.
      public bool IsBacktestOrder => false;
   }
}

namespace NinjaTrader.Gui.Chart
{
   public partial class ChartControl : System.Windows.DependencyObject { }
}

namespace SharpDX.Direct2D1
{
   // Gradient types used in OnRender. Compile-only
   // stubs shaped exactly like the call sites: GradientStop[] with Position/
   // Color, GradientStopCollection(RenderTarget, GradientStop[]) in a `using`,
   // LinearGradientBrush(RenderTarget, LinearGradientBrushProperties,
   // GradientStopCollection) in a `using`, RenderTarget.FillRectangle(rect,
   // brush) (FillRectangle already exists in GraphicsStubs.cs). OnRender never
   // runs headless (RenderTarget stays null).
   public struct GradientStop
   {
      public float Position;
      public SharpDX.Color4 Color;
   }

   public sealed class GradientStopCollection : System.IDisposable
   {
      public GradientStopCollection(RenderTarget rt, GradientStop[] stops) { }
      public void Dispose() { }
   }

   public struct LinearGradientBrushProperties
   {
      public SharpDX.Vector2 StartPoint;
      public SharpDX.Vector2 EndPoint;
   }

   public sealed class LinearGradientBrush : Brush, System.IDisposable
   {
      public LinearGradientBrush(RenderTarget rt, LinearGradientBrushProperties props,
                                 GradientStopCollection stops) { }
      public void Dispose() { }
   }
}

namespace NinjaTrader.Gui.Chart
{
   public enum ChartExecutionStyle { DoNotPlot, MarkersOnly, TextAndMarker }

   public class BarsProperties
   {
      public ChartExecutionStyle PlotExecutions { get; set; } = ChartExecutionStyle.TextAndMarker;
   }

   public partial class ChartBars
   {
      public BarsProperties Properties { get; set; } = new BarsProperties();
   }
}

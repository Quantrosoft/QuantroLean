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
using System.Windows.Media;

namespace NinjaTrader.Gui
{
   public class Stroke
   {
      public System.Windows.Media.Brush Brush { get; set; }
      public DashStyleHelper DashStyle { get; set; }
      public double Width { get; set; } = 1;
      public float Opacity { get; set; } = 100;
      public bool IsColorDisabled { get; set; }
      public Stroke() { }
      public Stroke(System.Windows.Media.Brush brush) { Brush = brush; }
      public Stroke(System.Windows.Media.Brush brush, double width) { Brush = brush; Width = width; }
      public Stroke(System.Windows.Media.Brush brush, DashStyleHelper dashStyle, double width) { Brush = brush; DashStyle = dashStyle; Width = width; }
      public Stroke(System.Windows.Media.Brush brush, DashStyleHelper dashStyle, double width, float opacity) { Brush = brush; DashStyle = dashStyle; Width = width; Opacity = opacity; }
      public SharpDX.Direct2D1.Brush BrushDX { get; set; }
      public SharpDX.Direct2D1.StrokeStyle StrokeStyle { get; set; }
      public void RenderTarget(SharpDX.Direct2D1.RenderTarget rt) { }
      public Stroke Clone() => new Stroke(Brush, DashStyle, Width, Opacity);
      // NT8 uses `myStroke.RenderTarget = rt;` as a property setter to refresh
      // GPU-side resources. Stub as a property to satisfy compile-time usage.
      public SharpDX.Direct2D1.RenderTarget RenderTargetProp { get; set; }

      // NT8 sources pass `Stroke` where the method expects `Brush` — provide
      // an implicit conversion that pulls the inner Brush. Lossy (drops
      // DashStyle/Width) but only matters for graphics which is no-op here.
      public static implicit operator System.Windows.Media.Brush(Stroke s) => s?.Brush;
   }

   public enum DashStyleHelper { Solid, Dash, Dot, DashDot, DashDotDot }

   public static class ColorBrushes
   {
      public static System.Windows.Media.Brush ConvertARGBToBrush(int argb)
         => System.Windows.Media.Brushes.Black;
      public static System.Windows.Media.Color ConvertHexToColor(string hex)
         => System.Windows.Media.Colors.Black;
      public static System.Windows.Media.Brush ToSolidColorBrush(this System.Windows.Media.Brush brush)
         => brush;
   }
}

namespace NinjaTrader.Gui.Chart
{
   public partial class ChartControl
   {
      public double Width { get; set; }
      public double Height { get; set; }
      public NinjaTrader.Gui.Chart.ChartProperties Properties { get; set; } = new ChartProperties();
      public System.Collections.ObjectModel.ObservableCollection<NinjaTrader.Gui.Chart.ChartBars> BarsArray { get; set; }
         = new System.Collections.ObjectModel.ObservableCollection<NinjaTrader.Gui.Chart.ChartBars>();
      public List<NinjaTrader.Gui.Chart.ChartBars> ChartBarsArray { get; } = new List<ChartBars>();

      public int GetXByBarIndex(NinjaTrader.Gui.Chart.ChartBars bars, int barIndex) => 0;
      public double GetXByTime(DateTime time) => 0;
      public DateTime GetTimeByX(double x) => DateTime.MinValue;
      public int GetSlotIndexByX(double x) => 0;
      public int GetBarPaintWidth(NinjaTrader.Gui.Chart.ChartBars bars) => 1;
      public List<NinjaTrader.Gui.Chart.ChartPanel> ChartPanels { get; } = new List<ChartPanel>();

      public DateTime FirstTimePainted { get; set; }
      public DateTime LastTimePainted  { get; set; }
      public int      LastSlotPainted  { get; set; }
      public System.Windows.Threading.Dispatcher Dispatcher
         => System.Windows.Threading.Dispatcher.CurrentDispatcher;
      public void InvalidateVisual() { }
   }

   public partial class ChartScale
   {
      public double Width { get; set; }
      public double Height { get; set; }
      public double MinValue { get; set; }
      public double MaxValue { get; set; }
      public double GetValueAt(double y) => 0;

      // GetYByValue already in Gui.cs; don't redeclare here.
   }

   public partial class ChartBars
   {
      public NinjaTrader.Data.Bars Bars { get; set; }
      public int FromIndex { get; set; }
      public int ToIndex   { get; set; }
      public int Count { get; set; }
      public DateTime GetTimeByBarIdx(int idx) => DateTime.MinValue;
      public int GetBarIdxByTime(DateTime time) => 0;
   }

   public partial class ChartPanel
   {
      public double X { get; set; }
      public double Y { get; set; }
      public double W { get; set; }
      public double H { get; set; }
      public int PanelIndex { get; set; }
      public double Width => W;
      public double Height => H;
   }

   public class ChartProperties
   {
      public System.Windows.Media.Brush ChartBackground { get; set; }
      public System.Windows.Media.Brush ChartText { get; set; }
   }
}

namespace NinjaTrader.Core
{
   // Round out NT8 Core.Globals — already partly in Core.cs, additions here.
   public static partial class Globals
   {
      public static double ToCryptocurrencyVolume(long volume)
         => throw new NotSupportedException(
              "[SHIM] Globals.ToCryptocurrencyVolume is not implemented. "
            + "NT8's original exists only in binary form in NinjaTrader.Core.dll and "
            + "cannot be derived; a guessed body would silently compute wrong numbers. "
            + "It is called only for InstrumentType == CryptoCurrency "
            + "(@VolumeUpDown.cs:45/51) - for futures/CFDs that must never happen. "
            + $"Volume was {volume}.");

      public static System.Globalization.CultureInfo MarketDataCulture { get; set; } = System.Globalization.CultureInfo.InvariantCulture;
      public static int  AssemblyRegister_Get(string name) => 0;
      public static bool IsInOptimizer { get; set; }
      public static bool IsConvertedToDecimal { get; set; } = true;
      public static string DefaultDataDirectory { get; set; } = "";
      // NT8 graphics indicators use a shared DirectWriteFactory singleton
      // for TextFormat construction.
      public static SharpDX.DirectWrite.Factory DirectWriteFactory { get; } = new SharpDX.DirectWrite.Factory();
      public static SharpDX.Direct2D1.Factory  D2DFactory          { get; } = new SharpDX.Direct2D1.Factory();
   }
}

// ---- SharpDX completions ------------------------------------------------
namespace SharpDX.Direct2D1
{
   public partial class RenderTarget
   {
      public SharpDX.Vector2 Size { get; set; }
      public float DotsPerInch { get; set; } = 96f;
      public AntialiasMode AntialiasMode { get; set; }
      public void DrawLine(SharpDX.Vector2 from, SharpDX.Vector2 to, Brush brush, float strokeWidth) { }
      public void DrawLine(SharpDX.Vector2 from, SharpDX.Vector2 to, Brush brush, float strokeWidth, StrokeStyle ss) { }
      public void DrawRectangle(SharpDX.RectangleF r, Brush brush, float strokeWidth) { }
      public void FillRectangle(SharpDX.RectangleF r, Brush brush) { }
      public void DrawEllipse(object e, Brush brush, float strokeWidth) { }
      public void FillEllipse(object e, Brush brush) { }
      public void DrawText(string text, DirectWrite.TextFormat fmt, SharpDX.RectangleF rect, Brush brush) { }
      public void DrawTextLayout(SharpDX.Vector2 origin, DirectWrite.TextLayout layout, Brush brush) { }
      public void DrawTextLayout(SharpDX.Vector2 origin, DirectWrite.TextLayout layout, Brush brush, DrawTextOptions opts) { }

      // Geometry helpers for hatched fills and arcs.
      public void DrawRoundedRectangle(object rect, Brush brush, float w) { }
      public void FillRoundedRectangle(object rect, Brush brush) { }
      public void DrawArc(object arc, Brush brush, float w) { }
      public void FillGeometry(object geom, Brush brush) { }
   }

   public enum DrawTextOptions { None, Clip, NoSnap }

   public partial class SolidColorBrush
   {
      // Opacity inherited from Brush base.
      public SharpDX.Color4 Color { get; set; }
   }

   public partial class Brush
   {
      public float Opacity { get; set; } = 1f;
      public void Dispose() { }
   }

   public partial class StrokeStyle
   {
      public StrokeStyle() { }
      public StrokeStyle(SharpDX.Direct2D1.Factory f, StrokeStyleProperties props) { }
      public void Dispose() { }
   }

   public class Factory
   {
      public void Dispose() { }
   }

   public struct StrokeStyleProperties
   {
      public DashStyle DashStyle;
      public CapStyle DashCap;
      public CapStyle StartCap;
      public CapStyle EndCap;
   }
}

namespace SharpDX.DirectWrite
{
   public partial class TextFormat
   {
      public TextFormat(Factory f, string family, float size) { }
      public TextAlignment TextAlignment { get; set; }
      public ParagraphAlignment ParagraphAlignment { get; set; }
      public void Dispose() { }
   }

   public class TextLayout
   {
      public TextLayout() { }
      public TextLayout(Factory f, string text, TextFormat fmt, float maxWidth, float maxHeight) { }
      public TextMetrics Metrics { get; set; }
      public void Dispose() { }
      public float MaxWidth  { get; set; }
      public float MaxHeight { get; set; }
   }

   public struct TextMetrics
   {
      public float Width;
      public float Height;
      public float WidthIncludingTrailingWhitespace;
   }
}

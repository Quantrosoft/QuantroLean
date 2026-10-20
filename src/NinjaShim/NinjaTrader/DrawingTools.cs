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

// Shim — NinjaTrader.NinjaScript.DrawingTools namespace.
// `Draw.TextFixed`, `Draw.Line`, `Draw.HorizontalLine`, etc. All no-op stubs
// returning a dummy object so the strategy can reference them (e.g., remove
// or recolor) without crashing.

using System;
using System.Windows.Media;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.DrawingTools
{
   public class DrawingTool
   {
      public string Tag { get; set; }
      public bool IsLocked { get; set; }
      public bool IsAutoScale { get; set; }
   }

   public class Text : DrawingTool
   {
      public string DisplayText { get; set; }
      public Brush TextBrush { get; set; }
   }

   public class Line : DrawingTool { }
   public class HorizontalLine : DrawingTool { }
   public class VerticalLine : DrawingTool { }
   public class TextFixed : DrawingTool { }
   public class Rectangle : DrawingTool { }
   public class Dot : DrawingTool { }
   public class Square : DrawingTool { }
   public class Ray : DrawingTool { }
   public class Arrow : DrawingTool { }
   public class ArrowUp : DrawingTool { }
   public class ArrowDown : DrawingTool { }

   public enum TextPosition { TopLeft, TopRight, Center, BottomLeft, BottomRight }

   public static class Draw
   {
      public static Text Text(NinjaScriptBase owner, string tag, string text, int barsAgo, double y, Brush brush)
         => new Text { Tag = tag, DisplayText = text, TextBrush = brush };

      public static TextFixed TextFixed(NinjaScriptBase owner, string tag, string text, TextPosition position)
         => new TextFixed { Tag = tag };

      public static TextFixed TextFixed(NinjaScriptBase owner, string tag, string text, TextPosition position,
                                        Brush textBrush, NinjaTrader.Gui.Tools.SimpleFont font,
                                        Brush outlineBrush, Brush areaBrush, int areaOpacity)
         => new TextFixed { Tag = tag };

      public static Line Line(NinjaScriptBase owner, string tag, int startBarsAgo, double startY,
                              int endBarsAgo, double endY, Brush brush)
         => new Line { Tag = tag };

      public static Line Line(NinjaScriptBase owner, string tag, bool isAutoScale,
                              int startBarsAgo, double startY, int endBarsAgo, double endY,
                              Brush brush, NinjaTrader.Gui.DashStyleHelper dashStyle, int width)
         => new Line { Tag = tag, IsAutoScale = isAutoScale };

      public static HorizontalLine HorizontalLine(NinjaScriptBase owner, string tag, double y, Brush brush)
         => new HorizontalLine { Tag = tag };

      public static HorizontalLine HorizontalLine(NinjaScriptBase owner, string tag,
         bool isAutoScale, double y, Brush brush, NinjaTrader.Gui.DashStyleHelper dashStyle, int width)
         => new HorizontalLine { Tag = tag };

      public static VerticalLine VerticalLine(NinjaScriptBase owner, string tag, int barsAgo, Brush brush)
         => new VerticalLine { Tag = tag };

      public static Rectangle Rectangle(NinjaScriptBase owner, string tag, int startBarsAgo, double startY,
                                        int endBarsAgo, double endY, Brush brush)
         => new Rectangle { Tag = tag };

      public static Dot Dot(NinjaScriptBase owner, string tag, bool isAutoScale, int barsAgo, double y, Brush brush)
         => new Dot { Tag = tag, IsAutoScale = isAutoScale };

      public static ArrowUp ArrowUp(NinjaScriptBase owner, string tag, bool isAutoScale, int barsAgo, double y, Brush brush)
         => new ArrowUp { Tag = tag };

      public static ArrowDown ArrowDown(NinjaScriptBase owner, string tag, bool isAutoScale, int barsAgo, double y, Brush brush)
         => new ArrowDown { Tag = tag };

      public static void Remove(NinjaScriptBase owner, string tag) { /* no-op */ }
      public static void RemoveDrawObjects(NinjaScriptBase owner) { /* no-op */ }

      // Region overlays (shaded backgrounds).
      public static Region Region(NinjaScriptBase owner, string tag, int startBarsAgo, double startY,
                                  int endBarsAgo, double endY, Brush outlineBrush, Brush areaBrush, int areaOpacity)
         => new Region { Tag = tag };

      // Region overload bound to a series:
      // Draw.Region(this, tag, startBarsAgo, endBarsAgo, seriesAsTopBound, y2, areaBrush, outlineOpacity, areaOpacity)
      public static Region Region(NinjaScriptBase owner, string tag, int startBarsAgo, int endBarsAgo,
                                  NinjaTrader.NinjaScript.ISeries<double> series1,
                                  double y2, Brush areaBrush, int outlineOpacity, int areaOpacity)
         => new Region { Tag = tag };

      public static Region Region(NinjaScriptBase owner, string tag, int startBarsAgo, int endBarsAgo,
                                  NinjaTrader.NinjaScript.ISeries<double> series1,
                                  NinjaTrader.NinjaScript.ISeries<double> series2,
                                  Brush outlineBrush, Brush areaBrush, int areaOpacity)
         => new Region { Tag = tag };

      // Larger Text overload (font, alignment, padding, opacity, draw-on-pricepanel).
      public static Text Text(NinjaScriptBase owner, string tag, bool isAutoScale, string text,
                              int barsAgo, double y, int yPixelOffset,
                              Brush textBrush, NinjaTrader.Gui.Tools.SimpleFont font,
                              System.Windows.TextAlignment textAlignment,
                              Brush outlineBrush, Brush areaBrush, int areaOpacity)
         => new Text { Tag = tag, DisplayText = text, TextBrush = textBrush };

      // Larger Rectangle overload (auto-scale, anchor times, fill colors).
      public static Rectangle Rectangle(NinjaScriptBase owner, string tag, bool isAutoScale,
                                        System.DateTime startTime, double startY,
                                        System.DateTime endTime,   double endY,
                                        Brush outlineBrush, Brush areaBrush, int areaOpacity, bool drawOnPricePanel)
         => new Rectangle { Tag = tag };

      // Rectangle overload by bars-ago.
      public static Rectangle Rectangle(NinjaScriptBase owner, string tag, bool isAutoScale,
                                        int startBarsAgo, double startY,
                                        int endBarsAgo, double endY,
                                        Brush outlineBrush, Brush areaBrush, int areaOpacity, bool drawOnPricePanel)
         => new Rectangle { Tag = tag };

      public static TriangleUp TriangleUp(NinjaScriptBase owner, string tag, bool isAutoScale,
                                          int barsAgo, double y, Brush brush)
         => new TriangleUp { Tag = tag };
      public static TriangleUp TriangleUp(NinjaScriptBase owner, string tag, bool isAutoScale,
                                          int barsAgo, double y, Brush brush, bool drawOnPricePanel)
         => new TriangleUp { Tag = tag };

      public static TriangleDown TriangleDown(NinjaScriptBase owner, string tag, bool isAutoScale,
                                              int barsAgo, double y, Brush brush)
         => new TriangleDown { Tag = tag };
      public static TriangleDown TriangleDown(NinjaScriptBase owner, string tag, bool isAutoScale,
                                              int barsAgo, double y, Brush brush, bool drawOnPricePanel)
         => new TriangleDown { Tag = tag };

      public static Diamond Diamond(NinjaScriptBase owner, string tag, bool isAutoScale,
                                    int barsAgo, double y, Brush brush)
         => new Diamond { Tag = tag };
   }

   public class Region : DrawingTool { }
   public class TriangleUp   : DrawingTool { }
   public class TriangleDown : DrawingTool { }
   public class Diamond      : DrawingTool { }
}

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

namespace NinjaTrader.NinjaScript
{
   public partial class NinjaScriptBase
   {
      // `BarBrushes[0] = brush;` - per-bar candle colouring.
      private BrushSeries _barBrushes;
      public BrushSeries BarBrushes => _barBrushes ?? (_barBrushes = new BrushSeries());
   }
}

namespace NinjaTrader.Gui
{
   public static class Serialize
   {
      public static string BrushToString(System.Windows.Media.Brush brush)
         => brush == null ? "" : brush.ToString();

      public static System.Windows.Media.Brush StringToBrush(string value)
      {
         if (string.IsNullOrEmpty(value))
            return null;
         var brush = new System.Windows.Media.BrushConverter().ConvertFromString(value) as System.Windows.Media.Brush;
         if (brush == null)
            throw new FormatException($"[SHIM] Serialize.StringToBrush: '{value}' is not a brush. No fallback.");
         return brush;
      }
   }
}

namespace SharpDX.Direct2D1
{
   public partial class Brush : IDisposable { }

   public struct Ellipse
   {
      public SharpDX.Vector2 Point;
      public float RadiusX, RadiusY;
      public Ellipse(SharpDX.Vector2 center, float radiusX, float radiusY)
      { Point = center; RadiusX = radiusX; RadiusY = radiusY; }
   }
}

namespace SharpDX.DirectWrite
{
   public partial class TextFormat : IDisposable { }
}

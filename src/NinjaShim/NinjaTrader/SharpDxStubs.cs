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

namespace SharpDX
{
   public struct Color4
   {
      public float R, G, B, A;
      public Color4(float r, float g, float b, float a) { R = r; G = g; B = b; A = a; }
      public Color4(float rgba) { R = G = B = A = rgba; }
   }

   public struct Color
   {
      public byte R, G, B, A;
      public Color(byte r, byte g, byte b, byte a) { R = r; G = g; B = b; A = a; }
      public Color(int r, int g, int b, int a) { R = (byte)r; G = (byte)g; B = (byte)b; A = (byte)a; }
      public static readonly Color White = new Color(255, 255, 255, 255);
      public static implicit operator Color4(Color c)
         => new Color4(c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f);
   }

   public struct Vector2
   {
      public float X, Y;
      public Vector2(float x, float y) { X = x; Y = y; }
   }

   public struct RectangleF
   {
      public float X, Y, Width, Height;
      public RectangleF(float x, float y, float width, float height)
      { X = x; Y = y; Width = width; Height = height; }
   }
}

namespace SharpDX.Direct2D1
{
   public partial class RenderTarget { }
   public partial class SolidColorBrush : Brush
   {
      public SolidColorBrush() { }
      public SolidColorBrush(RenderTarget rt, SharpDX.Color4 color) { }
   }
   public partial class Brush { }
   public partial class StrokeStyle { }
   public enum AntialiasMode { Aliased, PerPrimitive }
   public enum CapStyle { Flat, Square, Round, Triangle }
   public enum DashStyle { Solid, Dash, Dot, DashDot, DashDotDot, Custom }
}

namespace SharpDX.DirectWrite
{
   public partial class TextFormat
   {
      public TextFormat() { }
   }
   public partial class Factory { }
   public enum FontWeight { Thin, ExtraLight, Light, Normal, Medium, SemiBold, Bold, ExtraBold, Black }
   public enum FontStyle { Normal, Oblique, Italic }
   public enum FontStretch { Undefined, UltraCondensed, ExtraCondensed, Condensed, SemiCondensed, Normal, SemiExpanded, Expanded, ExtraExpanded, UltraExpanded }
   public enum ParagraphAlignment { Near, Far, Center }
   public enum TextAlignment { Leading, Trailing, Center, Justified }
}

namespace NinjaTrader.Core.FloatingPoint
{
   public static class FloatingPointExtensions
   {
      public static int ApproxCompare(this double a, double b)
      {
         const double eps = 1e-9;
         if (a - b > eps) return 1;
         if (b - a > eps) return -1;
         return 0;
      }
   }
}

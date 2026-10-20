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

// Shim — NinjaTrader.Gui* namespaces (chart, tools, drawing).
// All no-ops in Lean (headless backtest). Stubs exist so that NT8 source code
// referencing them compiles without modification.

using System;
using System.Windows.Media;

namespace NinjaTrader.Gui
{
   public static class BrushHelpers
   {
      public static Brush GetSolidBrush(Color color) => null;
   }

   [AttributeUsage(AttributeTargets.All, AllowMultiple = false)]
   public class PropertyEditorAttribute : Attribute
   {
      public string EditorTypeName { get; }
      public PropertyEditorAttribute(string editorTypeName) { EditorTypeName = editorTypeName; }
      public PropertyEditorAttribute(string editorTypeName, string param) { EditorTypeName = editorTypeName; }
   }
}

namespace NinjaTrader.Gui.Tools
{
   public class SimpleFont
   {
      public string Family { get; set; } = "Arial";
      public int Size { get; set; } = 10;
      public bool Bold { get; set; }
      public bool Italic { get; set; }
      public SimpleFont() { }
      public SimpleFont(string family, int size) { Family = family; Size = size; }
   }

   public class AccountSelector : System.Windows.Controls.ComboBox
   {
      public NinjaTrader.Cbi.Account SelectedAccount { get; set; }
      public string DesiredAccount { get; set; }
   }
}

namespace NinjaTrader.Gui.Chart
{
   public class Chart : System.Windows.Window
   {
      public object FindFirst(string name) => null;
      public System.Windows.Controls.TabControl MainTabControl => null;
   }

   public class ChartTab
   {
      public ChartControl ChartControl => null;
   }

   public class ChartTrader
   {
      public object Content => null;
   }

   public partial class ChartControl
   {
      public double CanvasLeft { get; set; }
      public double CanvasRight { get; set; }
      public double CanvasWidth => CanvasRight - CanvasLeft;
      public System.Windows.DependencyObject Parent => null;
   }

   public partial class ChartScale
   {
      public int GetYByValue(double value) => 0;
      public double GetValueByY(double y) => 0;
   }

   public partial class ChartBars { }
   public partial class ChartPanel { }
}

namespace NinjaTrader.Gui.NinjaScript
{
   public class NinjaScriptVisualType { }
}

namespace NinjaTrader.Gui.SuperDom
{
   public class SuperDomBase { }
   public enum SuperDomNoteRelativePosition { Below, Above, Left, Right }
}

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
   [AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
   public class NinjaScriptPropertyAttribute : Attribute
   {
      public bool IsVisible { get; set; } = true;
      public bool IsRequired { get; set; }
      public string DisplayName { get; set; }
   }

   [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
   public class CategoryOrderAttribute : Attribute
   {
      public string Category { get; set; }
      public int Order { get; set; }
      public CategoryOrderAttribute() { }
      public CategoryOrderAttribute(string category, int order)
      {
         Category = category;
         Order = order;
      }
   }

   // Range / numeric constraint attributes (used heavily in strategy parameters).
   [AttributeUsage(AttributeTargets.Property)]
   public class RangeAttribute : Attribute
   {
      public double Min { get; }
      public double Max { get; }
      public RangeAttribute(double min, double max) { Min = min; Max = max; }
      public RangeAttribute(int min, int max) { Min = min; Max = max; }
   }

   [AttributeUsage(AttributeTargets.Class | AttributeTargets.Property, AllowMultiple = true)]
   public class CategoryExpandedAttribute : Attribute
   {
      public string Category { get; set; }
      public bool Expanded { get; set; }
      public CategoryExpandedAttribute() { }
      public CategoryExpandedAttribute(string category, bool expanded)
      {
         Category = category;
         Expanded = expanded;
      }
   }

   // NT8 uses [Browsable(false)] from System.ComponentModel — already in BCL,
   // no shim needed. Same for [TypeConverter], [XmlIgnore].
}

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
using System.Globalization;

namespace QuantroLean.NinjaHost
{
   /// <summary>Tick-exact equity drawdown, measured by the host itself: the account equity including the open
   /// position valued at the price its market exit would fill at (bid for a long, ask for a short) is sampled
   /// after every tick, and the high/low watermarks give the largest fall from a high - in account currency, in %
   /// of that high and in % of the start equity. All timestamps are those of the data (UTC).</summary>
   public sealed class EquityWatermark
   {
      private bool _started;
      private double _high;
      private DateTime _highUtc;

      public double StartEquity { get; private set; }
      public double LowEquity { get; private set; }
      public DateTime LowUtc { get; private set; }
      public double MaxDrawdown { get; private set; }
      public double MaxDrawdownPctOfHigh { get; private set; }
      public DateTime MaxDrawdownHighUtc { get; private set; }
      public DateTime MaxDrawdownLowUtc { get; private set; }
      public long Samples { get; private set; }

      /// <summary>The largest drawdown in % of the start equity.</summary>
      public double MaxDrawdownPctOfStart => StartEquity > 0 ? MaxDrawdown / StartEquity * 100.0 : 0.0;

      public void Sample(double equity, DateTime utc)
      {
         Samples++;
         if (!_started)
         {
            _started = true;
            StartEquity = _high = LowEquity = equity;
            _highUtc = LowUtc = utc;
            return;
         }
         if (equity > _high)
         {
            _high = equity;
            _highUtc = utc;
         }
         if (equity < LowEquity)
         {
            LowEquity = equity;
            LowUtc = utc;
         }
         var drawdown = _high - equity;
         if (drawdown > MaxDrawdown)
         {
            MaxDrawdown = drawdown;
            MaxDrawdownPctOfHigh = _high > 0 ? drawdown / _high * 100.0 : 0.0;
            MaxDrawdownHighUtc = _highUtc;
            MaxDrawdownLowUtc = utc;
         }
      }

      public string Summary(string currency)
      {
         var ic = CultureInfo.InvariantCulture;
         return string.Format(ic,
            "max equity drawdown {0:F2} {1} = {2:F2} % of the high = {3:F2} % of the start equity {4:F2} "
            + "(high {5:yyyy-MM-dd HH:mm:ss}Z, low {6:yyyy-MM-dd HH:mm:ss}Z); lowest equity {7:F2} at "
            + "{8:yyyy-MM-dd HH:mm:ss}Z; {9} samples",
            MaxDrawdown, currency, MaxDrawdownPctOfHigh, MaxDrawdownPctOfStart, StartEquity,
            MaxDrawdownHighUtc, MaxDrawdownLowUtc, LowEquity, LowUtc, Samples);
      }
   }
}

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
using NinjaTrader.Data;
using QuantroLean.NinjaShim;

// Unit test for the forming bar on a time-based series (ShimRuntime [TIME-SECONDARY-FORMING]).
// A time BarStream with EmulateFormingBar opens the next bar's forming slot BEFORE the bar-close
// callback, so at the callback the newest absolute index (Count-1) is that forming bar and the bar
// that JUST closed is at ClosedCount-1 (ClosedCount excludes the open forming slot). Without
// EmulateFormingBar there is no forming slot and ClosedCount == Count. This is the invariant the
// secondary bar-close callback relies on (closedCount = stream.ClosedCount - 1).
internal static class Program
{
    private static int _fail;

    private static void Check(string name, bool ok)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
        if (!ok) _fail++;
    }

    private static int Main()
    {
        var t0 = new DateTime(2013, 10, 7, 14, 0, 30, DateTimeKind.Utc);   // minute 0
        var t1 = new DateTime(2013, 10, 7, 14, 1, 15, DateTimeKind.Utc);   // minute 1 -> closes the minute-0 bar

        // --- Case 1: EmulateFormingBar = true (the time secondary carries the forming bar) ---
        {
            var bars = new Bars();
            var bs = new BarStream(bars, TimeSpan.FromMinutes(1)) { EmulateFormingBar = true };
            bs.OnTick(t0, 100, 100, 100, 1, () => { });       // open the minute-0 bar (+ its forming slot)

            int count = -1, closed = -1;
            double closedBarClose = double.NaN, newestSlotClose = double.NaN;
            bs.OnTick(t1, 101, 101, 101, 1, () =>
            {
                count = bs.Count;                              // includes the open forming slot
                closed = bs.ClosedCount;                       // excludes it
                closedBarClose  = bars.GetClose(closed - 1);   // the FIX reads this index -> the just-closed bar
                newestSlotClose = bars.GetClose(count - 1);    // the OLD code read this index -> the forming bar
            });

            // the minute-0 tick was 100, the minute-1 (forming) tick 101, so the closes identify the bars.
            Check("forming: ClosedCount == Count - 1 (a forming slot is open)", closed == count - 1);
            Check("forming: ClosedCount-1 is the just-closed bar (close 100)", closedBarClose == 100);
            Check("forming: Count-1 is the still-forming bar (close 101, the old/wrong index)", newestSlotClose == 101);
            Check("forming: the two indices differ", (closed - 1) != (count - 1));
        }

        // --- Case 2: EmulateFormingBar = false (no forming slot) ---
        {
            var bars = new Bars();
            var bs = new BarStream(bars, TimeSpan.FromMinutes(1)) { EmulateFormingBar = false };
            bs.OnTick(t0, 100, 100, 100, 1, () => { });

            int count = -1, closed = -1;
            double closedBarClose = double.NaN;
            bs.OnTick(t1, 101, 101, 101, 1, () =>
            {
                count = bs.Count;
                closed = bs.ClosedCount;
                closedBarClose = bars.GetClose(closed - 1);
            });

            Check("no-forming: ClosedCount == Count (no forming slot)", closed == count);
            Check("no-forming: the just-closed bar's close is 100", closedBarClose == 100);
        }

        Console.WriteLine(_fail == 0 ? "SUMMARY: all forming-bar checks passed" : "SUMMARY: " + _fail + " check(s) FAILED");
        return _fail == 0 ? 0 : 1;
    }
}

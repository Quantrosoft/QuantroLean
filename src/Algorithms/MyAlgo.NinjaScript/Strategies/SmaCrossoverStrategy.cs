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

#region Using declarations
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
   /// <summary>Example NinjaScript strategy, written as for NinjaTrader 8 and run unchanged on Lean through the
   /// NinjaShim: long while the fast simple moving average is above the slow one, flat when it falls below.
   /// Same idea as the SmaCrossoverAlgorithm examples in C# and Python.</summary>
   public class SmaCrossoverStrategy : Strategy
   {
      private SMA _fast;
      private SMA _slow;

      protected override void OnStateChange()
      {
         if (State == State.SetDefaults)
         {
            Name = "SmaCrossoverStrategy";
            Description = "Long while SMA(fast) is above SMA(slow).";
            Calculate = Calculate.OnBarClose;
            EntriesPerDirection = 1;
            EntryHandling = EntryHandling.AllEntries;
            IsExitOnSessionCloseStrategy = true;
            ExitOnSessionCloseSeconds = 30;
            BarsRequiredToTrade = 30;
            FastPeriod = 10;
            SlowPeriod = 30;
            Quantity = 100;
         }
         else if (State == State.DataLoaded)
         {
            _fast = SMA(FastPeriod);
            _slow = SMA(SlowPeriod);
         }
      }

      protected override void OnBarUpdate()
      {
         if (CurrentBar < SlowPeriod)
            return;

         if (Position.MarketPosition == MarketPosition.Flat && _fast[0] > _slow[0])
            EnterLong(Quantity, "SmaLong");
         else if (Position.MarketPosition == MarketPosition.Long && _fast[0] < _slow[0])
            ExitLong("SmaExit", "SmaLong");
      }

      [NinjaScriptProperty]
      [Range(1, int.MaxValue)]
      [Display(Name = "Fast period", Order = 1, GroupName = "Parameters")]
      public int FastPeriod { get; set; }

      [NinjaScriptProperty]
      [Range(2, int.MaxValue)]
      [Display(Name = "Slow period", Order = 2, GroupName = "Parameters")]
      public int SlowPeriod { get; set; }

      [NinjaScriptProperty]
      [Range(1, int.MaxValue)]
      [Display(Name = "Quantity", Order = 3, GroupName = "Parameters")]
      public int Quantity { get; set; }
   }
}

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
using NinjaTrader.NinjaScript;

namespace QuantroLean.NinjaShim.Hosting
{
   public static class ShimFillBridge
   {
      public static void RaiseFill(StrategyBase s, bool isEntry, string signal,
         string fromSignal, string exitSignal, bool isLong, int qty, double price,
         DateTime timeUtc, string orderId, NinjaTrader.Cbi.Instrument instrument)
      {
         if (s == null)
            throw new ArgumentNullException(nameof(s),
               "[SHIM-HOSTING] ShimFillBridge.RaiseFill: strategy is null.");
         if (instrument == null)
            throw new ArgumentNullException(nameof(instrument),
               "[SHIM-HOSTING] ShimFillBridge.RaiseFill: instrument is null — pass " +
               "the host's ShimRuntime.DefaultInstrument. No fallback.");
         if (qty <= 0)
            throw new ArgumentOutOfRangeException(nameof(qty), qty,
               "[SHIM-HOSTING] ShimFillBridge.RaiseFill: qty must be > 0.");

         bool isExit = !isEntry;
         // For exits: the exit-signal label carries the reason (SL/TP/
         // EmergencyClose/exit). For entries: signal name == entry signal.
         string entrySig  = isExit ? (fromSignal ?? "") : (signal ?? "");
         string execName  = isExit ? (exitSignal ?? "exit") : entrySig;
         string ntName    = isExit ? execName : entrySig;
         string ntFrom    = isExit ? entrySig : null;

         // Order action from position side + entry/exit kind.
         bool buys = isEntry ? isLong : !isLong;
         var nt8Side = buys
            ? NinjaTrader.Cbi.OrderAction.Buy
            : NinjaTrader.Cbi.OrderAction.Sell;

         var nt8Order = new NinjaTrader.Cbi.Order
         {
            OrderState  = NinjaTrader.Cbi.OrderState.Filled,
            OrderAction = nt8Side,
            OrderType   = NinjaTrader.Cbi.OrderType.Market,
            Quantity    = qty,
            AverageFillPrice = price,
            Name        = ntName,
            FromEntrySignal = ntFrom,
            Instrument  = instrument,
         };
         var nt8Exec = new NinjaTrader.Cbi.Execution
         {
            ExecutionId = orderId ?? "",
            Order       = nt8Order,
            Price       = price,
            Quantity    = qty,
            Time        = timeUtc,
            MarketPosition = nt8Side == NinjaTrader.Cbi.OrderAction.Buy
               ? NinjaTrader.Cbi.MarketPosition.Long
               : NinjaTrader.Cbi.MarketPosition.Short,
            Instrument  = instrument,
            Name        = execName,
            OrderAction = nt8Side,
            IsEntry     = isEntry,
            IsExit      = isExit,
         };
         // Direct typed call into the shim's public Raise* forwarder — the
         // strategy's `protected override OnExecutionUpdate` dispatches via
         // virtual-method polymorphism; no reflection, no boxed args.
         s.RaiseExecutionUpdate(
            nt8Exec, nt8Exec.ExecutionId, nt8Exec.Price, nt8Exec.Quantity,
            nt8Exec.MarketPosition, nt8Order.Name, nt8Exec.Time);
      }
   }
}

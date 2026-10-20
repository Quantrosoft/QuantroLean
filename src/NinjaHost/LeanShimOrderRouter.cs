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
using MyLean.NinjaShim;
using MyLean.NinjaShim.Hosting;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Orders;

namespace MyLean.NinjaHost
{
   /// <summary>Mirrors the hosted strategy's orders as Lean market orders. The shim decides every fill by
   /// NinjaTrader's rules (stop loss and profit target included, checked tick by tick) and hands the resulting
   /// market orders to this router; Lean executes them with its own fill model and brokerage - in a backtest the
   /// backtesting brokerage, live the brokerage the launcher is configured with. Every order carries a tag
   /// (ShimOrderTag) that names the NinjaScript signal it belongs to.</summary>
   public sealed class LeanShimOrderRouter : IShimOrderRouter
   {
      private readonly QCAlgorithm _algorithm;
      private readonly Symbol _symbol;

      public LeanShimOrderRouter(QCAlgorithm algorithm, Symbol symbol)
      {
         _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
         _symbol = symbol ?? throw new ArgumentNullException(nameof(symbol));
      }

      /// <summary>Entry orders routed (market entries and shim-side limit fills).</summary>
      public int EntryOrders { get; private set; }

      /// <summary>Exit orders routed (strategy exits, stop loss, profit target, session-end close).</summary>
      public int ExitOrders { get; private set; }

      public void MarketLong(int qty, string signalName)
      {
         Submit(qty, ShimOrderTag.EncodeEntry(signalName));
         EntryOrders++;
      }

      public void MarketShort(int qty, string signalName)
      {
         Submit(-qty, ShimOrderTag.EncodeEntry(signalName));
         EntryOrders++;
      }

      public void ExitLong(int qty, string exitSignal, string fromSignal)
      {
         Submit(-qty, ShimOrderTag.EncodeExit(fromSignal, exitSignal));
         ExitOrders++;
      }

      public void ExitShort(int qty, string exitSignal, string fromSignal)
      {
         Submit(qty, ShimOrderTag.EncodeExit(fromSignal, exitSignal));
         ExitOrders++;
      }

      public void LimitEntryFilled(int qty, bool isLong, string signalName)
      {
         Submit(isLong ? qty : -qty, ShimOrderTag.EncodeEntry(signalName));
         EntryOrders++;
      }

      private void Submit(int signedQuantity, string tag)
      {
         if (signedQuantity == 0)
            throw new InvalidOperationException($"[NINJAHOST] order with quantity 0 for {_symbol} ({tag}). No fallback.");
         var ticket = _algorithm.MarketOrder(_symbol, signedQuantity, asynchronous: _algorithm.LiveMode, tag: tag);
         if (ticket.Status == OrderStatus.Invalid)
            throw new InvalidOperationException(
               $"[NINJAHOST] Lean rejected the market order {signedQuantity} {_symbol} ({tag}): "
               + $"{ticket.SubmitRequest?.Response?.ErrorMessage}. The engine position would no longer match the "
               + "strategy's - stopping. No fallback.");
      }
   }
}

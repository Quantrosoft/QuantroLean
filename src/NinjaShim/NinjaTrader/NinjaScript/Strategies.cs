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

// Shim — NinjaTrader.NinjaScript.Strategies namespace.
// Strategy : NinjaScriptBase. Adds the order-management API surface that
// strategies use (EnterLong, ExitLong, SetStopLoss, SetProfitTarget,
// CancelOrder, SubmitOrderUnmanaged, etc.).
//
// All order calls forward to ShimRuntime, which fills them by NT8's rules and
// hands the resulting market orders to the host's IShimOrderRouter.

using System;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace NinjaTrader.NinjaScript.Strategies
{
   // Strategy is `partial` because each linked NT8 file carries an auto-
   // generated trailer that ADDS indicator helper-methods to it. NOT marked
   // abstract — partials must share modifiers, NT8 trailers don't declare it.
   public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
   {
      public Position[] Positions
      {
         get
         {
            var bars = BarsArray ?? Array.Empty<Bars>();
            if (_positions == null || _positions.Length != bars.Length
                || (bars.Length > 0 && !ReferenceEquals(_positions[0], Position)))
            {
               string primary = bars.Length > 0 ? bars[0]?.Instrument?.FullName : null;
               for (int i = 1; i < bars.Length; i++)
               {
                  string name = bars[i]?.Instrument?.FullName;
                  if (name != primary)
                     throw new InvalidOperationException(
                        "[NINJASHIM] Positions: series " + i + " is on instrument '"
                        + name + "' but the shim hosts only '" + primary
                        + "' - a second instrument has no position here. No fallback.");
               }
               _positions = new Position[bars.Length];
               for (int i = 0; i < bars.Length; i++)
                  _positions[i] = Position;
            }
            return _positions;
         }
      }
      private Position[] _positions;

      public int Position_Quantity => _runtime?.GetPositionQuantity(0) ?? 0;
      public Position Position { get; internal set; } = new Position();

      public double DefaultQuantity { get; set; } = 1;
      public OrderFillResolution OrderFillResolution { get; set; } = OrderFillResolution.Standard;
      public BarsPeriodType OrderFillResolutionType { get; set; } = BarsPeriodType.Tick;
      public int            OrderFillResolutionValue { get; set; } = 1;
      public TimeInForce TimeInForce { get; set; } = TimeInForce.Gtc;
      // EntriesPerDirection is `int` in NT8 (max simultaneous entries per
      // direction), not bool. MaximumBarsLookBack is on the partial above
      // (an enum type, not int).
      public int EntriesPerDirection { get; set; } = 1;
      public NinjaTrader.Cbi.EntryHandling EntryHandling { get; set; } = NinjaTrader.Cbi.EntryHandling.AllEntries;
      public StopTargetHandling StopTargetHandling { get; set; } = StopTargetHandling.PerEntryExecution;
      public bool IncludeCommission { get; set; }
      public bool IsExitOnSessionCloseStrategy { get; set; }
      public int ExitOnSessionCloseSeconds { get; set; } = 30;
      public bool IsFillLimitOnTouch { get; set; }
      public bool IsUnmanaged { get; set; }
      public RealtimeErrorHandling RealtimeErrorHandling { get; set; } = RealtimeErrorHandling.StopCancelClose;
      public bool TraceOrders { get; set; }
      public double Slippage { get; set; }
      public StartBehavior StartBehavior { get; set; } = Strategies.StartBehavior.WaitUntilFlat;

      // ---- Managed-approach order API --------------------------------------
      public Order EnterLong() => EnterLong((int)DefaultQuantity, "");
      public Order EnterLong(int quantity) => EnterLong(quantity, "");
      public Order EnterLong(string signalName) => EnterLong((int)DefaultQuantity, signalName);
      public Order EnterLong(int quantity, string signalName) =>
         _runtime?.EnterLong(this, 0, quantity, signalName);
      // 3-arg version: barsInProgress + quantity + signal
      public Order EnterLong(int barsInProgress, int quantity, string signalName) =>
         _runtime?.EnterLong(this, barsInProgress, quantity, signalName);

      public Order EnterShort() => EnterShort((int)DefaultQuantity, "");
      public Order EnterShort(int quantity) => EnterShort(quantity, "");
      public Order EnterShort(string signalName) => EnterShort((int)DefaultQuantity, signalName);
      public Order EnterShort(int quantity, string signalName) =>
         _runtime?.EnterShort(this, 0, quantity, signalName);
      public Order EnterShort(int barsInProgress, int quantity, string signalName) =>
         _runtime?.EnterShort(this, barsInProgress, quantity, signalName);

      public Order ExitLong() => ExitLong((int)DefaultQuantity, "", "");
      public Order ExitLong(string signalName, string fromEntrySignal) =>
         ExitLong(_runtime?.OpenQuantityOf(fromEntrySignal) ?? 0, signalName, fromEntrySignal);
      public Order ExitLong(int quantity, string signalName, string fromEntrySignal) =>
         _runtime?.ExitLong(this, 0, quantity, signalName, fromEntrySignal);
      // 4-arg version: barsInProgress + qty + signal + fromEntrySignal
      public Order ExitLong(int barsInProgress, int quantity, string signalName, string fromEntrySignal) =>
         _runtime?.ExitLong(this, barsInProgress, quantity, signalName, fromEntrySignal);

      public Order ExitShort() => ExitShort((int)DefaultQuantity, "", "");
      public Order ExitShort(string signalName, string fromEntrySignal) =>
         ExitShort(_runtime?.OpenQuantityOf(fromEntrySignal) ?? 0, signalName, fromEntrySignal);
      public Order ExitShort(int quantity, string signalName, string fromEntrySignal) =>
         _runtime?.ExitShort(this, 0, quantity, signalName, fromEntrySignal);
      public Order ExitShort(int barsInProgress, int quantity, string signalName, string fromEntrySignal) =>
         _runtime?.ExitShort(this, barsInProgress, quantity, signalName, fromEntrySignal);

      // Stop-/profit-target attachments
      public void SetStopLoss(double currency) =>
         _runtime?.SetStopLoss(this, "", CalculationMode.Currency, currency, false);

      public void SetStopLoss(CalculationMode mode, double value) =>
         _runtime?.SetStopLoss(this, "", mode, value, false);

      public void SetStopLoss(string fromEntrySignal, CalculationMode mode, double value, bool isSimulatedStop) =>
         _runtime?.SetStopLoss(this, fromEntrySignal, mode, value, isSimulatedStop);

      public void SetProfitTarget(double currency) =>
         _runtime?.SetProfitTarget(this, "", CalculationMode.Currency, currency);

      public void SetProfitTarget(CalculationMode mode, double value) =>
         _runtime?.SetProfitTarget(this, "", mode, value);

      public void SetProfitTarget(string fromEntrySignal, CalculationMode mode, double value) =>
         _runtime?.SetProfitTarget(this, fromEntrySignal, mode, value);

      public void SetTrailStop(double currency) =>
         _runtime?.SetTrailStop(this, "", CalculationMode.Currency, currency, false);

      public void SetTrailStop(string fromEntrySignal, CalculationMode mode, double value, bool isSimulatedStop) =>
         _runtime?.SetTrailStop(this, fromEntrySignal, mode, value, isSimulatedStop);

      // ---- Order management ------------------------------------------------
      public void CancelOrder(Order order) => _runtime?.CancelOrder(this, order);
      public void ChangeOrder(Order order, int quantity, double limitPrice, double stopPrice) =>
         _runtime?.ChangeOrder(this, order, quantity, limitPrice, stopPrice);

      // ---- Unmanaged approach ----------------------------------------------
      public Order SubmitOrderUnmanaged(int barsInProgress, OrderAction orderAction, OrderType orderType,
                                        int quantity, double limitPrice, double stopPrice,
                                        string ocoId, string signalName) =>
         _runtime?.SubmitOrderUnmanaged(this, barsInProgress, orderAction, orderType,
                                       quantity, limitPrice, stopPrice, ocoId, signalName);

      public Order SubmitOrderUnmanaged(int barsInProgress, OrderAction orderAction, OrderType orderType,
                                        int quantity) =>
         SubmitOrderUnmanaged(barsInProgress, orderAction, orderType, quantity, 0, 0, "", "");

   }

   public enum StopTargetHandling { PerEntryExecution, ByStrategyPosition }
   public enum OrderFillResolution { Standard, High, ByStrategy }
   public enum RealtimeErrorHandling { StopCancelClose, StopCancelCloseIgnoreRejects, IgnoreAllErrors }
   public enum StartBehavior { WaitUntilFlat, ImmediatelySubmitSynchronizeAccount, AdoptAccountPosition }
   public enum CalculationMode { Currency, Percent, Pips, Price, Ticks }
}

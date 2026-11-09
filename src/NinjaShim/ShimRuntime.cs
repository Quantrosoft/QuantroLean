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
using System.Collections.Generic;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;

namespace MyLean.NinjaShim
{
   /// <summary>
   /// A simple tick record fed into ShimRuntime.PumpTick().
   /// </summary>
   public sealed class ShimTick
   {
      public DateTime Ts;
      public double   Price;
      public double   Bid;
      public double   Ask;
      public long     Volume;

      public bool     IsLast = true;
   }

   /// <summary>
   /// One open position tracked by ShimRuntime. Mirrors NT8's position
   /// state at a minimal level — entry price/ts, SL/TP, qty.
   /// </summary>
   public sealed class ShimPosition
   {
      public string SignalName;
      public DateTime EntryTs;
      public double   EntryPrice;
      public int      Quantity;
      public bool     IsLong;        // true=Long, false=Short
      public int      EntryBarIdx;
      public double   StopLossPx;
      public double   TakeProfitPx;

      public NinjaTrader.Cbi.Order EntryOrder;
      public NinjaTrader.Cbi.Order StopOrder;
      public NinjaTrader.Cbi.Order TargetOrder;
      public NinjaScriptBase Owner;
      public bool RaiseOrderEvents;

      public DateTime StopActiveTs;
      public DateTime TargetActiveTs;
      public double   PendingStopPx = double.NaN;
      public DateTime PendingStopActiveTs;
      public double   PendingTargetPx = double.NaN;
      public DateTime PendingTargetActiveTs;
      public DateTime StopTriggerTs = DateTime.MinValue;
      public NinjaTrader.Cbi.Order PendingExitOrder;
   }

   /// <summary>
   /// One completed trade — entry + exit recorded by ShimRuntime.
   /// </summary>
   public sealed class ShimTrade
   {
      public int      Idx;
      public string   SignalName;
      public DateTime EntryTs;
      public double   EntryPrice;
      public DateTime ExitTs;
      public double   ExitPrice;
      public int      Quantity;
      public bool     IsLong;
      public int      EntryBarIdx;   // [BARINDEX] primary-bar index at entry
      public int      ExitBarIdx;    // [BARINDEX] primary-bar index at exit
      public string   ExitReason;    // "SL", "TP", "EmergencyClose", "EndOfData"
      /// <summary>[COMMISSION] round-turn commission of THIS trade in account currency
      /// (0 unless ShimRuntime.CommissionPerSide is set). Every consumer that turns
      /// points into money subtracts it - the trade carries its own cost.</summary>
      public double   Commission;
   }

   public interface IShimOrderRouter
   {
      void MarketLong(int qty, string signalName);

      /// <summary>Submit a market SELL for `qty` contracts.</summary>
      void MarketShort(int qty, string signalName);

      /// <summary>Close an open long position via market SELL.
      /// `fromSignal` matches the entry signal name; `exitSignal` carries
      /// the reason ("SL"/"TP"/"EmergencyClose"/"exit") for the log.</summary>
      void ExitLong(int qty, string exitSignal, string fromSignal);

      /// <summary>Close an open short via market BUY-to-cover.</summary>
      void ExitShort(int qty, string exitSignal, string fromSignal);

      void LimitEntryFilled(int qty, bool isLong, string signalName) { }
   }

   public class ShimRuntime
   {
      private readonly List<NinjaScriptBase> _hosted = new();
      private readonly Dictionary<string, ShimPosition> _openPositions = new();

      private struct PendingProtection {
         public double SlValue, TpValue;
         public NinjaTrader.NinjaScript.Strategies.CalculationMode SlMode, TpMode;
         public bool SlSet, TpSet;
      }
      private readonly Dictionary<string, PendingProtection> _pendingProtection = new();
      public readonly List<ShimTrade> TradeLedger = new();

      public double OpenFloatingPoints(double lastPrice)
      {
         double pts = 0;
         foreach (var kv in _openPositions)
         {
            var p = kv.Value;
            pts += (lastPrice - p.EntryPrice) * (p.IsLong ? 1.0 : -1.0) * p.Quantity;
         }
         return pts;
      }

      public double OpenFloatingPointsAtExit()
      {
         double pts = 0;
         foreach (var kv in _openPositions)
         {
            var p = kv.Value;
            pts += (FillPx(!p.IsLong) - p.EntryPrice) * (p.IsLong ? 1.0 : -1.0) * p.Quantity;
         }
         return pts;
      }

      /// <summary>Number of currently open shim positions.</summary>
      public int OpenPositionCount => _openPositions.Count;

      private sealed class PendingEntry
      {
         public string SignalName;
         public bool   IsLong;
         public int    Quantity;
         public PendingProtection Protection;
         public bool   HasProtection;
         public DateTime SubmitTs;
         public DateTime ActiveTs;      // [NT8-SECOND-GRID] working from here on (= SubmitTs when off)
         public double SubmitBid;
         public double SubmitAsk;
         public double SubmitPrice;
         // the second grid's book of the batch the entry was submitted in (GridBid/GridAsk at the submit; 0 when the
         // grid is off) - see Nt8SecondGridEntryAtGridBook
         public double SubmitGridBid;
         public double SubmitGridAsk;

         public NinjaTrader.Cbi.Order NtOrder;
         public NinjaScriptBase Owner;
         public bool RaiseOrderEvents;
      }
      private readonly Queue<PendingEntry> _pendingEntries = new();
      private PendingEntry _lastEnqueuedEntry;

      /// <summary>Signed quantity of the market entries that are submitted (the router already has them) but
      /// not yet filled shim-side - they fill on a later tick (FinalisePendingEntries). A host that mirrors the
      /// orders compares its engine position against the strategy's Position plus this.</summary>
      internal int PendingEntryNetQuantity
      {
         get
         {
            int net = 0;
            foreach (var e in _pendingEntries) net += e.IsLong ? e.Quantity : -e.Quantity;
            return net;
         }
      }

      private sealed class PendingLimit
      {
         public string SignalName;
         public bool   IsLong;
         public int    Quantity;
         public double LimitPrice;
         public DateTime SubmitTs;
         public DateTime ActiveTs;      // [NT8-SECOND-GRID] working from here on (= SubmitTs when off)
         public NinjaTrader.Cbi.Order NtOrder;
         public NinjaScriptBase Owner;
         public PendingProtection Protection;
         public bool   HasProtection;
         // [NT8-UNMANAGED] true when submitted via SubmitOrderUnmanaged: the
         // strategy holds NtOrder and gets OnOrderUpdate/OnExecutionUpdate on it.
         public bool   RaiseOrderEvents;
         public bool   CheckMarketableNext;
      }
      private readonly List<PendingLimit> _pendingLimits = new();
      private readonly HashSet<string> _limitOrigin = new();

      public NinjaTrader.Cbi.Instrument DefaultInstrument
      {
         get => _defaultInstrument;
         set
         {
            _defaultInstrument = value;
            NinjaTrader.Cbi.Instrument.HostDefault = value;
         }
      }
      private NinjaTrader.Cbi.Instrument _defaultInstrument;

      /// <summary>When set, the market orders of the strategy's entries and exits go to this router
      /// (stop loss and profit target included: the shim checks them tick by tick and exits at
      /// market), so a host can mirror them as engine orders. ShimPosition and TradeLedger are
      /// kept either way.</summary>
      public IShimOrderRouter OrderRouter { get; set; }

      // ---- Bar consolidator ----------------------------------------------
      private BarStream _barStream;

      public int SessionAdvances => _barStream?.SessionAdvances ?? -1;

      public NinjaTrader.Data.Bars PrimaryBars => _bars;

      public NinjaTrader.Data.Bars SignalBars
         => _secondaries.Count > 0 ? _secondaries[0].bars : _bars;

      private NinjaTrader.Data.Bars _bars;

      private readonly List<(NinjaTrader.Data.Bars bars, BarStream stream)> _secondaries = new();

      public IReadOnlyList<NinjaTrader.NinjaScript.Indicators.Indicator> HostedIndicators
         => _indicators;

      // ---- Per-series observer events -----------------------------------------------------------------------
      // For code outside the shim that has to follow every tick and every bar close of a bar series, e.g. an
      // order-flow indicator that accumulates the prints of the forming bar and rolls them at its close. Raised
      // per series (0 = primary, 1.. = AddDataSeries order), in this order for every tick:
      //   1. SeriesBarClosed      for each bar the tick closes, at the start of that close - BEFORE the hosted
      //                           strategies see the closed bar; closedIdx = the closed bar's index in `series`
      //                           (Count - 1, or Count - 2 when the forming slot is already open on the series);
      //   2. SeriesTickConsolidated once, after the series consolidated the tick (all closes done) and BEFORE the
      //                           strategies' per-tick (forming bar) call; anyBarClosed = the tick closed a bar;
      //   3. SeriesTickDispatched once, after every dispatch of the tick on that series - the print then belongs to
      //                           the bar that is forming now.
      // The primary series runs 1-3 before the first secondary series.

      /// <summary>See the order above. Arguments: series index, the series, closed bar index, close time (UTC).</summary>
      public event Action<int, NinjaTrader.Data.Bars, int, DateTime> SeriesBarClosed;

      /// <summary>See the order above. Arguments: series index, the tick, whether the tick closed a bar of that series.</summary>
      public event Action<int, ShimTick, bool> SeriesTickConsolidated;

      /// <summary>See the order above. Arguments: series index, the tick.</summary>
      public event Action<int, ShimTick> SeriesTickDispatched;

      /// <summary>Raised once per priced tick at the very end of its processing: after every series consolidated and
      /// dispatched it, after the hosted scripts were set back to the primary series and the primary bars' first-bar-
      /// of-session flag was set for the bar that is forming. For code outside the shim that has to see the tick once
      /// the bars have taken it, e.g. an indicator that accumulates the prints of the forming bar in OnMarketData.</summary>
      public event Action<ShimTick> TickCompleted;

      private void RaiseSeriesBarClosed(int seriesIdx, NinjaTrader.Data.Bars series, DateTime closeTimeUtc)
      {
         var h = SeriesBarClosed;
         if (h == null) return;
         if (series == null || series.Count < 1)
            throw new InvalidOperationException(
               $"[NINJASHIM] bar close of series {seriesIdx} without bars.");
         int closedIdx = series.Count - (series.FormingSlotMirrored ? 2 : 1);
         if (closedIdx < 0)
            throw new InvalidOperationException(
               $"[NINJASHIM] bar close of series {seriesIdx} with only a forming slot in the series.");
         h(seriesIdx, series, closedIdx, closeTimeUtc);
      }

      // [SESSION-CLOSE-GATE] the end-of-data flush books EVERY position still open at the end of the data,
      // including one of a strategy without IsExitOnSessionCloseStrategy whose session-end close the gate
      // skips. What NinjaTrader does with such a position at the end of a backtest is not characterised here.
      public void FlushOpenPositionsAtEndOfData()
      {
         FlushSecondGrid();   // [NT8-SECOND-GRID] the last collected second still has to run
         if (_openPositions.Count == 0) return;
         // Prefer the last pre-session-end tick (matches what
         // CheckOpenPositionsExit would have used). Falls back to Now if
         // no such tick was tracked (e.g. day with no session boundary).
         var exitTs = _lastTickTsBeforeSessionEnd != DateTime.MinValue
            ? _lastTickTsBeforeSessionEnd
            : Now;
         var exitPx = _lastTickTsBeforeSessionEnd != DateTime.MinValue
            ? _lastTickPxBeforeSessionEnd
            : LastPrice;
         var exitBid = _lastTickTsBeforeSessionEnd != DateTime.MinValue ? _lastTickBidBeforeSessionEnd : LastBid;
         var exitAsk = _lastTickTsBeforeSessionEnd != DateTime.MinValue ? _lastTickAskBeforeSessionEnd : LastAsk;
         WriteLine?.Invoke(
            $"[NT8-PARITY-FLUSH] {_openPositions.Count} open position(s) at end of data — " +
            $"flushing with emergency exit @ {exitTs:yyyy-MM-dd HH:mm:ss} px={exitPx:F2}");
         foreach (var (label, pos) in _openPositions)
         {
            TradeLedger.Add(new ShimTrade
            {
               Idx = TradeLedger.Count + 1,
               SignalName = pos.SignalName,
               EntryTs    = pos.EntryTs,
               EntryPrice = pos.EntryPrice,
               ExitTs     = exitTs,
               ExitPrice  = SessionClosePx(pos, exitPx, exitBid, exitAsk),   // [SESSION-CLOSE-BBO]
               Quantity   = pos.Quantity,
               IsLong     = pos.IsLong,
               Commission = CommissionFor(pos.Quantity),

                  EntryBarIdx = pos.EntryBarIdx,

                  ExitBarIdx  = _bars?.Count ?? 0,
               ExitReason = "EndOfData",
            });
            AppendStrategyTrade(TradeLedger[TradeLedger.Count - 1], "EndOfData");
            // Don't flatten Lean (algorithm is ending anyway) — just record.
         }
         _openPositions.Clear();
         SyncHostedPositions();
      }
      // [NT8-PARITY-FLUSH] END ──────────────────────────────────────────────

      private bool _unmanagedLifecycleActive;
      private NinjaTrader.Cbi.MarketPosition _lastSyncedNetMp = NinjaTrader.Cbi.MarketPosition.Flat;

      public bool UnmanagedNt8Events { get; internal set; } = false;

      public bool ExecutionsFromLedger { get; internal set; } = false;

      private int _ledgerExecSeq;
      private void RaiseLedgerFill(NinjaScriptBase owner, bool isEntry, string signal,
                                   string fromSignal, string exitSignal, bool isLong,
                                   int qty, double price, DateTime tsUtc)
      {
         var strat = owner as NinjaTrader.NinjaScript.Strategies.Strategy;
         if (strat == null)
            foreach (var ns in _hosted)
               if (ns is NinjaTrader.NinjaScript.Strategies.Strategy s) { strat = s; break; }
         if (strat == null) return;
         _ledgerExecSeq++;
         try
         {
            MyLean.NinjaShim.Hosting.ShimFillBridge.RaiseFill(strat,
               isEntry: isEntry, signal: signal, fromSignal: fromSignal, exitSignal: exitSignal,
               isLong: isLong, qty: qty, price: price, timeUtc: tsUtc,
               orderId: "ledger_" + _ledgerExecSeq.ToString(System.Globalization.CultureInfo.InvariantCulture),
               instrument: DefaultInstrument);
         }
         catch (Exception ex) { LogShimError(owner ?? (NinjaScriptBase)strat, isEntry ? "OnExecutionUpdate(ledger entry)" : "OnExecutionUpdate(ledger exit)", ex); }
      }

      public bool QuoteDrivenExecution { get; internal set; } = false;

      private void SyncHostedPositions()
      {
         if (SyncHostedPositionFields(out int net, out double lastEntry, out var newMp))
            RaiseHostedPositionUpdates(net, lastEntry, newMp);
      }

      // Field half of SyncHostedPositions: the hosted Position mirrors the
      // ledger. Returns true when the market position changed; the caller
      // decides WHEN OnPositionUpdate fires (see [NT8-UNMANAGED]).
      private bool SyncHostedPositionFields(out int net, out double lastEntry,
                                            out NinjaTrader.Cbi.MarketPosition newMp)
      {
         net = 0; lastEntry = 0;
         double notional = 0; int gross = 0;
         foreach (var kv in _openPositions)
         {
            var p = kv.Value;
            net += p.IsLong ? p.Quantity : -p.Quantity;
            lastEntry = p.EntryPrice;
            notional += p.EntryPrice * p.Quantity;
            gross += p.Quantity;
         }
         newMp = net > 0
            ? NinjaTrader.Cbi.MarketPosition.Long
            : net < 0 ? NinjaTrader.Cbi.MarketPosition.Short
                      : NinjaTrader.Cbi.MarketPosition.Flat;
         double averagePrice = gross > 0 ? notional / gross : lastEntry;
         bool mpChanged = newMp != _lastSyncedNetMp;
         _lastSyncedNetMp = newMp;
         foreach (var ns in _hosted)
         {
            if (ns is NinjaTrader.NinjaScript.Strategies.Strategy st && st.Position != null)
            {
               st.Position.MarketPosition = newMp;
               st.Position.Quantity = Math.Abs(net);
               st.Position.AveragePrice = averagePrice;
               st.Position.Instrument = DefaultInstrument;
            }
         }
         return mpChanged;
      }

      // Event half of SyncHostedPositions (gate: unmanaged lifecycle, or NT8 semantics - see
      // [NT8-LIFECYCLE-GATE] at AppendStrategyTrade).
      private void RaiseHostedPositionUpdates(int net, double lastEntry,
                                              NinjaTrader.Cbi.MarketPosition newMp)
      {
         if (!_unmanagedLifecycleActive && !UnmanagedNt8Events) return;
         foreach (var ns in _hosted)
         {
            if (ns is NinjaTrader.NinjaScript.Strategies.Strategy st && st.Position != null)
            {
               try { st.RaisePositionUpdate(st.Position, lastEntry, Math.Abs(net), newMp); }
               catch (Exception ex) { LogShimError(ns, "OnPositionUpdate", ex); }
            }
         }
      }

      private void AppendStrategyTrade(ShimTrade t, string exitName)
      {
         if (!_unmanagedLifecycleActive && !UnmanagedNt8Events) return;
         double pointValue = DefaultInstrument?.MasterInstrument?.PointValue ?? 1.0;
         double tickSize   = DefaultInstrument?.MasterInstrument?.TickSize ?? 0.01;
         double dir        = t.IsLong ? 1.0 : -1.0;
         double points     = (t.ExitPrice - t.EntryPrice) * dir;
         foreach (var ns in _hosted)
         {
            if (ns is not NinjaTrader.NinjaScript.Strategies.Strategy st) continue;
            var trades = st.SystemPerformance?.AllTrades;
            if (trades == null) continue;
            var trade = new NinjaTrader.NinjaScript.Trade
            {
               EntryTime  = t.EntryTs,
               ExitTime   = t.ExitTs,
               EntryPrice = t.EntryPrice,
               ExitPrice  = t.ExitPrice,
               Quantity   = t.Quantity,
               ProfitPoints   = points,
               ProfitTicks    = tickSize > 0 ? points / tickSize : 0,
               ProfitCurrency = points * pointValue * t.Quantity,
               TradeNumber    = trades.Count + 1,
               Entry = new NinjaTrader.NinjaScript.TradeEntry
               {
                  Time = t.EntryTs, Price = t.EntryPrice, Quantity = t.Quantity,
                  Name = t.SignalName,
                  MarketPosition = t.IsLong ? NinjaTrader.Cbi.MarketPosition.Long
                                            : NinjaTrader.Cbi.MarketPosition.Short,
               },
               Exit = new NinjaTrader.NinjaScript.TradeEntry
               {
                  Time = t.ExitTs, Price = t.ExitPrice, Quantity = t.Quantity,
                  Name = exitName ?? t.ExitReason,
               },
            };
            trades.Add(trade);
            AddToPerformance(trades.TradesPerformance, trade);

            trades.TradesCount++;
            var bucket = trade.ProfitCurrency > 0 ? trades.WinningTrades
                                                  : trades.LosingTrades;
            if (bucket != null)
            {
               bucket.Add(trade);
               bucket.TradesCount++;
               AddToPerformance(bucket.TradesPerformance, trade);
            }

            // NT8 fills SystemPerformance.RealTimeTrades with trades closed in real time
            // (live or market replay, once past the warm-up gate); a plain backtest leaves it empty.
            if ((PlaybackMode || LiveMode) && _warmupGateOpen)
            {
               var realtime = st.SystemPerformance?.RealTimeTrades;
               if (realtime != null)
               {
                  realtime.Add(trade);
                  realtime.TradesCount++;
                  AddToPerformance(realtime.TradesPerformance, trade);
               }
            }
         }
      }

      private static void AddToPerformance(NinjaTrader.NinjaScript.TradePerformance perf,
                                           NinjaTrader.NinjaScript.Trade trade)
      {
         if (perf == null) return;
         perf.Currency.CumProfit += trade.ProfitCurrency;
         perf.Points.CumProfit   += trade.ProfitPoints;
         perf.Ticks.CumProfit    += trade.ProfitTicks;
         if (trade.ProfitCurrency > 0) perf.GrossProfit += trade.ProfitCurrency;
         else                          perf.GrossLoss   += trade.ProfitCurrency;
         perf.NetProfit += trade.ProfitCurrency;
         perf.TradesCount++;
      }

      /// <summary>
      /// Attach a primary Bars object to the runtime + every hosted strategy.
      /// Subsequent PumpTick calls consolidate into this Bars, increment
      /// CurrentBars[0], and fire DispatchBarUpdate on boundary crossings.
      /// </summary>
      public void AttachPrimaryBars(NinjaTrader.Data.Bars bars, TimeSpan period)
      {
         bars.TradingHours = NewSessionTradingHours();   // [NO-UTC-DEFAULT]
         _bars = bars;
         _barStream = new BarStream(bars, period) { EmulateFormingBar = EmulateFormingBar };
         foreach (var ns in _hosted)
         {
            ns.Bars = bars;
            ns.BarsArray = new[] { bars };
            ns.CurrentBars = new[] { 0 };
         }
      }

      public void AttachPrimaryBars(NinjaTrader.Data.Bars bars, int ticksPerBar)
      {
         bars.TradingHours = NewSessionTradingHours();   // [NO-UTC-DEFAULT]
         _bars = bars;
         _barStream = new BarStream(bars, ticksPerBar);
         foreach (var ns in _hosted)
         {
            ns.Bars = bars;
            ns.BarsArray = new[] { bars };
            ns.CurrentBars = new[] { 0 };
         }
      }

      /// <summary>Create a hosted NT8 bars type (Renko/Tick/Range, linked from the local NinjaTrader install via
      /// NINJASHIM_NT8_BARSTYPES) by period type. Returns null when this build does not link it and no
      /// SecondaryHostedBarsTypeResolver serves it. Kept in this assembly so a caller (e.g. NinjaHost) needs
      /// neither the NT8 bars-type classes nor the build symbol.</summary>
      public NinjaTrader.NinjaScript.BarsType CreateHostedBarsType(BarsPeriodType type, int value)
      {
         switch (type)
         {
#if NINJASHIM_NT8_BARSTYPES
            case BarsPeriodType.Range: return new NinjaTrader.NinjaScript.BarsTypes.RangeBarsType();
            case BarsPeriodType.Renko: return new NinjaTrader.NinjaScript.BarsTypes.RenkoBarsType();
            case BarsPeriodType.Tick:  return new NinjaTrader.NinjaScript.BarsTypes.TickBarsType();
#endif
            default:
               return SecondaryHostedBarsTypeResolver?.Invoke(type, value).hosted;
         }
      }

      /// <summary>Attach a hosted NT8 bars type (Range/Renko) as the PRIMARY series, by period type, so the host
      /// can request a hosted primary without referencing NT8's bars-type classes or NINJASHIM_NT8_BARSTYPES.
      /// Throws loud when the type is not linked in this build - a missing primary bars type makes every result
      /// worthless.</summary>
      public void AttachPrimaryHostedBars(NinjaTrader.Data.Bars bars, BarsPeriodType type, int value,
                                          bool isResetOnNewTradingDay)
      {
         var hosted = CreateHostedBarsType(type, value);
         if (hosted == null)
            throw new NotSupportedException(
               $"[SHIM] primary bars type {type} ({value}) is not linked in this build. Build the NinjaShim "
             + "with NINJASHIM_NT8_BARSTYPES (needs a local NinjaTrader 8 install) or register a "
             + "SecondaryHostedBarsTypeResolver. No silent fallback.");
         AttachPrimaryBars(bars, hosted, isResetOnNewTradingDay);
      }

      public void AttachPrimaryBars(NinjaTrader.Data.Bars bars,
                                    NinjaTrader.NinjaScript.BarsType hostedType,
                                    bool isResetOnNewTradingDay)
      {
         if (hostedType == null) throw new ArgumentNullException(nameof(hostedType));
         _bars = bars;

         var shadow = new NinjaTrader.Data.Bars
         {
            Instrument  = bars.Instrument,
            BarsPeriod  = bars.BarsPeriod,
            FromDate    = bars.FromDate,
            ToDate      = bars.ToDate,
            IsResetOnNewTradingDay = isResetOnNewTradingDay,
            TradingHours = NewSessionTradingHours(),   // [NO-UTC-DEFAULT] was `?? TimeZoneInfo.Utc`
         };
         // The strategy series inherits the same session settings so that a
         // later reader (indicator, strategy) does not meet different
         // boundaries than the bar type.
         bars.TradingHours           = shadow.TradingHours;
         bars.IsResetOnNewTradingDay = isResetOnNewTradingDay;

         hostedType.DispatchStateChange(NinjaTrader.NinjaScript.State.SetDefaults);
         hostedType.BarsPeriod = bars.BarsPeriod;
         hostedType.DispatchStateChange(NinjaTrader.NinjaScript.State.Configure);

         _barStream = new BarStream(bars, hostedType, shadow)
         {
            SessionTrace = msg => WriteLine?.Invoke(msg),
            EmulateFormingBar = EmulateFormingBar,
         };
         WriteLine?.Invoke(
            $"[BARSTYPE] hosted bars type {hostedType.GetType().Name} "
            + $"(Value={bars.BarsPeriod.Value}, Value2={bars.BarsPeriod.Value2}, "
            + $"TickSize={bars.Instrument?.MasterInstrument?.TickSize}, "
            + $"IsResetOnNewTradingDay={isResetOnNewTradingDay}, "
            + $"session end {SessionEndMinuteOfDay} min "
            + $"{shadow.TradingHours.SessionEndTimeZone.Id})");

         foreach (var ns in _hosted)
         {
            ns.Bars = bars;
            ns.BarsArray = new[] { bars };
            ns.CurrentBars = new[] { 0 };
         }
      }

      // ---- Latest tick state (drives GetCurrentBid/Ask/Time accessors) ----
      public double LastBid  { get; private set; }
      public double LastAsk  { get; private set; }
      public double LastPrice{ get; private set; }
      public long   LastVol  { get; private set; }
      // ---- Properties read by NinjaScriptBase forwarders -----------------
      public DateTime Now { get; internal set; } = DateTime.UtcNow;
      public bool IsConnected { get; internal set; } = true;
      public bool IsBacktest  { get; internal set; } = true;

      public bool PlaybackMode { get; internal set; } = false;

      public bool LiveMode { get; internal set; } = false;

      public bool FillAtBbo { get; internal set; } = false;

      public bool FillAtNextTick { get; internal set; } = false;

      public bool Nt8SecondGrid { get; internal set; } = false;
      public string Nt8SecondGridQuote { get; internal set; } = "end";
      /// <summary>[NT8-SECOND-GRID] whole seconds a protective leg or a stop/target change waits
      /// after the second it was placed in (1 = effective from the next whole second).</summary>
      public int Nt8SecondGridLegDelaySeconds { get; internal set; } = 1;
      public bool Nt8SecondGridTouchThrough { get; internal set; } = false;
      /// <summary>With Nt8SecondGrid: a MARKET entry submitted inside second S fills at the book NinjaTrader's Market
      /// Replay hands the strategy for S - the end book of the batch (GridBid/GridAsk at the submit) - and is stamped
      /// at the submit tick, instead of the first event of its side from S + 1 on. Measured on a strategy suite against
      /// NinjaTrader Market Replay (99 entries, 01.01.2025-12.06.2026): this book equals NinjaTrader's fill in 99 of 99,
      /// the first event from S + 1 on in 43. Default false = unchanged behaviour.</summary>
      public bool Nt8SecondGridEntryAtGridBook { get; internal set; } = false;
      /// <summary>With Nt8SecondGrid: a triggered STOP fills at the worse of the stop level, the next event's price
      /// and - when that event is a trade - the book side the order trades against on the same event (bid for a
      /// long's sell stop, ask for a short's buy stop). Measured on the 54 stop exits of the same Market Replay run
      /// against a trades tape that carries the book on each trade: 53 of 54 fill prices, the next trade's price
      /// alone 47. Market exits the strategy submitted keep the next event's price. Default false = unchanged.</summary>
      public bool Nt8SecondGridStopFillWorst { get; internal set; } = false;
      /// <summary>With Nt8SecondGrid: true (default) = GetCurrentBid/Ask answer the book at the end of the dispatched
      /// second's batch (GridBid/GridAsk); false = the book of the event being dispatched (LastBid/LastAsk), as without
      /// the grid. Measured on a strategy suite's Market Replay runs: with the
      /// batch-end book its spread filter let entries through that NinjaTrader blocked (a long entry 06.07.2026
      /// 09:42:00 ET: 2 ticks at the batch end, 4 at the dispatched tick). The grid's fill rules do not change.</summary>
      public bool Nt8SecondGridStrategyBookAtBatchEnd { get; internal set; } = true;
      public bool LimitJournalAlways { get; internal set; } = false;
      public bool LimitBookTouch { get; internal set; } = false;

      public double OrderDelayMs { get; internal set; } = 0.0;

      /// <summary>True when an order carries an activation time at all - either from the
      /// NT8 second grid or from OrderDelayMs. The gates below ask THIS, not the grid, so
      /// the delay works on its own. Both off = the untouched first-principles path
      /// (fill on the submit event).</summary>
      private bool OrderActivationInUse => Nt8SecondGrid || OrderDelayMs > 0;

      public double CommissionPerSide { get; internal set; } = 0.0;

      /// <summary>Round-turn cost of a position of this size - the one place that decides
      /// how CommissionPerSide becomes money (entry side + exit side).</summary>
      private double CommissionFor(int quantity) => 2.0 * CommissionPerSide * quantity;
      // prices are exact tick multiples as doubles; the epsilon only guards the comparison itself
      private const double TickSizeEpsilon = 1e-6;
      /// <summary>[BOOK-TOUCH] counters for the host's end line: limits filled by the book touch, book-only
      /// ticks that reached the limit check, limits still pending when a book-only tick arrived.</summary>
      public int TouchFills { get; private set; }
      public long BookOnlyTicksChecked { get; private set; }
      /// <summary>[NEXT-EVENT-FILL] counters for the host's end line: limits that were marketable on the
      /// tick after their submit tick and filled at that tick's price; stops filled on the tick after
      /// their trigger tick.</summary>
      public int MarketableFills { get; private set; }
      public int NextEventStopFills { get; private set; }
      /// <summary>[NT8-SECOND-GRID] the book GetCurrentBid/Ask answer while a second is dispatched.</summary>
      public double GridBid { get; private set; }
      public double GridAsk { get; private set; }
      private readonly List<ShimTick> _gridBatch = new();
      private DateTime _gridBatchEnd = DateTime.MinValue;   // exclusive end (whole second) of the batch being collected
      private string _gridSymbol;

      private static DateTime FloorSecond(DateTime ts)
         => new DateTime(ts.Ticks - ts.Ticks % TimeSpan.TicksPerSecond, ts.Kind);
      /// <summary>Start of the whole second after the one `ts` lies in, plus `delaySeconds - 1`.</summary>
      private static DateTime NextSecond(DateTime ts, int delaySeconds = 1)
         => FloorSecond(ts).AddSeconds(delaySeconds);
      // [ORDER-DELAY] the ONE place that says when a submitted order is working:
      // the grid moves it to the next whole second, OrderDelayMs adds the simulator's
      // latency on top. Both off = working at the submit event, as before.
      private DateTime ActivationFor(DateTime submitTs)
      {
         var ts = Nt8SecondGrid ? NextSecond(submitTs) : submitTs;
         return OrderDelayMs > 0 ? ts.AddMilliseconds(OrderDelayMs) : ts;
      }
      private DateTime LegActivationFor(DateTime submitTs)
      {
         if (Nt8SecondGrid)
            return NextSecond(submitTs, Nt8SecondGridLegDelaySeconds).AddMilliseconds(OrderDelayMs);
         // MinValue = "live immediately" for the t.Ts >= ActiveTs tests below.
         return OrderDelayMs > 0 ? submitTs.AddMilliseconds(OrderDelayMs) : DateTime.MinValue;
      }

      public bool ExitTriggerAtBbo { get; internal set; } = false;

      public bool EmulateFormingBar { get; internal set; } = false;

      public bool HostedIndicatorsFollowStrategyCalculate { get; internal set; } = false;

      public bool DriveIndicatorInputFirst { get; internal set; } = false;

      public bool PlotSeriesAtSetDefaults { get; internal set; } = false;

      /// <summary>True while the switch above applies: forming bar on and a
      /// hosted strategy with Calculate.OnEachTick.</summary>
      internal bool FormingViewForAllHosted
      {
         get
         {
            if (!HostedIndicatorsFollowStrategyCalculate || !EmulateFormingBar) return false;
            for (int i = 0; i < _hosted.Count; i++)
               if (_hosted[i].Calculate == Calculate.OnEachTick) return true;
            return false;
         }
      }

      public Func<BarsPeriodType, int, (NinjaTrader.NinjaScript.BarsType hosted, string periodTypeName)>
         SecondaryHostedBarsTypeResolver { get; set; }

      public bool DiscardNonTradeTicks { get; internal set; } = false;

      public bool Nt8LocalBarTimes { get; internal set; } = false;

      /// <summary>Fill price of a market order. `isBuy` = buy (long entry
      /// or short exit). Falls back to LastPrice when the BBO is missing
      /// — a tick without a book must not book a price of 0.</summary>
      private double FillPx(bool isBuy)
      {
         if (!FillAtBbo) return LastPrice;
         var px = isBuy ? LastAsk : LastBid;
         return px > 0 ? px : LastPrice;
      }
      public Account Account  { get; internal set; }

      // ---- Output ----------------------------------------------------------
      public Action<string> WriteLine { get; set; } = Console.WriteLine;

      private bool _warmupGateOpen;
      private int _warmupLastTicks;
      public bool SeriesWarmup { get; set; }
      public int WarmupEntriesRefused { get; private set; }
      // [NT8-UNMANAGED] last BBO values raised as Bid/Ask OnMarketData events.
      private double _lastBidEvent, _lastAskEvent;

      // ---- Host management -------------------------------------------------
      public void Host(NinjaScriptBase ns)
      {
         ns._runtime = this;
         ns.IsTickReplay = true;
         _hosted.Add(ns);
      }
      public void DriveLifecycle(NinjaScriptBase ns, State target)
         => ns.DispatchStateChange(target);

      public IReadOnlyList<NinjaScriptBase> HostedStrategies => _hosted;

      private ShimTick _curTick;
      private bool     _anyBarClosed;
      private Action   _onBarClosed;

      private int ClosedBarIndex
         => Math.Max(0, (_barStream != null ? _barStream.ClosedCount : (_bars?.Count ?? 0)) - 1);

      private void DispatchBoundaryForOnEachTick()
      {
         for (int i = 0; i < _hosted.Count; i++)
         {
            var ns = _hosted[i];
            if (ns.Calculate != Calculate.OnEachTick) continue;
            int idx = Math.Max(0, (_bars?.Count ?? 0) - 1);
            if (ns.CurrentBars != null && ns.CurrentBars.Length > 0)
               ns.CurrentBars[0] = idx;
            ns.CurrentBar       = idx;
            ns.BarsInProgress   = 0;
            ns.IsFirstTickOfBar = true;
            DriveOnEachTickIndicators();
            try { ns.DispatchBarUpdate(); }
            catch (Exception ex) { LogShimError(ns, "OnBarUpdate(boundary)", ex); }
            ns.IsFirstTickOfBar = false;
         }
      }

      private void DispatchBarClose()
      {
         _anyBarClosed = true;
         var t = _curTick;
         RaiseSeriesBarClosed(0, _bars, t.Ts);   // observers before the strategies (order: see the events)

         // [NO-CRUTCH] IsNewBar fires on every clock bar (no NT8 timeline gate).

         _closesThisTick++;
         bool _isNewSession =
            _closesThisTick == 1 ? _formingBarIsFirst
            : (_closesThisTick == 2 && _tickOpensNewSession);
         if (_closesThisTick == 1)
            _formingBarIsFirst = false;   // first-bar status is used up
         foreach (var ns in _hosted)
         {
            int _barIdx = ClosedBarIndex;   // NT8: INDEX, not count - the just-closed bar, [0] of the series here
            if (ns.CurrentBars != null && ns.CurrentBars.Length > 0)
               ns.CurrentBars[0] = _barIdx;
            ns.CurrentBar = _barIdx;
            ns.BarsInProgress = 0;   // primary series
            if (ns.Bars != null) ns.Bars.IsFirstBarOfSession = _isNewSession;
            ns.IsFirstTickOfBar = true;
            DriveHostedIndicators();   // bring indicators current before signal eval
            if (EmulateFormingBar && ns.Calculate == Calculate.OnEachTick)
            {
               ns.IsFirstTickOfBar = false;
               continue;
            }
            try { ns.DispatchBarUpdate(); }
            catch (Exception ex) { LogShimError(ns, "OnBarUpdate", ex); }
            ns.IsFirstTickOfBar = false;   // clear for secondary + per-tick OnMarketData passes
         }
      }

      public void PumpTick(ShimTick t, string symbol = null)
      {
         if (!Nt8SecondGrid) { PumpTickCore(t, symbol); return; }
         if (_gridBatch.Count > 0 && t.Ts >= _gridBatchEnd.AddMilliseconds(1)) FlushSecondGrid(t, hasNext: true);
         if (_gridBatch.Count == 0) { _gridBatchEnd = NextSecond(t.Ts); _gridSymbol = symbol; }
         _gridBatch.Add(t);
      }

      private void FlushSecondGrid(ShimTick next, bool hasNext)
      {
         double bid = 0, ask = 0;
         if (Nt8SecondGridQuote == "next" && hasNext && next.Bid > 0 && next.Ask > 0)
         {
            bid = next.Bid; ask = next.Ask;
         }
         else
         {
            for (int i = _gridBatch.Count - 1; i >= 0; i--)
               if (_gridBatch[i].Bid > 0 && _gridBatch[i].Ask > 0) { bid = _gridBatch[i].Bid; ask = _gridBatch[i].Ask; break; }
         }
         GridBid = bid; GridAsk = ask;
         var batch = _gridBatch.ToArray();
         _gridBatch.Clear();
         foreach (var b in batch) PumpTickCore(b, _gridSymbol);
      }

      public void FlushSecondGrid()
      {
         if (_gridBatch.Count > 0) FlushSecondGrid(null, hasNext: false);
      }

      private void PumpTickCore(ShimTick t, string symbol)
      {
         bool hasPrice = t.Price > 0;
         bool hasBbo   = t.Bid > 0 && t.Ask > 0;
         if (!hasPrice && !hasBbo) return;

         _prevBid = LastBid; _prevAsk = LastAsk;   // [SIDE-EVENTS] the book before this tick, see SideEventPx
         if (hasBbo) { LastBid = t.Bid; LastAsk = t.Ask; }
         if (hasPrice) LastPrice = t.Price;
         LastVol  = t.Volume;
         Now      = t.Ts;
         FlushDeferredOrderEvents();

         if (Nt8SecondGrid)
         {
            if (_openPositions.Count > 0) FillTriggeredStops(t);
            if (_pendingLimits.Count > 0) CheckMarketableLimits(t);
         }

         if (!hasPrice)
         {
            if ((Nt8SecondGrid || LimitBookTouch) && _pendingLimits.Count > 0) { BookOnlyTicksChecked++; CheckPendingLimits(t); }   // [LIMIT-BOOK-TOUCH]
            return;
         }

         if (DiscardNonTradeTicks && !t.IsLast) return;

         FinalisePendingEntries(t);

         // [LIMIT-ORDERS] fill any working limit whose price this tick reaches
         // (before SL/TP so a same-bar limit-fill→stop sequence is possible).
         CheckPendingLimits(t);

         // SL/TP check against any open position FIRST (tick-by-tick, before
         // the strategy sees the tick — mirrors NT8's order-fill priority).
         CheckOpenPositionsExit(t);

         if (!SeriesWarmup && !_warmupGateOpen)
         {
            // [QUOTE-DRIVEN] a quote-only stream has no Last to count - every
            // tick counts there (see QuoteDrivenExecution).
            if ((t.IsLast || QuoteDrivenExecution) && ++_warmupLastTicks >= 20)
            {
               _warmupGateOpen = true;
               foreach (var s in _hosted)
                  s.BarsRequiredToTrade = 1;
            }
         }
         if (!SeriesWarmup && UnmanagedNt8Events && (PlaybackMode || LiveMode) && _warmupGateOpen)
         {
            if (t.Bid > 0 && t.Bid != _lastBidEvent)
            {
               _lastBidEvent = t.Bid;
               var bev = new MarketDataEventArgs
               {
                  Time = t.Ts, Price = t.Bid, Bid = t.Bid, Ask = t.Ask,
                  Volume = 0, MarketDataType = MarketDataType.Bid,
               };
               foreach (var ns in _hosted)
               {
                  try { ns.DispatchMarketData(bev); }
                  catch (Exception ex) { LogShimError(ns, "OnMarketData(Bid)", ex); }
               }
            }
            if (t.Ask > 0 && t.Ask != _lastAskEvent)
            {
               _lastAskEvent = t.Ask;
               var aev = new MarketDataEventArgs
               {
                  Time = t.Ts, Price = t.Ask, Bid = t.Bid, Ask = t.Ask,
                  Volume = 0, MarketDataType = MarketDataType.Ask,
               };
               foreach (var ns in _hosted)
               {
                  try { ns.DispatchMarketData(aev); }
                  catch (Exception ex) { LogShimError(ns, "OnMarketData(Ask)", ex); }
               }
            }
         }
         var mev = new MarketDataEventArgs
         {
            Time = t.Ts, Price = t.Price, Bid = t.Bid, Ask = t.Ask,
            Volume = t.Volume, MarketDataType = MarketDataType.Last,
         };
         if (!SeriesWarmup && _warmupGateOpen)
            foreach (var ns in _hosted)
            {
               try { ns.DispatchMarketData(mev); }
               catch (Exception ex) { LogShimError(ns, "OnMarketData", ex); }
            }

         // Consolidate the tick into bars. A single tick can close SEVERAL bars
         // (a hosted bars type can finish several), so OnTick invokes DispatchBarClose
         // once per closed bar. Time and tick-count modes close at most one. The callback is
         // cached (_onBarClosed) to avoid a per-tick delegate allocation.
         _curTick      = t;
         _anyBarClosed = false;
         _onBarClosed ??= DispatchBarClose;
         {
            var _etLocal = ToSessionLocal(t.Ts);
            var _tdTick  = _etLocal.TimeOfDay.TotalMinutes >= 18 * 60
                           ? _etLocal.Date.AddDays(1) : _etLocal.Date;
            _tickOpensNewSession = _tdTick != _curSessionTradeDate;
            if (_tickOpensNewSession) _curSessionTradeDate = _tdTick;
            _closesThisTick = 0;
         }
         if (_barStream != null)
            _barStream.OnTick(t.Ts, t.Price, t.Bid, t.Ask, t.Volume, _onBarClosed);
         // After the closes: does the now FORMING bar belong to the new
         // session as its first bar? Only if this tick rolled AND the
         // first bar was not already closed again within the same tick
         // (_closesThisTick <= 1: at most the OLD bar was closed).
         if (_tickOpensNewSession)
            _formingBarIsFirst = _closesThisTick <= 1;
         SeriesTickConsolidated?.Invoke(0, t, _anyBarClosed);

         if (!_anyBarClosed)
         {
            for (int i = 0; EmulateFormingBar && i < _hosted.Count; i++)
            {
               var ns = _hosted[i];
               if (ns.Calculate != Calculate.OnEachTick) continue;
               int _formingIdx = Math.Max(0, (_bars?.Count ?? 0) - 1);
               if (ns.CurrentBars != null && ns.CurrentBars.Length > 0)
                  ns.CurrentBars[0] = _formingIdx;
               ns.CurrentBar      = _formingIdx;
               ns.BarsInProgress  = 0;
               ns.IsFirstTickOfBar = false;   // the first tick was the bar close
               DriveOnEachTickIndicators();   // NOT the OnBarClose indicators
               try { ns.DispatchBarUpdate(); }
               catch (Exception ex) { LogShimError(ns, "OnBarUpdate(OnEachTick)", ex); }
            }
         }
         else if (EmulateFormingBar)
         {
            DispatchBoundaryForOnEachTick();
         }
         SeriesTickDispatched?.Invoke(0, t);

         // ---- Secondary timeframe series (MTF) ---------------------------
         // Feed every secondary consolidator the same tick. On a secondary
         // bar boundary, advance that series' CurrentBars[i], set
         // BarsInProgress=i, and dispatch OnBarUpdate so the strategy (and,
         // once Phase-4 indicator hosting lands, its MTF indicators) update
         // on the secondary timeframe. Primary dispatch already happened above.

         for (int si = 0; si < _secondaries.Count; si++)
         {
            var sek       = _secondaries[si];
            int seriesIdx = 1 + si;                 // primary is 0
            bool formingDispatch = EmulateFormingBar && sek.stream.EmulateFormingBar;
            int  countBefore     = sek.bars.Count;
            bool secClosed       = false;
            sek.stream.OnTick(t.Ts, t.Price, t.Bid, t.Ask, t.Volume, () =>
            {
            secClosed = true;
            RaiseSeriesBarClosed(seriesIdx, sek.bars, t.Ts);   // observers before the strategies
            // [TIME-SECONDARY-FORMING] the just-closed bar's index from the stream's closed count: with
            // EmulateFormingBar a time secondary opens the next bar's forming slot BEFORE this callback
            // (BarStream time mode), so sek.bars.Count - 1 would be that forming bar; ClosedCount excludes the
            // open forming slot. On the hosted path the slot is dropped during the closes, so ClosedCount == Count
            // and the value is unchanged.
            int closedCount = sek.stream.ClosedCount - 1;
            if (StopTraceEnabled && si == 0)
               DispatchTrace.Add(new DispatchRow {
                  TickUtc = t.Ts, BarIdx = closedCount,
                  BarUtc  = closedCount >= 0 ? sek.bars.GetTime(closedCount) : default,
                  TickPx  = t.Price });
            foreach (var ns in _hosted)
            {
               if (ns.CurrentBars != null && seriesIdx < ns.CurrentBars.Length)
                  ns.CurrentBars[seriesIdx] = closedCount;
               if (formingDispatch && ns.Calculate == Calculate.OnEachTick)
               {
                  // [FORMING-SECONDARY] indicators advance on the finished bar
                  // here; the strategy sees this bar as [1] after the tick.
                  DriveHostedIndicators();
                  continue;
               }
               ns.CurrentBar = closedCount;
               ns.BarsInProgress = seriesIdx;
               ns.IsFirstTickOfBar = true;
               DriveHostedIndicators();
               try { ns.DispatchBarUpdate(); }
               catch (Exception ex) { LogShimError(ns, "OnBarUpdate(secondary)", ex); }
               ns.IsFirstTickOfBar = false;   // clear it as in the primary path
            }
            });
            SeriesTickConsolidated?.Invoke(seriesIdx, t, secClosed);
            if (formingDispatch && t.Price > 0)
            {
               // [FORMING-SECONDARY] the strategy's calls for this tick: one per
               // bar the tick ADDED (first tick of each, [0] = that bar), or one
               // on the forming bar (not its first tick). Count includes the
               // forming slot (mirrored again after the closes), so the new bars
               // are exactly the indices countBefore .. Count-1.
               int countAfter = sek.bars.Count;
               int last       = countAfter - 1;
               if (last >= 0)
                  foreach (var ns in _hosted)
                  {
                     if (ns.Calculate != Calculate.OnEachTick) continue;
                     if (ns.CurrentBars == null || seriesIdx >= ns.CurrentBars.Length) continue;
                     if (countAfter > countBefore)
                        for (int b = countBefore; b <= last; b++)
                           DispatchSecondaryFormingTick(ns, sek.bars, sek.stream, seriesIdx, b, firstTick: true);
                     else if (sek.bars.FormingSlotMirrored)
                        DispatchSecondaryFormingTick(ns, sek.bars, sek.stream, seriesIdx, last, firstTick: false);
                  }
            }
            SeriesTickDispatched?.Invoke(seriesIdx, t);
         }
         if (_secondaries.Count > 0)
            foreach (var ns in _hosted)
            {
               ns.BarsInProgress = 0;
               ns.CurrentBar     = (EmulateFormingBar && ns.Calculate == Calculate.OnEachTick)
                                   ? Math.Max(0, (_bars?.Count ?? 0) - 1) : ClosedBarIndex;
            }

         foreach (var ns in _hosted)
            if (ns.Bars != null)
               ns.Bars.IsFirstBarOfSession = _formingBarIsFirst;

         TickCompleted?.Invoke(t);
      }

      /// <summary>One pre-aggregated bid/ask-aware bar (runner-side DTO).</summary>
      public sealed class ShimBar
      {
         public DateTime StartUtc, EndUtc;
         public double LastO, LastH, LastL, LastC;
         public double BidO,  BidH,  BidL,  BidC;
         public double AskO,  AskH,  AskL,  AskC;
         public long AskVol, BidVol;
         public IReadOnlyDictionary<int, long> AskLevels, BidLevels;
         public bool LongDirection = true;
      }

      public void PumpFootprintBar(ShimBar b)
      {
         if (b.LastC <= 0)
            return;
         // Inject the PREVIOUS bar's aggregates so the boundary tick that is
         // about to close it rolls the correct accumulators into history.

         bool lng = b.LongDirection;
         PumpTick(new ShimTick { Ts = b.StartUtc,
            Price = b.LastO, Bid = b.BidO, Ask = b.AskO, Volume = 0 });
         PumpTick(new ShimTick { Ts = b.StartUtc.AddMilliseconds(1),
            Price = lng ? b.LastL : b.LastH,
            Bid   = lng ? b.BidL  : b.BidH,
            Ask   = lng ? b.AskL  : b.AskH, Volume = 0 });
         PumpTick(new ShimTick { Ts = b.StartUtc.AddMilliseconds(2),
            Price = lng ? b.LastH : b.LastL,
            Bid   = lng ? b.BidH  : b.BidL,
            Ask   = lng ? b.AskH  : b.AskL, Volume = 0 });
         PumpTick(new ShimTick { Ts = b.EndUtc.AddMilliseconds(-1),
            Price = b.LastC, Bid = b.BidC, Ask = b.AskC,
            Volume = b.AskVol + b.BidVol });
      }

      // Called by the runner when a bar boundary crosses — fires OnBarUpdate.
      public void FireBarUpdate(int barsInProgress = 0)
      {
         foreach (var ns in _hosted)
         {
            try { ns.DispatchBarUpdate(); }
            catch (Exception ex) { LogShimError(ns, "OnBarUpdate", ex); }
         }
      }

      /// <summary>Session-end timezone = the exchange's LOCAL zone (e.g. "Eastern Standard Time" for CME US
      /// Index Futures; the session end 17:00 is local time, so its UTC instant moves with the zone's
      /// daylight-saving changes). Used by CheckOpenPositionsExit (when the daily session close arrives),
      /// ToSessionLocal and the trading hours every bar series gets (NewSessionTradingHours).
      /// [NO-UTC-DEFAULT] No default any more (it was TimeZoneInfo.Utc): unset = every reader stops
      /// (RequireSessionZone), never a silent fallback to UTC.</summary>
      public TimeZoneInfo SessionEndTimeZone { get; set; }

      /// <summary>[NO-UTC-DEFAULT] The session zone, or a stop naming the reader - never UTC in its place.</summary>
      public TimeZoneInfo RequireSessionZone(string reader)
         => SessionEndTimeZone ?? throw new InvalidOperationException(
               $"[SHIM] {reader}: the runtime has no SessionEndTimeZone (the exchange's local zone, e.g. "
             + "\"Eastern Standard Time\" for CME). No fallback to UTC.");

      /// <summary>[NO-UTC-DEFAULT] The trading hours EVERY bar series of this runtime carries - primary (time,
      /// tick-count, hosted bar type) and secondary alike: the session zone and SessionEndMinuteOfDay, the two
      /// values the forced session close uses too (one source). Until then only a HOSTED primary got them;
      /// every other series kept Bars' default (zone UTC, minute 0), so a SessionIterator on it computed its
      /// boundaries at 00:00 UTC.</summary>
      public NinjaTrader.Data.TradingHours NewSessionTradingHours()
         => new NinjaTrader.Data.TradingHours
            {
               Name                  = "ShimSession",
               SessionEndTimeZone    = RequireSessionZone("session trading hours of a bar series"),
               SessionEndMinuteOfDay = SessionEndMinuteOfDay,
            };

      public int SessionEndMinuteOfDay { get; set; } = 1020;

      /// <summary>The latest tick observed with localMinute &lt;
      /// SessionEndMinuteOfDay. Reset at the start of each new
      /// session-local day. Used as the exit-Ts/Price for session-end
      /// emergency closes.</summary>
      private DateTime _lastTickTsBeforeSessionEnd = DateTime.MinValue;

      private DateTime _anchorSessionDate = DateTime.MinValue;
      private double   _lastTickPxBeforeSessionEnd;
      private double   _lastTickBidBeforeSessionEnd;
      private double   _lastTickAskBeforeSessionEnd;
      /// <summary>Local date on which the most recent session-end close
      /// already fired (in SessionEndTimeZone). Guarantees one emergency-
      /// close per local session-day.</summary>
      private DateTime _lastSessionEndCloseDate = DateTime.MinValue;
      private DateTime _curSessionTradeDate = DateTime.MinValue;
      private bool     _formingBarIsFirst;      // forming bar = session's first bar
      private bool     _tickOpensNewSession;    // THIS tick rolled the trading day
      private int      _closesThisTick;         // DispatchBarClose counter per tick

      public DateTime ToSessionLocal(DateTime utcTs)
      {
         var zone = RequireSessionZone("ToSessionLocal");   // [NO-UTC-DEFAULT]
         return zone == TimeZoneInfo.Utc
            ? utcTs
            : TimeZoneInfo.ConvertTime(DateTime.SpecifyKind(utcTs, DateTimeKind.Utc), zone);
      }

      public int TickDrivenSessionCloses { get; private set; }

      public int CloseSessionPositionsBeforeClose(ShimTick t)
      {
         if (_openPositions.Count == 0) return 0;
         var localT = ToSessionLocal(t.Ts);
         var boundaryLocal = localT.Date.AddMinutes(SessionEndMinuteOfDay);
         if (localT >= boundaryLocal)
            throw new InvalidOperationException(
               $"[EXEC-SESSION-CLOSE] tick {t.Ts:yyyy-MM-dd HH:mm:ss.fff}Z lies at/after the session end "
               + $"{boundaryLocal:yyyy-MM-dd HH:mm} (session zone) - the close before the end needs a tick before it.");
         return CloseSessionPositions(t.Ts, t.Price, t.Bid, t.Ask, boundaryLocal);
      }

      private double SessionClosePx(ShimPosition pos, double px, double bid, double ask)
      {
         if (!FillAtBbo) return px;
         var side = pos.IsLong ? bid : ask;
         return side > 0 ? side : px;
      }

      /// <summary>[SESSION-CLOSE-GATE] true when the strategy that owns <paramref name="pos"/> has NinjaTrader's
      /// "Exit on session close" (IsExitOnSessionCloseStrategy) set - only such a position is closed at the session
      /// end, as in NinjaTrader; with the switch off NinjaTrader holds the position over the session end and its stop
      /// and target stay active. The owner is the position's own Owner (unmanaged path), or else the single hosted
      /// strategy (the managed path leaves ShimPosition.Owner null; a host runs exactly one strategy). No owner to
      /// ask is an error, never a silent default.</summary>
      private bool ClosesAtSessionEnd(ShimPosition pos)
      {
         var owner = pos.Owner;
         if (owner == null)
         {
            if (_hosted.Count != 1)
               throw new InvalidOperationException(
                  $"[SESSION-CLOSE-GATE] position '{pos.SignalName}' has no owning strategy and the runtime hosts "
                  + $"{_hosted.Count} - no strategy to ask for IsExitOnSessionCloseStrategy.");
            owner = _hosted[0];
         }
         if (owner is not NinjaTrader.NinjaScript.Strategies.Strategy strat)
            throw new InvalidOperationException(
               $"[SESSION-CLOSE-GATE] the owner of position '{pos.SignalName}' is a {owner.GetType().Name}, "
               + "not a strategy - no IsExitOnSessionCloseStrategy to ask.");
         return strat.IsExitOnSessionCloseStrategy;
      }

      /// <summary>[SESSION-CLOSE-GATE] true when at least one open position is closed at the session end.</summary>
      private bool AnyPositionClosesAtSessionEnd()
      {
         foreach (var (_, pos) in _openPositions)
            if (ClosesAtSessionEnd(pos)) return true;
         return false;
      }

      private int CloseSessionPositions(DateTime emergencyTs, double emergencyPx, double emergencyBid,
                                        double emergencyAsk, DateTime boundaryLocal)
      {
         List<string> toClose = null;
         List<ShimPosition> closedPositions = null;
         foreach (var (label, pos) in _openPositions)
         {
            if (ToSessionLocal(pos.EntryTs) >= boundaryLocal) continue;
            if (!ClosesAtSessionEnd(pos)) continue;   // [SESSION-CLOSE-GATE] NinjaTrader holds it over the end
            TradeLedger.Add(new ShimTrade
            {
               Idx = TradeLedger.Count + 1,
               SignalName = pos.SignalName,
               EntryTs    = pos.EntryTs,
               EntryPrice = pos.EntryPrice,
               ExitTs     = emergencyTs,
               ExitPrice  = SessionClosePx(pos, emergencyPx, emergencyBid, emergencyAsk),   // [SESSION-CLOSE-BBO]
               Quantity   = pos.Quantity,
               IsLong     = pos.IsLong,
               Commission = CommissionFor(pos.Quantity),

                  EntryBarIdx = pos.EntryBarIdx,

                  ExitBarIdx  = _bars?.Count ?? 0,
               ExitReason = "EmergencyClose",
            });
            AppendStrategyTrade(TradeLedger[TradeLedger.Count - 1], "EmergencyClose");
            (toClose ??= new List<string>()).Add(label);
            (closedPositions ??= new List<ShimPosition>()).Add(pos);
            // Flatten Lean's position to match.
            if (pos.IsLong)  OrderRouter?.ExitLong(pos.Quantity, "EmergencyClose", pos.SignalName);
            else             OrderRouter?.ExitShort(pos.Quantity, "EmergencyClose", pos.SignalName);
            if (UnmanagedNt8Events && pos.RaiseOrderEvents)
            {
               _limitOrigin.Remove(pos.SignalName);   // [NT8-UNMANAGED] events below
               continue;
            }
            if (_limitOrigin.Remove(pos.SignalName))
               FireLimitExit(pos, "EmergencyClose");
            else if (ExecutionsFromLedger)   // [EXECUTIONS-FROM-LEDGER] exit at the ledger price
               RaiseLedgerFill(pos.Owner, isEntry: false, signal: null, fromSignal: pos.SignalName,
                               exitSignal: "EmergencyClose", isLong: pos.IsLong, qty: pos.Quantity,
                               price: TradeLedger[TradeLedger.Count - 1].ExitPrice, tsUtc: emergencyTs);
         }
         bool nt8Close = UnmanagedNt8Events && closedPositions != null
                         && closedPositions.Exists(p => p.RaiseOrderEvents);
         if (toClose != null)
         {
            foreach (var l in toClose) _openPositions.Remove(l);
            if (!nt8Close) SyncHostedPositions();
         }
         if (closedPositions != null && !nt8Close)
            foreach (var pos in closedPositions)
               if (pos.RaiseOrderEvents)
               {
                  FireOrderUpdate(pos.Owner, pos.StopOrder,   OrderState.Cancelled, 0, 0);
                  FireOrderUpdate(pos.Owner, pos.TargetOrder, OrderState.Cancelled, 0, 0);
               }
         if (nt8Close)
         {
            bool mpChanged = SyncHostedPositionFields(out int net, out double lastEntry, out var newMp);
            foreach (var pos in closedPositions)
            {
               if (!pos.RaiseOrderEvents) continue;
               FireOrderUpdate(pos.Owner, pos.StopOrder,   OrderState.Cancelled, 0, 0);
               FireOrderUpdate(pos.Owner, pos.TargetOrder, OrderState.Cancelled, 0, 0);
               var closeOrder = new NinjaTrader.Cbi.Order
               {
                  OrderAction = pos.IsLong ? OrderAction.Sell : OrderAction.BuyToCover,
                  OrderType   = OrderType.Market,
                  Quantity    = pos.Quantity,
                  Name        = "Exit on session close",
                  FromEntrySignal = pos.SignalName,
                  Instrument  = DefaultInstrument,
                  Time        = emergencyTs,
               };
               var closePx = SessionClosePx(pos, emergencyPx, emergencyBid, emergencyAsk);   // [SESSION-CLOSE-BBO]
               FireOrderUpdate(pos.Owner, closeOrder, OrderState.Filled, closePx, pos.Quantity);
               FireExitExecution(pos, closeOrder, closeOrder.Name, closePx, newMp);
            }
            if (mpChanged) RaiseHostedPositionUpdates(net, lastEntry, newMp);
         }
         return closedPositions?.Count ?? 0;
      }

      // Check open positions against latest tick — exit on SL/TP hit OR
      // by the emergency close at the daily session end (the latter only for a
      // strategy with IsExitOnSessionCloseStrategy — [SESSION-CLOSE-GATE]).
      private void CheckOpenPositionsExit(ShimTick t)
      {
         var localT  = ToSessionLocal(t.Ts);
         var localMin = (int)localT.TimeOfDay.TotalMinutes;
         bool isAtOrPastSessionEnd = localMin >= SessionEndMinuteOfDay;

         bool closeOnSameDay = isAtOrPastSessionEnd
                               && localT.Date > _lastSessionEndCloseDate
                               && _anchorSessionDate == localT.Date;
         bool closeOnSessionRollover = _anchorSessionDate != DateTime.MinValue
                               && localT.Date > _anchorSessionDate
                               && _anchorSessionDate > _lastSessionEndCloseDate;
         // [SESSION-CLOSE-GATE] only positions of a strategy with IsExitOnSessionCloseStrategy count:
         // when none is open the block is skipped as with no position at all - the anchor keeps tracking
         // and the stop/target check below runs on this tick.
         if ((closeOnSameDay || closeOnSessionRollover)
             && _lastTickTsBeforeSessionEnd != DateTime.MinValue
             && _openPositions.Count > 0
             && AnyPositionClosesAtSessionEnd())
         {
            _lastSessionEndCloseDate = closeOnSessionRollover ? _anchorSessionDate : localT.Date;
            TickDrivenSessionCloses += CloseSessionPositions(_lastTickTsBeforeSessionEnd, _lastTickPxBeforeSessionEnd,
               _lastTickBidBeforeSessionEnd, _lastTickAskBeforeSessionEnd,
               localT.Date.AddMinutes(SessionEndMinuteOfDay));
            // Reset the tracker so a stale value can't bleed across days.
            _lastTickTsBeforeSessionEnd = DateTime.MinValue;
            _anchorSessionDate = DateTime.MinValue;   // [STALE-ANCHOR-FIX]
            return;
         }

         if (!isAtOrPastSessionEnd)
         {
            _lastTickTsBeforeSessionEnd = t.Ts;
            _lastTickPxBeforeSessionEnd = t.Price;
            _lastTickBidBeforeSessionEnd = t.Bid;   // [SESSION-CLOSE-BBO]
            _lastTickAskBeforeSessionEnd = t.Ask;
            _anchorSessionDate = localT.Date;   // [STALE-ANCHOR-FIX] track it along
         }

         List<string> slTpClose = null;
         List<(ShimPosition pos, string reason, double exitPx)> nt8Exits = null;   // [NT8-UNMANAGED]
         foreach (var (label, pos) in _openPositions)
         {
            string reason = null;
            double exitPx = 0;
            // [NEXT-EVENT-FILL] a stop already triggered fills in FillTriggeredStops; no further check here
            if (pos.StopTriggerTs != DateTime.MinValue) continue;
            // [NT8-SECOND-GRID] a stop/target change becomes effective at its whole second; a leg is
            // evaluated only from its activation second on (both MinValue/NaN when the grid is off).
            bool slLive = true, tpLive = true;
            if (OrderActivationInUse)   // [ORDER-DELAY] the legs are latent too
            {
               if (!double.IsNaN(pos.PendingStopPx) && t.Ts >= pos.PendingStopActiveTs)
               { pos.StopLossPx = pos.PendingStopPx; pos.PendingStopPx = double.NaN; }
               if (!double.IsNaN(pos.PendingTargetPx) && t.Ts >= pos.PendingTargetActiveTs)
               { pos.TakeProfitPx = pos.PendingTargetPx; pos.PendingTargetPx = double.NaN; }
               slLive = t.Ts >= pos.StopActiveTs;
               tpLive = t.Ts >= pos.TargetActiveTs;
            }
            double trigPx = t.Price;
            // [NT8-SECOND-GRID] NT8's Market Replay triggers a stop on the LAST print and fills the
            // resulting market order on the next market data event (FillTriggeredStops) - so on the
            // grid the trigger stays the trade price even with ExitTriggerAtBbo.
            if (ExitTriggerAtBbo && !Nt8SecondGrid)
            {
               var side = pos.IsLong ? t.Bid : t.Ask;
               if (side > 0) trigPx = side;
            }
            bool stopHit = pos.IsLong ? (slLive && trigPx <= pos.StopLossPx) : (slLive && trigPx >= pos.StopLossPx);
            if (stopHit)
            {
               // [NEXT-EVENT-FILL] on the grid the stop is only TRIGGERED here; its market order fills on
               // the next market data event at that event's price (FillTriggeredStops). Off the grid the
               // level is booked at once, as before.
               if (Nt8SecondGrid) { pos.StopTriggerTs = t.Ts; continue; }
               reason = "SL"; exitPx = pos.StopLossPx;
            }
            else if (pos.IsLong ? (tpLive && trigPx >= pos.TakeProfitPx) : (tpLive && trigPx <= pos.TakeProfitPx))
            {
               reason = "TP"; exitPx = pos.TakeProfitPx;
            }
            if (reason != null)
               BookExit(label, pos, reason, exitPx, t.Ts, ref slTpClose, ref nt8Exits);
         }
         FinishExits(slTpClose, nt8Exits);
      }

      private void BookExit(string label, ShimPosition pos, string reason, double exitPx, DateTime exitTs,
                            ref List<string> closed, ref List<(ShimPosition pos, string reason, double exitPx)> nt8Exits)
      {
         TradeLedger.Add(new ShimTrade
         {
            Idx = TradeLedger.Count + 1,
            SignalName = pos.SignalName,
            EntryTs    = pos.EntryTs,
            EntryPrice = pos.EntryPrice,
            ExitTs     = exitTs,
            ExitPrice  = exitPx,
            Quantity   = pos.Quantity,
            IsLong     = pos.IsLong,
            Commission = CommissionFor(pos.Quantity),
            EntryBarIdx = pos.EntryBarIdx,
            ExitBarIdx  = _bars?.Count ?? 0,
            ExitReason = reason,
         });
         AppendStrategyTrade(TradeLedger[TradeLedger.Count - 1], reason);
         (closed ??= new List<string>()).Add(label);
         if (pos.IsLong)  OrderRouter?.ExitLong(pos.Quantity, reason, pos.SignalName);
         else             OrderRouter?.ExitShort(pos.Quantity, reason, pos.SignalName);
         if (UnmanagedNt8Events && pos.RaiseOrderEvents)
         {
            // [NT8-UNMANAGED] events fire AFTER the position is removed
            // (FinishExits), in NT8's order; nothing else here.
            _limitOrigin.Remove(pos.SignalName);
            (nt8Exits ??= new List<(ShimPosition, string, double)>()).Add((pos, reason, exitPx));
            return;
         }
         if (_limitOrigin.Remove(pos.SignalName))
            FireLimitExit(pos, reason);
         else if (ExecutionsFromLedger)   // [EXECUTIONS-FROM-LEDGER] exit at the ledger price
            RaiseLedgerFill(pos.Owner, isEntry: false, signal: null, fromSignal: pos.SignalName,
                            exitSignal: reason, isLong: pos.IsLong, qty: pos.Quantity,
                            price: exitPx, tsUtc: exitTs);
         if (pos.RaiseOrderEvents)
         {
            var leg = ExitLegFor(pos, reason);
            FireOrderUpdate(pos.Owner, leg, OrderState.Filled, exitPx, pos.Quantity);
         }
      }

      /// <summary>Remove the positions BookExit closed and raise the position/execution events
      /// (nothing when `closed` is null).</summary>
      private void FinishExits(List<string> closed, List<(ShimPosition pos, string reason, double exitPx)> nt8Exits)
      {
         if (closed == null) return;
         foreach (var l in closed) _openPositions.Remove(l);
         if (nt8Exits == null) { SyncHostedPositions(); return; }
         // [NT8-UNMANAGED] Position first, then per closed position
         // OnOrderUpdate(Filled) on the bracket leg and ONE execution with
         // the position AFTER the fill, then OnPositionUpdate.
         bool mpChanged = SyncHostedPositionFields(out int net, out double lastEntry, out var newMp);
         foreach (var (pos, reason, exitPx) in nt8Exits)
         {
            var leg = ExitLegFor(pos, reason);
            FireOrderUpdate(pos.Owner, leg, OrderState.Filled, exitPx, pos.Quantity);
            FireExitExecution(pos, leg, leg?.Name ?? reason, exitPx, newMp);
         }
         if (mpChanged) RaiseHostedPositionUpdates(net, lastEntry, newMp);
      }

      /// <summary>[NEXT-EVENT-FILL] fill the market orders of the stops CheckOpenPositionsExit triggered
      /// on an earlier tick: the price of the first tick of the ORDER'S side after the trigger tick -
      /// its last when it is a trade, its bid (long exit) / ask (short exit) when it is a quote-only
      /// tick (a quote of the other side only is skipped, SideEventPx). Runs for every tick,
      /// before the tick's own trigger checks (see PumpTickCore). Grid only.</summary>
      private void FillTriggeredStops(ShimTick t)
      {
         List<string> closed = null;
         List<(ShimPosition pos, string reason, double exitPx)> nt8Exits = null;
         foreach (var (label, pos) in _openPositions)
         {
            if (pos.StopTriggerTs == DateTime.MinValue || t.Ts <= pos.StopTriggerTs) continue;
            // a long exits with a sell, a short with a buy: the order's side is the opposite of the position's
            double px = SideEventPx(t, orderIsBuy: !pos.IsLong);
            if (px <= 0) continue;   // a quote event of the other side only - not this order's event
            // [MARKET-EXIT-NEXT-EVENT] a market exit the strategy submitted fills the same way, under its name
            string reason = pos.PendingExitOrder != null
               ? (string.IsNullOrEmpty(pos.PendingExitOrder.Name) ? (pos.IsLong ? "ExitLong" : "ExitShort") : pos.PendingExitOrder.Name)
               : "SL";
            // see Nt8SecondGridStopFillWorst
            if (Nt8SecondGridStopFillWorst && reason == "SL")
            {
               double own = t.Price > 0 ? (pos.IsLong ? t.Bid : t.Ask) : 0;
               if (pos.IsLong) { px = Math.Min(px, pos.StopLossPx); if (own > 0) px = Math.Min(px, own); }
               else            { px = Math.Max(px, pos.StopLossPx); if (own > 0) px = Math.Max(px, own); }
            }
            NextEventStopFills++;
            WriteLine?.Invoke($"[STOP-FILL] {(pos.IsLong ? "L" : "S")} sig='{pos.SignalName}' {(reason == "SL" ? $"stop={pos.StopLossPx:F2}" : $"market '{reason}'")} "
                            + $"trigger {pos.StopTriggerTs:yyyy-MM-dd HH:mm:ss.ffffff} fill {t.Ts:HH:mm:ss.ffffff} px={px:F2} on {(t.Price > 0 ? "last" : "book")}");
            BookExit(label, pos, reason, px, t.Ts, ref closed, ref nt8Exits);
         }
         FinishExits(closed, nt8Exits);
      }

      /// <summary>the order whose fill closes the position: a market exit the strategy submitted itself,
      /// else the bracket leg the reason names.</summary>
      private static Order ExitLegFor(ShimPosition pos, string reason)
         => pos.PendingExitOrder ?? (reason == "SL" ? pos.StopOrder : pos.TargetOrder);

      // ---- Output forwarders -----------------------------------------------
      public void Print(NinjaScriptBase from, string msg)
         => WriteLine?.Invoke($"[{from.GetType().Name}] {msg}");
      public void Log(NinjaScriptBase from, string msg, LogLevel level)
         => WriteLine?.Invoke($"[{from.GetType().Name}][{level}] {msg}");
      public void LogShimError(NinjaScriptBase from, string hook, Exception ex)
      {
         WriteLine?.Invoke($"[SHIM][{from.GetType().Name}][{hook}] {ex.GetType().Name}: {ex.Message}");
         if (VerboseErrors && ex.StackTrace != null)
         {
            foreach (var line in ex.StackTrace.Split('\n'))
            {
               WriteLine?.Invoke("    " + line.TrimEnd());
            }
         }
      }

      /// <summary>If true, LogShimError prints the full stack trace.</summary>
      public bool VerboseErrors { get; set; }

      /// <summary>Counter: how many OnBarUpdate dispatches actually fired.</summary>
      public int BarUpdateCallCount { get; private set; }
      internal void NotifyBarUpdateCalled(NinjaScriptBase ns) => BarUpdateCallCount++;

      // ---- Data subscriptions --------------------------------------------
      public void AddDataSeries(string instrument, BarsPeriodType type, int value, MarketDataType mdt)
      {
         if (value <= 0) return;

         TimeSpan period = TimeSpan.Zero;
         NinjaTrader.NinjaScript.BarsType hosted = null;
         string hostedName = null;
         switch (type)
         {
            case BarsPeriodType.Second: period = TimeSpan.FromSeconds(value); break;
            case BarsPeriodType.Minute: period = TimeSpan.FromMinutes(value); break;
            case BarsPeriodType.Day:    period = TimeSpan.FromDays(value);    break;
            case BarsPeriodType.Range:
#if NINJASHIM_NT8_BARSTYPES
               hosted = new NinjaTrader.NinjaScript.BarsTypes.RangeBarsType(); break;
#else
               throw new NotSupportedException(
                  "[SHIM] AddDataSeries: Range as a secondary series requires NT8's "
                + "@RangeBarsType.cs. This build does not link it (symbol "
                + "NINJASHIM_NT8_BARSTYPES missing) - add the source to the .csproj, "
                + "no rebuild.");
#endif
            case BarsPeriodType.Renko:
#if NINJASHIM_NT8_BARSTYPES
               hosted = new NinjaTrader.NinjaScript.BarsTypes.RenkoBarsType(); break;
#else
               throw new NotSupportedException(
                  "[SHIM] AddDataSeries: Renko as a secondary series requires NT8's "
                + "@RenkoBarsType.cs. This build does not link it (symbol "
                + "NINJASHIM_NT8_BARSTYPES missing) - add the source to the .csproj, "
                + "no rebuild.");
#endif
            case BarsPeriodType.Tick:
#if NINJASHIM_NT8_BARSTYPES
               hosted = new NinjaTrader.NinjaScript.BarsTypes.TickBarsType(); break;
#else
               throw new NotSupportedException(
                  "[SHIM] AddDataSeries: Tick as a secondary series requires NT8's "
                + "@TickBarsType.cs. This build does not link it (symbol "
                + "NINJASHIM_NT8_BARSTYPES missing) - add the source to the .csproj, "
                + "no rebuild.");
#endif
            default:
            {
               var resolved = SecondaryHostedBarsTypeResolver?.Invoke(type, value)
                              ?? (hosted: null, periodTypeName: null);
               if (resolved.hosted != null)
               {
                  hosted = resolved.hosted;
                  hostedName = resolved.periodTypeName;
                  break;
               }
               // 🔴 FAIL-LOUD: a registered series that does not
               // exist makes every number about it worthless — and unnoticed at that.
               throw new NotSupportedException(
                  $"[SHIM] AddDataSeries: bars type {type} ({value}) is not served as a "
                + "secondary series. Time based (Second/Minute/Day), hosted NT8 "
                + "bars types (Renko/Tick/Range) and what SecondaryHostedBarsTypeResolver "
                + "serves are possible. No silent skip - the bot would otherwise "
                + "have carried on without its series.");
            }
         }

         if (_bars?.BarsPeriod != null &&
             _bars.BarsPeriod.BarsPeriodType == type && _bars.BarsPeriod.Value == value)
            return;
         foreach (var sec in _secondaries)
            if (sec.bars.BarsPeriod.BarsPeriodType == type && sec.bars.BarsPeriod.Value == value)
               return;

         var secBars = new NinjaTrader.Data.Bars
         {
            Instrument = _bars?.Instrument ?? DefaultInstrument,
            BarsPeriod = new NinjaTrader.Data.BarsPeriod { BarsPeriodType = type, Value = value },
            FromDate   = _bars?.FromDate ?? default,
            ToDate     = _bars?.ToDate ?? default,
            TradingHours = NewSessionTradingHours(),   // [NO-UTC-DEFAULT] the session of every series from the one source
         };
         BarStream secStream;
         if (hosted != null)
         {
            // Shadow series as for the primary series: there the bar type works
            // with NT8 semantics (last bar = forming one), only the finished
            // bars are mirrored.
            var secShadow = new NinjaTrader.Data.Bars
            {
               Instrument = secBars.Instrument,
               BarsPeriod = new NinjaTrader.Data.BarsPeriod
                            { BarsPeriodType = type, Value = value },
               IsResetOnNewTradingDay = true,
               // [NO-UTC-DEFAULT] was `_bars?.TradingHours ?? new TradingHours()` (UTC midnight); secBars now
               // carries the real session hours (NewSessionTradingHours).
               TradingHours = secBars.TradingHours,
            };
            if (hostedName != null)
            {
               secBars.BarsPeriod.BarsPeriodTypeName = hostedName;
               secShadow.BarsPeriod.BarsPeriodTypeName = hostedName;
            }
            secStream = new BarStream(secBars, hosted, secShadow)
            {
               SessionTrace = msg => WriteLine?.Invoke(msg),
               EmulateFormingBar = EmulateFormingBar,
            };
            WriteLine?.Invoke($"[SHIM][SECONDARY-HOSTED] {hosted.GetType().Name} "
                            + $"({type} {value}) created as a secondary series.");
         }
         // [TIME-SECONDARY-FORMING] a time secondary carries its forming bar like the primary
         // (AttachPrimaryBars) and the hosted secondaries above: under a Calculate.OnEachTick strategy NT8
         // delivers [0] = the forming bar and [1] = the bar that just closed. Without it [0] was the last
         // CLOSED bar, so an indicator on the series read [1] one bar too old.
         else secStream = new BarStream(secBars, period) { EmulateFormingBar = EmulateFormingBar };
         _secondaries.Add((secBars, secStream));

         // NT8: BarsArray index = AddDataSeries call order (primary = 0).
         // Append to every hosted strategy + the indicator factory so created
         // indicators index the full array.
         foreach (var ns in _hosted)
         {
            ns.BarsArray   = AppendArray(ns.BarsArray, secBars);
            ns.CurrentBars = AppendArray(ns.CurrentBars, -1);
         }
         WriteLine?.Invoke($"[SHIM] AddDataSeries: secondary {type} {value} → BarsArray[{_secondaries.Count}].");
      }

      private static T[] AppendArray<T>(T[] arr, T item)
      {
         int n = arr?.Length ?? 0;
         var grown = new T[n + 1];
         if (n > 0) System.Array.Copy(arr, grown, n);
         grown[n] = item;
         return grown;
      }

      public void RegisterHostedIndicator(
         NinjaTrader.NinjaScript.Indicators.Indicator indi,
         NinjaTrader.NinjaScript.ISeries<double> input,
         NinjaTrader.NinjaScript.NinjaScriptBase caller)
      {
         if (indi == null) return;
         var host = _hosted.Count > 0 ? _hosted[0] : caller;

         indi._runtime    = this;
         indi.Bars        = host?.Bars;
         indi.BarsArray   = host?.BarsArray;
         indi.CurrentBars = host?.CurrentBars;
         if (input != null) indi.Input = input;

         indi._hostIdx = ResolveHostIndex(indi.Input, host, caller);

         if (host?.BarsArray != null && indi._hostIdx >= 0
             && indi._hostIdx < host.BarsArray.Length)
            indi.Bars = host.BarsArray[indi._hostIdx];

         var target = host?.State ?? NinjaTrader.NinjaScript.State.DataLoaded;

         var nspSnapshot = new System.Collections.Generic.List<(System.Reflection.PropertyInfo, object)>();
         foreach (var p in indi.GetType().GetProperties(
                     System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
            if (p.CanRead && p.CanWrite && HasNinjaScriptPropertyAttribute(p))
               nspSnapshot.Add((p, p.GetValue(indi)));

         DriveIndicatorLifecycle(indi, target, afterSetDefaults: () =>
         {
            foreach (var (p, v) in nspSnapshot)
               try { p.SetValue(indi, v); } catch { /* type quirk → keep SetDefaults value */ }
            if (PlotSeriesAtSetDefaults && indi.Plots != null && indi.Plots.Count > 0)
            {
               var early = new NinjaTrader.NinjaScript.Indicators.Series<double>[indi.Plots.Count];
               for (int i = 0; i < early.Length; i++)
                  early[i] = i < indi.Values.Length && indi.Values[i] != null
                           ? indi.Values[i]
                           : new NinjaTrader.NinjaScript.Indicators.Series<double>();
               indi.Values = early;
            }
         });

         int plots = indi.Plots != null ? indi.Plots.Count : 0;
         if (plots == 0)
         {
            // No AddPlot: in NT8 Values.Length would be 0 here and every access
            // to Values[0] an index error. That happens only with shim-own
            // classes without an NT8 original; they get ONE series and are
            // reported by name, instead of silently inheriting a wrong column count.
            plots = 1;
            Print(indi, "[SHIM][PLOTS] " + indi.GetType().Name
                      + " declares no plot (no AddPlot). NT8 would have "
                      + "Values.Length = 0 here; the shim creates ONE value series.");
         }
         // One line per indicator: which SERIES it is bound to and how many
         // value series it keeps. Both silently decide every result —
         // an indicator on the wrong series computes the wrong thing flawlessly.
         var _bp = indi.Bars != null ? indi.Bars.BarsPeriod : null;
         Print(indi, "[SHIM][INDICATOR] " + indi.GetType().Name + ": series " + indi._hostIdx
                   + " (" + (_bp != null ? _bp.BarsPeriodType + ":" + _bp.Value : "?") + ")"
                   + ", Plots " + plots);
         var werte = new NinjaTrader.NinjaScript.Indicators.Series<double>[plots];
         for (int i = 0; i < plots; i++)
            werte[i] = i < indi.Values.Length && indi.Values[i] != null
                     ? indi.Values[i]   // keep the series set in the constructor
                     : new NinjaTrader.NinjaScript.Indicators.Series<double>();
         indi.Values = werte;

         _indicators.Add(indi);
      }

      private readonly List<NinjaTrader.NinjaScript.Indicators.Indicator> _indicators = new();

      private static bool HasNinjaScriptPropertyAttribute(System.Reflection.PropertyInfo p)
      {
         foreach (var attr in p.GetCustomAttributes(true))
            if (attr.GetType().Name == "NinjaScriptPropertyAttribute") return true;
         return false;
      }

      private void DispatchSecondaryFormingTick(NinjaScriptBase ns, NinjaTrader.Data.Bars bars,
                                                BarStream stream, int seriesIdx, int b, bool firstTick)
      {
         ns.CurrentBars[seriesIdx] = b;
         ns.CurrentBar             = b;
         ns.BarsInProgress         = seriesIdx;
         ns.IsFirstTickOfBar       = firstTick;
         bars.IsFirstBarOfSession  = b == stream.SessionStartIndex;
         DriveHostedIndicators();
         DriveOnEachTickIndicators();
         try { ns.DispatchBarUpdate(); }
         catch (Exception ex) { LogShimError(ns, "OnBarUpdate(secondary, forming)", ex); }
         ns.IsFirstTickOfBar      = false;
         bars.IsFirstBarOfSession = false;
      }

      public void DriveHostedIndicators()
      {
         // index loop (not foreach): an indicator's OnBarUpdate may create+
         // register a new inline indicator (e.g. RSI(macdLine,…)) mid-drive.
         for (int i = 0; i < _indicators.Count; i++) _indicators[i].EnsureUpdated();
      }

      private void DriveOnEachTickIndicators()
      {
         bool all = FormingViewForAllHosted;
         for (int i = 0; i < _indicators.Count; i++)
            if (all || _indicators[i].Calculate == Calculate.OnEachTick)
               _indicators[i].EnsureUpdated();
      }

      private static readonly NinjaTrader.NinjaScript.State[] _indiLifecycle =
      {
         NinjaTrader.NinjaScript.State.SetDefaults,
         NinjaTrader.NinjaScript.State.Configure,
         NinjaTrader.NinjaScript.State.DataLoaded,
         NinjaTrader.NinjaScript.State.Historical,
         NinjaTrader.NinjaScript.State.Transition,
         NinjaTrader.NinjaScript.State.Realtime,
      };

      private void DriveIndicatorLifecycle(
         NinjaTrader.NinjaScript.Indicators.Indicator indi,
         NinjaTrader.NinjaScript.State target,
         System.Action afterSetDefaults = null)
      {
         foreach (var st in _indiLifecycle)
         {
            indi.DispatchStateChange(st);
            if (st == NinjaTrader.NinjaScript.State.SetDefaults) afterSetDefaults?.Invoke();
            if (st == target) break;
         }
      }

      private int ResolveHostIndex(
         NinjaTrader.NinjaScript.ISeries<double> input,
         NinjaTrader.NinjaScript.NinjaScriptBase host,
         NinjaTrader.NinjaScript.NinjaScriptBase caller)
      {
         var arr = host?.BarsArray;
         if (input != null && arr != null)
            for (int k = 0; k < arr.Length; k++)
               if (arr[k] != null && object.ReferenceEquals(input, arr[k].Close))
                  return k;
         // Computed-series input (e.g. RSI on macdLine): inherit the creating
         // indicator's host series so it advances on the same timeline.
         if (caller is NinjaTrader.NinjaScript.Indicators.Indicator ci) return ci._hostIdx;
         return 0;
      }

      private const double FAR_PRICE = 1e9;

      public int EnterLongCallCount, EnterShortCallCount;

      /// <summary>When true, log a one-line position snapshot every time
      /// EnterLong/EnterShort is called (diagnostics of a strategy's re-entry
      /// gating).</summary>
      public bool DiagPositionsOnEnter { get; set; }

      private void LogPositionsSnapshot(NinjaScriptBase from, string ctx)
      {
         if (!DiagPositionsOnEnter || WriteLine == null) return;
         try
         {
            var posField = from.GetType().GetField("Positions",
               System.Reflection.BindingFlags.Public |
               System.Reflection.BindingFlags.NonPublic |
               System.Reflection.BindingFlags.Instance |
               System.Reflection.BindingFlags.FlattenHierarchy);
            var positions = posField?.GetValue(from) as System.Collections.IEnumerable;
            if (positions == null) return;
            int n = 0, valid = 0;
            string firstLabel = null;
            DateTime firstEntry = default;
            string firstSym = null;
            foreach (var p in positions)
            {
               n++;
               var t = p.GetType();
               var entryT = t.GetProperty("EntryTime")?.GetValue(p);
               var lbl    = t.GetProperty("Label")?.GetValue(p) as string;
               var sym    = t.GetProperty("SymbolName")?.GetValue(p) as string;
               if (entryT is DateTime et && et > DateTime.MinValue) valid++;
               if (n == 1) { firstLabel = lbl; firstEntry = entryT is DateTime ee ? ee : default; firstSym = sym; }
            }
            WriteLine($"[ShimRuntime] {ctx} Positions={n} (valid={valid}) " +
                       $"first[Label='{firstLabel}' EntryTime={firstEntry:HH:mm:ss} SymbolName='{firstSym}']");
         }
         catch (Exception ex)
         {
            WriteLine($"[ShimRuntime] LogPositionsSnapshot failed: {ex.Message}");
         }
      }

      private bool EntryBlocked(NinjaScriptBase from, bool isLong, string sig)
      {
         if (SeriesWarmup) { WarmupEntriesRefused++; return true; }
         int perDirection = 1;
         var handling = NinjaTrader.Cbi.EntryHandling.AllEntries;
         if (from is NinjaTrader.NinjaScript.Strategies.Strategy st)
         {
            perDirection = Math.Max(1, st.EntriesPerDirection);
            handling = st.EntryHandling;
         }
         int inDirection = 0, withName = 0;
         bool named = !string.IsNullOrEmpty(sig);
         foreach (var p in _openPositions.Values)
            if (p.IsLong == isLong) { inDirection++; if (named && p.SignalName == sig) withName++; }
         foreach (var pe in _pendingEntries)
            if (pe.IsLong == isLong) { inDirection++; if (named && pe.SignalName == sig) withName++; }
         foreach (var pl in _pendingLimits)
            if (pl.IsLong == isLong) { inDirection++; if (named && pl.SignalName == sig) withName++; }
         if (withName > 0) return true;                       // label collision (see above)
         return handling == NinjaTrader.Cbi.EntryHandling.UniqueEntries
            ? withName >= perDirection
            : inDirection >= perDirection;
      }

      public Order EnterLong(NinjaScriptBase from, int bip, int qty, string sig)
      {
         EnterLongCallCount++;
         if (EntryBlocked(from, isLong: true, sig)) return null;   // NT8 EntriesPerDirection / EntryHandling of the strategy
         LogPositionsSnapshot(from, $"EnterLong sig='{sig}'");
         var label = string.IsNullOrEmpty(sig) ? $"long_{TradeLedger.Count + 1}" : sig;
         EnqueuePendingEntry(label, isLong: true, qty);
         var ntOrder = new Order
         {
            OrderAction = OrderAction.Buy,
            Quantity = qty,
            SignalName = label,
            Name = label + "|",
            Instrument = DefaultInstrument,
            OrderState = OrderState.Working,
         };
         OrderRouter?.MarketLong(qty, label);
         return ntOrder;
      }

      public Order EnterShort(NinjaScriptBase from, int bip, int qty, string sig)
      {
         EnterShortCallCount++;
         if (EntryBlocked(from, isLong: false, sig)) return null;  // NT8 EntriesPerDirection / EntryHandling of the strategy
         LogPositionsSnapshot(from, $"EnterShort sig='{sig}'");
         var label = string.IsNullOrEmpty(sig) ? $"short_{TradeLedger.Count + 1}" : sig;
         EnqueuePendingEntry(label, isLong: false, qty);
         var ntOrder = new Order
         {
            OrderAction = OrderAction.SellShort,
            Quantity = qty,
            SignalName = label,
            Name = label + "|",
            Instrument = DefaultInstrument,
            OrderState = OrderState.Working,
         };
         // Entry MarketOrder only. SL/TP via CheckOpenPositionsExit
         // tick-check — see EnterLong comment.
         OrderRouter?.MarketShort(qty, label);
         return ntOrder;
      }

      private void EnqueuePendingEntry(string label, bool isLong, int qty)
      {
         var entry = new PendingEntry
         {
            SignalName  = label,
            IsLong      = isLong,
            Quantity    = qty,
            SubmitTs    = Now,
            ActiveTs    = ActivationFor(Now),   // [NT8-SECOND-GRID]
            SubmitBid   = LastBid,
            SubmitAsk   = LastAsk,
            SubmitPrice = LastPrice,
            SubmitGridBid = GridBid,   // see Nt8SecondGridEntryAtGridBook
            SubmitGridAsk = GridAsk,
         };
         if (_pendingProtection.TryGetValue(label, out var pp))
         {
            entry.Protection    = pp;
            entry.HasProtection = true;
            _pendingProtection.Remove(label);
         }
         _pendingEntries.Enqueue(entry);
         _lastEnqueuedEntry = entry;
      }

      private void FireOrderUpdate(NinjaScriptBase owner, Order o, OrderState st,
                                   double avgFill, int filled)
         => FireOrderUpdate(owner, o, st, avgFill, filled, ErrorCode.NoError, "");

      /// <summary>OnOrderUpdate on the strategy; `error`/`nativeError` carry NT8's rejection of a change
      /// ([STOP-CHANGE-REJECT]: ErrorCode.UnableToChangeOrder with the original order in its old state).</summary>
      private void FireOrderUpdate(NinjaScriptBase owner, Order o, OrderState st,
                                   double avgFill, int filled, ErrorCode error, string nativeError)
      {
         if (o == null) return;
         o.OrderState = st;
         if (st == OrderState.Filled) { o.AverageFillPrice = avgFill; o.Filled = filled; }
         var strat = owner as NinjaTrader.NinjaScript.Strategies.Strategy;
         if (strat == null)
            foreach (var ns in _hosted)
               if (ns is NinjaTrader.NinjaScript.Strategies.Strategy s) { strat = s; break; }
         if (strat == null) return;
         try
         {
            strat.RaiseOrderUpdate(o, o.LimitPrice, o.StopPrice, o.Quantity, filled,
               avgFill, st, Now, error, nativeError ?? "");
         }
         catch (Exception ex) { LogShimError((NinjaScriptBase)strat, "OnOrderUpdate", ex); }
      }

      private void FinalisePendingEntries(ShimTick t)
      {
         if (_pendingEntries.Count == 0) return;
         var snapshot = new List<PendingEntry>();
         while (_pendingEntries.Count > 0) snapshot.Add(_pendingEntries.Dequeue());

         foreach (var p in snapshot)
         {
            // [NT8-SECOND-GRID] a market entry is working from the next whole second - not before
            if (OrderActivationInUse && t.Ts < p.ActiveTs) { _pendingEntries.Enqueue(p); continue; }
            DateTime fillTs    = FillAtNextTick ? t.Ts : p.SubmitTs;
            // [FILL-AT-BBO] Entry on the buy side for a long, on the sell side
            // for a short. The submit tick's BBO is carried along in the pending
            // record (SubmitBid/SubmitAsk), so no falling back to the
            // CURRENT tick — the fill belongs to the submit moment.
            double fillPrice = FillAtNextTick ? t.Price : p.SubmitPrice;
            if (FillAtBbo)
            {
               var bbo = FillAtNextTick ? (p.IsLong ? t.Ask : t.Bid)
                                        : (p.IsLong ? p.SubmitAsk : p.SubmitBid);
               if (bbo > 0) fillPrice = bbo;
            }
            // see Nt8SecondGridEntryAtGridBook: the book of the submit second, stamped at the submit tick
            if (Nt8SecondGrid && Nt8SecondGridEntryAtGridBook)
            {
               double gb = p.IsLong ? p.SubmitGridAsk : p.SubmitGridBid;
               if (gb <= 0)
                  throw new InvalidOperationException("[GRID-ENTRY-BOOK] entry '" + p.SignalName + "' submitted at "
                     + p.SubmitTs.ToString("yyyy-MM-dd HH:mm:ss.fff") + " without a grid book (GridBid/GridAsk "
                     + p.SubmitGridBid + "/" + p.SubmitGridAsk + ") - no fill price. No fallback.");
               fillPrice = gb;
               fillTs = p.SubmitTs;
            }
            var pos = new ShimPosition
            {
               SignalName   = p.SignalName,
               EntryTs      = fillTs,
               EntryPrice   = fillPrice,
               EntryBarIdx  = _bars?.Count ?? 0,   // [BARINDEX] primary series
               StopActiveTs   = LegActivationFor(t.Ts),   // [NT8-SECOND-GRID]
               TargetActiveTs = LegActivationFor(t.Ts),

               Quantity     = p.Quantity,
               IsLong       = p.IsLong,
               StopLossPx   = p.IsLong ? -FAR_PRICE :  FAR_PRICE,
               TakeProfitPx = p.IsLong ?  FAR_PRICE : -FAR_PRICE,
            };
            if (p.HasProtection)
            {
               if (p.Protection.SlSet)
                  pos.StopLossPx   = ResolveStopPrice(pos, p.Protection.SlMode, p.Protection.SlValue, isLossSide: true);
               if (p.Protection.TpSet)
                  pos.TakeProfitPx = ResolveStopPrice(pos, p.Protection.TpMode, p.Protection.TpValue, isLossSide: false);
            }
            _openPositions[p.SignalName] = pos;
            pos.EntryOrder       = p.NtOrder;
            pos.Owner            = p.Owner;
            pos.RaiseOrderEvents = p.RaiseOrderEvents;
            if (UnmanagedNt8Events && p.RaiseOrderEvents)
            {
               // [NT8-UNMANAGED] as in CheckPendingLimits: fields first,
               // OnOrderUpdate(Filled), ONE ledger execution at the ledger fill
               // price (not the engine mirror's), then OnPositionUpdate.
               bool mpChanged = SyncHostedPositionFields(out int net, out double lastEntry, out var newMp);
               FireOrderUpdate(p.Owner, p.NtOrder, OrderState.Filled, fillPrice, p.Quantity);
               FireLimitExecution(p.Owner, p.NtOrder, fillPrice, p.Quantity, p.IsLong);
               if (mpChanged) RaiseHostedPositionUpdates(net, lastEntry, newMp);
               continue;
            }
            SyncHostedPositions();
            if (p.RaiseOrderEvents)
               FireOrderUpdate(p.Owner, p.NtOrder, OrderState.Filled, fillPrice, p.Quantity);
            // [EXECUTIONS-FROM-LEDGER] the entry execution at the ledger fill
            // (price and tick), where the mirror used to raise it at submit.
            if (ExecutionsFromLedger)
               RaiseLedgerFill(p.Owner, isEntry: true, signal: p.SignalName, fromSignal: null,
                               exitSignal: null, isLong: p.IsLong, qty: p.Quantity,
                               price: fillPrice, tsUtc: fillTs);
         }
      }

      public Order EnterLongLimit(NinjaScriptBase from, int bip, int qty, double limitPrice, string sig)
         => EnterLimitImpl(from, qty, limitPrice, isLong: true, sig);
      public Order EnterShortLimit(NinjaScriptBase from, int bip, int qty, double limitPrice, string sig)
         => EnterLimitImpl(from, qty, limitPrice, isLong: false, sig);

      private void LimitJournal(string what, DateTime ts, bool isLong, double limit, string sig, string extra = "", NinjaScriptBase from = null)
      {
         if (!Nt8SecondGrid && !LimitJournalAlways) return;
         string view = from == null ? "" : $" cb={from.CurrentBar} ftb={from.IsFirstTickOfBar} bip={from.BarsInProgress}";
         // microseconds: tick stamps can carry the event order inside the millisecond, so a journal
         // line names the exact tick
         WriteLine?.Invoke($"[LIMIT-JOURNAL] {what,-7} {ts:yyyy-MM-dd HH:mm:ss.ffffff} {(isLong ? "L" : "S")} lim={limit:F2} sig='{sig}' {extra}{view}");
      }

      private Order EnterLimitImpl(NinjaScriptBase from, int qty, double limitPrice, bool isLong, string sig,
                                   string oco = null, bool raiseOrderEvents = false)
      {
         if (EntryBlocked(from, isLong, sig))      // NT8 EntriesPerDirection / EntryHandling of the strategy
         {
            LimitJournal("blocked", Now, isLong, limitPrice, sig,
               "open=" + _openPositions.Count + " pendingEntries=" + _pendingEntries.Count + " pendingLimits=" + _pendingLimits.Count);
            return null;
         }
         var label = string.IsNullOrEmpty(sig)
            ? $"{(isLong ? "long" : "short")}_{TradeLedger.Count + 1}" : sig;
         var ntOrder = new Order
         {
            OrderAction = isLong ? OrderAction.Buy : OrderAction.SellShort,
            OrderType   = OrderType.Limit,
            Quantity    = qty,
            SignalName  = label,
            Name        = label,
            LimitPrice  = limitPrice,
            Oco         = oco,
            Instrument  = DefaultInstrument,
            OrderState  = OrderState.Working,
            Time        = Now,
         };
         var pl = new PendingLimit
         {
            SignalName = label, IsLong = isLong, Quantity = qty,
            LimitPrice = limitPrice, SubmitTs = Now, ActiveTs = ActivationFor(Now), NtOrder = ntOrder, Owner = from,
            RaiseOrderEvents = raiseOrderEvents,
            CheckMarketableNext = Nt8SecondGrid,   // [NEXT-EVENT-FILL] the next tick decides
         };
         if (_pendingProtection.TryGetValue(label, out var pp))
         {
            pl.Protection = pp; pl.HasProtection = true; _pendingProtection.Remove(label);
         }
         _pendingLimits.Add(pl);
         LimitJournal("submit", Now, isLong, limitPrice, label, $"active={pl.ActiveTs:HH:mm:ss.fff}", from);
         if (raiseOrderEvents) _unmanagedLifecycleActive = true;
         FireOrderUpdate(from, ntOrder, OrderState.Working, 0, 0);
         return ntOrder;
      }

      private void CheckPendingLimits(ShimTick t)
      {
         if (_pendingLimits.Count == 0) return;
         for (int i = _pendingLimits.Count - 1; i >= 0; i--)
         {
            var pl = _pendingLimits[i];
            // never on the submit tick; [NT8-SECOND-GRID] not before the next whole second
            if (OrderActivationInUse ? t.Ts < pl.ActiveTs : t.Ts <= pl.SubmitTs) continue;
            bool hit;
            double fillPx = pl.LimitPrice;
            if (QuoteDrivenExecution)
            {
               hit = pl.IsLong ? (t.Ask > 0 && t.Ask <= pl.LimitPrice)
                               : (t.Bid > 0 && t.Bid >= pl.LimitPrice);
            }
            else
            {
               hit = t.IsLast
                     && (pl.IsLong ? (t.Price > 0 && t.Price <= pl.LimitPrice)
                                   : (t.Price > 0 && t.Price >= pl.LimitPrice));
               if (!hit && (Nt8SecondGrid || LimitBookTouch))   // [LIMIT-BOOK-TOUCH] also without the grid
               {
                  if (Nt8SecondGridTouchThrough)
                     hit = pl.IsLong ? (t.Ask > 0 && t.Ask < pl.LimitPrice - TickSizeEpsilon)
                                     : (t.Bid > 0 && t.Bid > pl.LimitPrice + TickSizeEpsilon);
                  else
                     hit = pl.IsLong ? (t.Ask > 0 && t.Ask <= pl.LimitPrice + TickSizeEpsilon)
                                     : (t.Bid > 0 && t.Bid >= pl.LimitPrice - TickSizeEpsilon);
                  if (hit) TouchFills++;
                  if (hit) LimitJournal("fill", t.Ts, pl.IsLong, pl.LimitPrice, pl.SignalName, $"px={fillPx:F2} rule=touch bid={t.Bid} ask={t.Ask}");
               }
               else if (hit) LimitJournal("fill", t.Ts, pl.IsLong, pl.LimitPrice, pl.SignalName, $"px={fillPx:F2} rule=print last={t.Price}");
            }
            if (!hit) continue;
            _pendingLimits.RemoveAt(i);
            FillPendingLimit(pl, t, fillPx);
         }
      }

      private void CheckMarketableLimits(ShimTick t)
      {
         for (int i = _pendingLimits.Count - 1; i >= 0; i--)
         {
            var pl = _pendingLimits[i];
            if (!pl.CheckMarketableNext || t.Ts <= pl.SubmitTs) continue;
            if (t.Ts >= pl.ActiveTs)
            {
               pl.CheckMarketableNext = false;
               LimitJournal("rests", t.Ts, pl.IsLong, pl.LimitPrice, pl.SignalName, "no side event through the limit before the activation second");
               continue;
            }
            double p = SideEventPx(t, orderIsBuy: pl.IsLong);
            if (p <= 0) continue;   // a quote event of the other side only
            bool through = pl.IsLong ? p <= pl.LimitPrice + TickSizeEpsilon : p >= pl.LimitPrice - TickSizeEpsilon;
            if (!through) continue;
            MarketableFills++;
            LimitJournal("fill", t.Ts, pl.IsLong, pl.LimitPrice, pl.SignalName, $"px={p:F2} rule=marketable on {(t.Price > 0 ? "last" : "book")}={p}");
            _pendingLimits.RemoveAt(i);
            FillPendingLimit(pl, t, p);
         }
      }

      // [SIDE-EVENTS] the book before the current tick (set at the top of PumpTickCore)
      private double _prevBid, _prevAsk;

      private double SideEventPx(ShimTick t, bool orderIsBuy)
      {
         if (t.Price > 0) return t.Price;
         bool bidChanged = Math.Abs(t.Bid - _prevBid) > TickSizeEpsilon;
         bool askChanged = Math.Abs(t.Ask - _prevAsk) > TickSizeEpsilon;
         if (!orderIsBuy && askChanged && !bidChanged) return 0;
         if (orderIsBuy && bidChanged && !askChanged) return 0;
         return orderIsBuy ? t.Ask : t.Bid;
      }

      /// <summary>Turn a filled PendingLimit into an open ShimPosition and raise the fill's executions
      /// and events. Shared by CheckPendingLimits (fill at the limit) and CheckMarketableLimits (fill
      /// at the next event's price). The caller has already removed `pl` from _pendingLimits.</summary>
      private void FillPendingLimit(PendingLimit pl, ShimTick t, double fillPx)
      {
         var pos = new ShimPosition
         {
            SignalName   = pl.SignalName,
            EntryTs      = t.Ts,
            EntryPrice   = fillPx,
            StopActiveTs   = LegActivationFor(t.Ts),   // [NT8-SECOND-GRID]
            TargetActiveTs = LegActivationFor(t.Ts),
            Quantity     = pl.Quantity,
            IsLong       = pl.IsLong,
            StopLossPx   = pl.IsLong ? -FAR_PRICE :  FAR_PRICE,
            TakeProfitPx = pl.IsLong ?  FAR_PRICE : -FAR_PRICE,
         };
         if (pl.HasProtection)
         {
            if (pl.Protection.SlSet)
               pos.StopLossPx   = ResolveStopPrice(pos, pl.Protection.SlMode, pl.Protection.SlValue, isLossSide: true);
            if (pl.Protection.TpSet)
               pos.TakeProfitPx = ResolveStopPrice(pos, pl.Protection.TpMode, pl.Protection.TpValue, isLossSide: false);
         }
         pos.EntryOrder = pl.NtOrder; pos.Owner = pl.Owner; pos.RaiseOrderEvents = pl.RaiseOrderEvents;
         _openPositions[pl.SignalName] = pos;
         _limitOrigin.Add(pl.SignalName);

         pl.NtOrder.OrderState       = OrderState.Filled;
         pl.NtOrder.AverageFillPrice = fillPx;
         pl.NtOrder.Filled           = pl.Quantity;
         if (UnmanagedNt8Events && pl.RaiseOrderEvents)
         {
            // [NT8-UNMANAGED] NT8's order of events for an unmanaged limit
            // fill: Position already reflects the fill, OnOrderUpdate(Filled)
            // on the strategy's own Order, ONE execution, then OnPositionUpdate.
            bool mpChanged = SyncHostedPositionFields(out int net, out double lastEntry, out var newMp);
            FireOrderUpdate(pl.Owner, pl.NtOrder, OrderState.Filled, fillPx, pl.Quantity);
            FireLimitExecution(pl.Owner, pl.NtOrder, fillPx, pl.Quantity, pl.IsLong);
            OrderRouter?.LimitEntryFilled(pl.Quantity, pl.IsLong, pl.SignalName);
            if (mpChanged) RaiseHostedPositionUpdates(net, lastEntry, newMp);
            return;
         }
         FireLimitExecution(pl.Owner, pl.NtOrder, fillPx, pl.Quantity, pl.IsLong);

         OrderRouter?.LimitEntryFilled(pl.Quantity, pl.IsLong, pl.SignalName);
         SyncHostedPositions();
      }

      private void FireLimitExecution(NinjaScriptBase owner, Order o, double price,
                                      int qty, bool isLong)
      {
         var strat = owner as NinjaTrader.NinjaScript.Strategies.Strategy;
         if (strat == null)
            foreach (var ns in _hosted)
               if (ns is NinjaTrader.NinjaScript.Strategies.Strategy s) { strat = s; break; }
         if (strat == null) return;
         var mp = isLong ? NinjaTrader.Cbi.MarketPosition.Long
                         : NinjaTrader.Cbi.MarketPosition.Short;
         var exec = new NinjaTrader.Cbi.Execution
         {
            ExecutionId = o.Name + "_lim",
            Order = o, Price = price, Quantity = qty, Time = Now,
            MarketPosition = mp, Instrument = DefaultInstrument,
            Name = o.Name, OrderAction = o.OrderAction,
            IsEntry = true, IsExit = false,
         };
         try
         {
            strat.RaiseExecutionUpdate(exec, exec.ExecutionId, price, qty, mp, o.Name, Now);
         }
         catch (Exception ex) { LogShimError((NinjaScriptBase)strat, "OnExecutionUpdate(limit)", ex); }
      }

      private void FireLimitExit(ShimPosition pos, string reason)
      {
         var strat = pos.Owner as NinjaTrader.NinjaScript.Strategies.Strategy;
         if (strat == null)
            foreach (var ns in _hosted)
               if (ns is NinjaTrader.NinjaScript.Strategies.Strategy s) { strat = s; break; }
         if (strat == null) return;
         var exitOrder = new NinjaTrader.Cbi.Order
         {
            OrderState  = OrderState.Filled,
            OrderAction = pos.IsLong ? OrderAction.Sell : OrderAction.BuyToCover,
            OrderType   = OrderType.Market,
            Quantity    = pos.Quantity,
            // [FILL-AT-BBO] same side as the ledger entry: a long
            // sells (bid), a short buys back (ask). If the order notification
            // and the booking contradicted each other, the strategy would see a
            // different price than the result CSV.
            AverageFillPrice = FillPx(isBuy: !pos.IsLong),
            Name        = reason ?? "exit",
            FromEntrySignal = pos.SignalName,
            Instrument  = DefaultInstrument,
         };
         var mp = pos.IsLong ? NinjaTrader.Cbi.MarketPosition.Long
                             : NinjaTrader.Cbi.MarketPosition.Short;
         var exec = new NinjaTrader.Cbi.Execution
         {
            ExecutionId = pos.SignalName + "_limx",
            Order = exitOrder, Price = FillPx(isBuy: !pos.IsLong), Quantity = pos.Quantity, Time = Now,
            MarketPosition = mp, Instrument = DefaultInstrument,
            Name = reason ?? "exit", OrderAction = exitOrder.OrderAction,
            IsEntry = false, IsExit = true,
         };
         try
         {
            strat.RaiseExecutionUpdate(exec, exec.ExecutionId, LastPrice, pos.Quantity, mp, exitOrder.Name, Now);
         }
         catch (Exception ex) { LogShimError((NinjaScriptBase)strat, "OnExecutionUpdate(limit-exit)", ex); }
      }

      private void FireExitExecution(ShimPosition pos, Order exitOrder, string exitName,
                                     double price, NinjaTrader.Cbi.MarketPosition mpAfter)
      {
         var strat = pos.Owner as NinjaTrader.NinjaScript.Strategies.Strategy;
         if (strat == null)
            foreach (var ns in _hosted)
               if (ns is NinjaTrader.NinjaScript.Strategies.Strategy s) { strat = s; break; }
         if (strat == null) return;
         if (exitOrder == null)
            exitOrder = new NinjaTrader.Cbi.Order
            {
               OrderAction = pos.IsLong ? OrderAction.Sell : OrderAction.BuyToCover,
               OrderType   = OrderType.Market,
               Quantity    = pos.Quantity,
               Name        = exitName,
               FromEntrySignal = pos.SignalName,
               Instrument  = DefaultInstrument,
               Time        = Now,
            };
         exitOrder.OrderState       = OrderState.Filled;
         exitOrder.AverageFillPrice = price;
         exitOrder.Filled           = pos.Quantity;
         var exec = new NinjaTrader.Cbi.Execution
         {
            ExecutionId = pos.SignalName + "_x" + TradeLedger.Count.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Order = exitOrder, Price = price, Quantity = pos.Quantity, Time = Now,
            MarketPosition = mpAfter, Instrument = DefaultInstrument,
            Name = exitOrder.Name, OrderAction = exitOrder.OrderAction,
            IsEntry = false, IsExit = true,
         };
         try
         {
            strat.RaiseExecutionUpdate(exec, exec.ExecutionId, price, pos.Quantity, mpAfter, exitOrder.Name, Now);
         }
         catch (Exception ex) { LogShimError((NinjaScriptBase)strat, "OnExecutionUpdate(exit)", ex); }
      }

      private void CloseUnmanagedAtMarket(NinjaScriptBase from, bool exitsLong, Order order)
      {
         var label = FindOpenSignal(isLong: exitsLong);
         if (label.Length == 0 || !_openPositions.TryGetValue(label, out var pos))
         {
            WriteLine?.Invoke($"[SHIM][NT8-UNMANAGED] market exit '{order.Name}' without an open "
                            + $"{(exitsLong ? "long" : "short")} position - rejected, as NT8 would.");
            FireOrderUpdate(from, order, OrderState.Rejected, 0, 0);
            return;
         }
         var exitName = string.IsNullOrEmpty(order.Name) ? (exitsLong ? "ExitLong" : "ExitShort") : order.Name;
         // [MARKET-EXIT-NEXT-EVENT] on the grid the market order fills at the first side event after this
         // tick (FillTriggeredStops), as NT8's simulator does - the order stays Working until then.
         if (Nt8SecondGrid)
         {
            pos.PendingExitOrder = order;
            pos.StopTriggerTs = Now;
            order.OrderState = OrderState.Working;
            WriteLine?.Invoke($"[MARKET-EXIT] {(pos.IsLong ? "L" : "S")} sig='{pos.SignalName}' '{exitName}' submitted {Now:yyyy-MM-dd HH:mm:ss.ffffff} - fills at the next side event");
            return;
         }
         double px = FillPx(isBuy: !exitsLong);
         TradeLedger.Add(new ShimTrade
         {
            Idx = TradeLedger.Count + 1, SignalName = pos.SignalName,
            EntryTs = pos.EntryTs, EntryPrice = pos.EntryPrice,
            ExitTs = Now, ExitPrice = px, Quantity = pos.Quantity,
            IsLong = pos.IsLong, ExitReason = exitName,
            EntryBarIdx = pos.EntryBarIdx, ExitBarIdx = _bars?.Count ?? 0,
         });
         AppendStrategyTrade(TradeLedger[TradeLedger.Count - 1], exitName);
         _openPositions.Remove(label);
         _limitOrigin.Remove(label);
         if (exitsLong) OrderRouter?.ExitLong(pos.Quantity, exitName, pos.SignalName);
         else           OrderRouter?.ExitShort(pos.Quantity, exitName, pos.SignalName);
         bool mpChanged = SyncHostedPositionFields(out int net, out double lastEntry, out var newMp);
         FireOrderUpdate(from, order, OrderState.Filled, px, pos.Quantity);
         FireExitExecution(pos, order, exitName, px, newMp);
         if (mpChanged) RaiseHostedPositionUpdates(net, lastEntry, newMp);
      }

      public Order ExitLong(NinjaScriptBase from, int bip, int qty, string sig, string fes)
      {
         bool had = _openPositions.TryGetValue(fes ?? "", out var pos);
         double px = 0;
         if (had)
         {
            px = FillPx(isBuy: false);
            TradeLedger.Add(new ShimTrade
            {
               Idx = TradeLedger.Count + 1, SignalName = pos.SignalName,
               EntryTs = pos.EntryTs, EntryPrice = pos.EntryPrice,
               ExitTs = Now, ExitPrice = px, Quantity = pos.Quantity,
               IsLong = true, ExitReason = "ExitLong",
               EntryBarIdx = pos.EntryBarIdx, ExitBarIdx = _bars?.Count ?? 0,
            });
            AppendStrategyTrade(TradeLedger[TradeLedger.Count - 1], sig ?? "ExitLong");
            _openPositions.Remove(fes);
            SyncHostedPositions();
         }
         OrderRouter?.ExitLong(qty, sig ?? "exit", fes ?? "");
         // [EXECUTIONS-FROM-LEDGER] the mirror raised this exit only when it
         // found an engine position to close; the ledger's equivalent is "had".
         if (had && ExecutionsFromLedger)
            RaiseLedgerFill(from, isEntry: false, signal: null, fromSignal: fes ?? "",
                            exitSignal: sig ?? "exit", isLong: true, qty: qty, price: px, tsUtc: Now);
         return new Order { OrderAction = OrderAction.Sell, Quantity = qty, SignalName = sig, FromEntrySignal = fes };
      }

      public Order ExitShort(NinjaScriptBase from, int bip, int qty, string sig, string fes)
      {
         bool had = _openPositions.TryGetValue(fes ?? "", out var pos);
         double px = 0;
         if (had)
         {
            px = FillPx(isBuy: true);
            TradeLedger.Add(new ShimTrade
            {
               Idx = TradeLedger.Count + 1, SignalName = pos.SignalName,
               EntryTs = pos.EntryTs, EntryPrice = pos.EntryPrice,
               ExitTs = Now, ExitPrice = px, Quantity = pos.Quantity,
               IsLong = false, ExitReason = "ExitShort",
               EntryBarIdx = pos.EntryBarIdx, ExitBarIdx = _bars?.Count ?? 0,
            });
            AppendStrategyTrade(TradeLedger[TradeLedger.Count - 1], sig ?? "ExitShort");
            _openPositions.Remove(fes);
            SyncHostedPositions();
         }
         OrderRouter?.ExitShort(qty, sig ?? "exit", fes ?? "");
         // [EXECUTIONS-FROM-LEDGER] see ExitLong.
         if (had && ExecutionsFromLedger)
            RaiseLedgerFill(from, isEntry: false, signal: null, fromSignal: fes ?? "",
                            exitSignal: sig ?? "exit", isLong: false, qty: qty, price: px, tsUtc: Now);
         return new Order { OrderAction = OrderAction.BuyToCover, Quantity = qty, SignalName = sig, FromEntrySignal = fes };
      }

      // SetStopLoss/SetProfitTarget update the SL/TP price on the open
      // position matching `fromSig`. One source of truth: these set
      // ONLY the in-memory ShimPosition.StopLossPx / TakeProfitPx — they
      // do NOT submit a separate Lean StopMarketOrder/LimitOrder.
      // CheckOpenPositionsExit enforces SL/TP on every tick and emits an
      // ExitLong/ExitShort MarketOrder when hit.
      public void SetStopLoss(NinjaScriptBase from, string fromSig,
         NinjaTrader.NinjaScript.Strategies.CalculationMode mode, double value, bool isSimulated)
      {
         if (string.IsNullOrEmpty(fromSig)) return;
         if (_openPositions.TryGetValue(fromSig, out var pos))
         {
            pos.StopLossPx = ResolveStopPrice(pos, mode, value, isLossSide: true);
         }
         else
         {
            _pendingProtection.TryGetValue(fromSig, out var p);
            p.SlValue = value; p.SlMode = mode; p.SlSet = true;
            _pendingProtection[fromSig] = p;
         }
      }

      public void SetProfitTarget(NinjaScriptBase from, string fromSig,
         NinjaTrader.NinjaScript.Strategies.CalculationMode mode, double value)
      {
         if (string.IsNullOrEmpty(fromSig)) return;
         if (_openPositions.TryGetValue(fromSig, out var pos))
         {
            pos.TakeProfitPx = ResolveStopPrice(pos, mode, value, isLossSide: false);
         }
         else
         {
            _pendingProtection.TryGetValue(fromSig, out var p);
            p.TpValue = value; p.TpMode = mode; p.TpSet = true;
            _pendingProtection[fromSig] = p;
         }
      }

      public void SetTrailStop(NinjaScriptBase from, string fromSig,
         NinjaTrader.NinjaScript.Strategies.CalculationMode mode, double value, bool isSimulated)
         => SetStopLoss(from, fromSig, mode, value, isSimulated);   // simplest impl

      private double ResolveStopPrice(ShimPosition pos,
         NinjaTrader.NinjaScript.Strategies.CalculationMode mode, double value, bool isLossSide)
      {
         // [SHIM-CORE] tickSize from instrument (was hardcoded 0.25)
         if (DefaultInstrument?.MasterInstrument == null)
            throw new InvalidOperationException(
               "[SHIM-CORE] ResolveStopPrice: ShimRuntime.DefaultInstrument (or its " +
               "MasterInstrument) is not set — the host must inject an Instrument with " +
               "a valid TickSize before SL/TP levels can be resolved. No fallback.");
         double tickSize = DefaultInstrument.MasterInstrument.TickSize;
         if (tickSize <= 0)
            throw new InvalidOperationException(
               $"[SHIM-CORE] ResolveStopPrice: DefaultInstrument.MasterInstrument.TickSize " +
               $"= {tickSize} is invalid (must be > 0). Fix the host's instrument spec. No fallback.");
         // Sign convention:
         //   Long  + SL = entry - X (below);  + TP = entry + X (above)
         //   Short + SL = entry + X (above);  + TP = entry - X (below)
         bool below = (pos.IsLong && isLossSide) || (!pos.IsLong && !isLossSide);
         double sign = below ? -1.0 : +1.0;
         switch (mode)
         {
            case NinjaTrader.NinjaScript.Strategies.CalculationMode.Price:
               return value;
            case NinjaTrader.NinjaScript.Strategies.CalculationMode.Ticks:
               return pos.EntryPrice + sign * value * tickSize;
            case NinjaTrader.NinjaScript.Strategies.CalculationMode.Currency:
               // value is currency; per-tick currency = tickSize * pointValue
               return pos.EntryPrice + sign * value;
            case NinjaTrader.NinjaScript.Strategies.CalculationMode.Percent:
               return pos.EntryPrice * (1.0 + sign * value / 100.0);
            default:
               return pos.EntryPrice + sign * value * tickSize;
         }
      }

      public void CancelOrder(NinjaScriptBase from, Order order)
      {
         if (order == null) return;
         for (int i = _pendingLimits.Count - 1; i >= 0; i--)
            if (ReferenceEquals(_pendingLimits[i].NtOrder, order))
            {
               var owner = _pendingLimits[i].Owner;
               LimitJournal("cancel", Now, _pendingLimits[i].IsLong, _pendingLimits[i].LimitPrice, _pendingLimits[i].SignalName, "by the strategy", from);
               _pendingLimits.RemoveAt(i);
               order.OrderState = OrderState.Cancelled;
               FireOrderUpdate(owner, order, OrderState.Cancelled, 0, 0);
               return;
            }
         // Reference match first (unmanaged path — exact leg), then the
         // direction/type heuristic as fallback.
         bool matched = false;
         NinjaScriptBase legOwner = null;
         foreach (var pos in _openPositions.Values)
         {
            if (ReferenceEquals(pos.StopOrder, order))   { pos.StopLossPx = pos.IsLong ? -FAR_PRICE : FAR_PRICE; pos.StopOrder = null; pos.PendingStopPx = double.NaN; matched = true; legOwner = pos.Owner; }
            if (ReferenceEquals(pos.TargetOrder, order)) { pos.TakeProfitPx = pos.IsLong ? FAR_PRICE : -FAR_PRICE; pos.TargetOrder = null; pos.PendingTargetPx = double.NaN; matched = true; legOwner = pos.Owner; }
         }
         if (!matched)
         {
            bool exitsLong = order.OrderAction == OrderAction.Sell;
            bool exitsShort = order.OrderAction == OrderAction.BuyToCover;
            if (!exitsLong && !exitsShort) return;  // entry cancels: nothing pending shim-side
            foreach (var pos in _openPositions.Values)
            {
               if (pos.IsLong != exitsLong) continue;
               if (order.OrderType == OrderType.Stop || order.OrderType == OrderType.StopMarket
                   || order.OrderType == OrderType.StopLimit) pos.StopLossPx = pos.IsLong ? -FAR_PRICE : FAR_PRICE;
               else if (order.OrderType == OrderType.Limit || order.OrderType == OrderType.MIT)
                  pos.TakeProfitPx = pos.IsLong ? FAR_PRICE : -FAR_PRICE;
            }
         }
         order.OrderState = OrderState.Cancelled;
         if (matched && UnmanagedNt8Events)
            QueueDeferredOrderEvent(legOwner ?? from, order, OrderState.Cancelled, ErrorCode.NoError, "");
      }

      public sealed class StopTraceRow
      {
         public DateTime Utc;
         public int      Bip, CurSignal, TradeDir;
         public double   StopOld, StopNew;
         public bool     Accepted;
         public string   Reason;
         public double   Bid, Ask, High0, Low0, Close0;
      }

      /// <summary>Recording on when the host has set a path.
      /// Default off — a trace that always runs is compute time without a reader.</summary>
      public bool StopTraceEnabled { get; internal set; }
      public List<StopTraceRow> StopTrace { get; } = new List<StopTraceRow>();

      public sealed class DispatchRow
      {
         public DateTime TickUtc, BarUtc;
         public int      BarIdx;
         public double   TickPx;
      }
      public List<DispatchRow> DispatchTrace { get; } = new List<DispatchRow>();

      private void RecordStopTrace(NinjaScriptBase from, ShimPosition pos, double stopNew,
                                   string reason = "-")
      {
         if (!StopTraceEnabled) return;
         double h0 = 0, l0 = 0, c0 = 0;
         try
         {
            if (from != null && from.High != null && from.High.Count > 0)
            {
               h0 = from.High[0]; l0 = from.Low[0]; c0 = from.Close[0];
            }
         }
         catch { /* series not ready yet — columns stay 0, the row stays valid */ }

         int curSig = 0;
         if (from?.CurrentBars != null && from.CurrentBars.Length > 1) curSig = from.CurrentBars[1];

         StopTrace.Add(new StopTraceRow {
            Utc = Now, Bip = from?.BarsInProgress ?? -1, CurSignal = curSig,
            TradeDir = pos.IsLong ? 1 : -1,
            StopOld = pos.StopLossPx, StopNew = stopNew,
            Accepted = true, Reason = reason,
            Bid = LastBid, Ask = LastAsk, High0 = h0, Low0 = l0, Close0 = c0,
         });
      }

      public void ChangeOrder(NinjaScriptBase from, Order order, int qty, double limit, double stop)
      {
         if (order == null) return;
         double prevStop = order.StopPrice;
         order.Quantity = qty; order.LimitPrice = limit; order.StopPrice = stop;
         bool matched = false;
         foreach (var pos in _openPositions.Values)
         {
            // 🔴 Record BEFORE overwriting — afterwards `pos.StopLossPx` is
            // the new value, and the old level would silently equal the new one.
            if (ReferenceEquals(pos.StopOrder, order))
            {
               if (RejectStopChangeIfCrossed(from, pos, order, stop, prevStop)) return;
               ApplyStopChange(from, pos, stop); matched = true;
            }
            if (ReferenceEquals(pos.TargetOrder, order)) { ApplyTargetChange(pos, limit); matched = true; }
         }
         if (!matched)
         {
            bool exitsLong = order.OrderAction == OrderAction.Sell;
            bool exitsShort = order.OrderAction == OrderAction.BuyToCover;
            if (!exitsLong && !exitsShort) return;
            foreach (var pos in _openPositions.Values)
            {
               if (pos.IsLong != exitsLong) continue;
               if (order.OrderType == OrderType.Stop || order.OrderType == OrderType.StopMarket
                   || order.OrderType == OrderType.StopLimit)
               {
                  if (RejectStopChangeIfCrossed(from, pos, order, stop, prevStop)) return;
                  ApplyStopChange(from, pos, stop); matched = true;
               }
               else if (order.OrderType == OrderType.Limit || order.OrderType == OrderType.MIT)
               { ApplyTargetChange(pos, limit); matched = true; }
            }
         }
         // [DEFERRED-ORDER-EVENTS] NT8 confirms the change on a later event:
         // ChangePending -> Accepted -> Working with the new price. Queued for the
         // next PumpTick; the strategy's OnOrderUpdate(Accepted/Working) then
         // clears its pending-change flag and adopts the accepted stop level.
         if (matched && UnmanagedNt8Events)
            QueueDeferredOrderEvents(from, order);
      }

      private bool RejectStopChangeIfCrossed(NinjaScriptBase from, ShimPosition pos, Order order, double stop, double prevStop)
      {
         if (!Nt8SecondGrid || GridBid <= 0 || GridAsk <= 0) return false;
         bool crossed = pos.IsLong ? stop >= GridBid - TickSizeEpsilon : stop <= GridAsk + TickSizeEpsilon;
         if (!crossed) return false;
         order.StopPrice = prevStop;
         string native = pos.IsLong ? "Sell stop price can't be changed above the market"
                                    : "Buy stop price can't be changed below the market";
         WriteLine?.Invoke($"[STOP-CHANGE] {(pos.IsLong ? "L" : "S")} sig='{pos.SignalName}' {Now:yyyy-MM-dd HH:mm:ss.ffffff} "
                         + $"REJECTED {pos.StopLossPx:F2} -> {stop:F2}: {native} (grid {GridBid}/{GridAsk}, last={LastPrice} book={LastBid}/{LastAsk})");
         StopChangesRejected++;
         if (UnmanagedNt8Events)
            QueueDeferredOrderEvent(from, order, order.OrderState == OrderState.Working ? OrderState.Working : OrderState.Accepted,
                                    ErrorCode.UnableToChangeOrder, native);
         return true;
      }

      /// <summary>[STOP-CHANGE-REJECT] counter for the host's end line.</summary>
      public int StopChangesRejected { get; private set; }

      // [NT8-SECOND-GRID] a changed stop/target is effective from its next whole second; until then
      // the OLD level is what the market can hit (NT8 processes the change with the next data second).
      // The stop trace records the change at once, as before - it documents the strategy's decision.
      private void ApplyStopChange(NinjaScriptBase from, ShimPosition pos, double stop)
      {
         RecordStopTrace(from, pos, stop);
         if (Nt8SecondGrid)
            WriteLine?.Invoke($"[STOP-CHANGE] {(pos.IsLong ? "L" : "S")} sig='{pos.SignalName}' {Now:yyyy-MM-dd HH:mm:ss.ffffff} "
                            + $"stop {pos.StopLossPx:F2} -> {stop:F2} (pending {pos.PendingStopPx:F2}) last={LastPrice} book={LastBid}/{LastAsk} grid={GridBid}/{GridAsk}");
         if (OrderActivationInUse) { pos.PendingStopPx = stop; pos.PendingStopActiveTs = LegActivationFor(Now); }
         else pos.StopLossPx = stop;
      }

      private void ApplyTargetChange(ShimPosition pos, double limit)
      {
         if (OrderActivationInUse) { pos.PendingTargetPx = limit; pos.PendingTargetActiveTs = LegActivationFor(Now); }
         else pos.TakeProfitPx = limit;
      }

      private readonly List<(NinjaScriptBase from, Order order, OrderState state, ErrorCode error, string nativeError)> _deferredOrderEvents = new();

      private void QueueDeferredOrderEvents(NinjaScriptBase from, Order order)
      {
         _deferredOrderEvents.Add((from, order, OrderState.Accepted, ErrorCode.NoError, ""));
         _deferredOrderEvents.Add((from, order, OrderState.Working, ErrorCode.NoError, ""));
      }

      /// <summary>one deferred OnOrderUpdate: a leg's Cancelled confirmation, or NT8's rejection of a
      /// change (the original order in its old state plus the error) - raised on the next tick.</summary>
      private void QueueDeferredOrderEvent(NinjaScriptBase from, Order order, OrderState state, ErrorCode error, string nativeError)
         => _deferredOrderEvents.Add((from, order, state, error, nativeError));

      private void FlushDeferredOrderEvents()
      {
         if (_deferredOrderEvents.Count == 0) return;
         var batch = _deferredOrderEvents.ToArray();
         _deferredOrderEvents.Clear();
         foreach (var (from, order, state, error, nativeError) in batch)
         {
            // A leg that was filled or cancelled in between keeps its final state - except that the
            // queued event IS that final state's confirmation (a cancelled leg's Cancelled).
            if (order.OrderState == OrderState.Filled) continue;
            if ((order.OrderState == OrderState.Cancelled || order.OrderState == OrderState.Rejected) && state != order.OrderState) continue;
            FireOrderUpdate(from, order, state, 0, 0, error, nativeError);
         }
      }

      public Order SubmitOrderUnmanaged(NinjaScriptBase from, int bip, OrderAction action, OrderType type,
                                        int qty, double limit, double stop, string oco, string sig)
      {
         var order = new Order { OrderAction = action, OrderType = type, Quantity = qty,
                                 LimitPrice = limit, StopPrice = stop, Oco = oco, SignalName = sig,
                                 Name = sig, OrderState = OrderState.Working };

         if (action == OrderAction.Buy || action == OrderAction.SellShort)
         {
            if (UnmanagedNt8Events && type == OrderType.Limit)
            {
               return EnterLimitImpl(from, qty, limit, action == OrderAction.Buy, sig,
                                     oco: oco, raiseOrderEvents: true);
            }
            var entryOrder = action == OrderAction.Buy
               ? EnterLong(from, bip, qty, sig)
               : EnterShort(from, bip, qty, sig);
            if (entryOrder != null && _lastEnqueuedEntry != null
                && _lastEnqueuedEntry.SignalName == (string.IsNullOrEmpty(sig) ? _lastEnqueuedEntry.SignalName : sig))
            {
               entryOrder.Name = sig;             // NT8 unmanaged: Name == signalName
               entryOrder.OrderType = type;
               _lastEnqueuedEntry.NtOrder = entryOrder;
               _lastEnqueuedEntry.Owner = from;
               _lastEnqueuedEntry.RaiseOrderEvents = true;
               _unmanagedLifecycleActive = true;
            }
            return entryOrder;
         }

         // ---- Exits (Sell = close long, BuyToCover = close short) --------
         bool exitsLong = action == OrderAction.Sell;
         if (type == OrderType.Market)
         {
            if (UnmanagedNt8Events)
            {
               // [NT8-UNMANAGED] ledger close at the fill price, engine mirror,
               // NT8's order of events - see CloseUnmanagedAtMarket.
               CloseUnmanagedAtMarket(from, exitsLong, order);
               return order;
            }
            if (exitsLong) ExitLong(from, bip, qty, sig, FindOpenSignal(isLong: true));
            else           ExitShort(from, bip, qty, sig, FindOpenSignal(isLong: false));
            FireOrderUpdate(from, order, OrderState.Filled, LastPrice, qty);
            return order;
         }
         bool attachedLeg = false;   // [DEFERRED-ORDER-EVENTS] a stop/target leg found its position
         foreach (var pos in _openPositions.Values)
         {
            if (pos.IsLong != exitsLong) continue;
            if (type == OrderType.Stop || type == OrderType.StopMarket || type == OrderType.StopLimit)
            {
               RecordStopTrace(from, pos, stop, "initial");
               pos.StopLossPx = stop;
               pos.StopOrder  = order;
               pos.StopActiveTs = LegActivationFor(Now);   // [NT8-SECOND-GRID]
               pos.Owner      = pos.Owner ?? from;
               pos.RaiseOrderEvents = true;
               attachedLeg = true;
            }
            else if (type == OrderType.Limit || type == OrderType.MIT)
            {
               pos.TakeProfitPx = limit;
               pos.TargetOrder  = order;
               pos.TargetActiveTs = LegActivationFor(Now);   // [NT8-SECOND-GRID]
               pos.Owner        = pos.Owner ?? from;
               pos.RaiseOrderEvents = true;
               attachedLeg = true;
            }
         }
         if (attachedLeg && UnmanagedNt8Events)
         {
            order.OrderState = OrderState.Submitted;
            FireOrderUpdate(from, order, OrderState.Accepted, 0, 0);
            FireOrderUpdate(from, order, OrderState.Working, 0, 0);
         }
         return order;
      }

      private string FindOpenSignal(bool isLong)
      {
         foreach (var kv in _openPositions)
            if (kv.Value.IsLong == isLong) return kv.Key;
         return "";
      }

      public int GetPositionQuantity(int bip)
      {
         int q = 0;
         foreach (var pos in _openPositions.Values)
            q += pos.IsLong ? pos.Quantity : -pos.Quantity;
         return q;
      }

      internal int OpenQuantityOf(string fromEntrySignal)
         => _openPositions.TryGetValue(fromEntrySignal ?? "", out var pos) ? pos.Quantity : 0;
      public Position[] GetPositions() => Array.Empty<Position>();
   }
}

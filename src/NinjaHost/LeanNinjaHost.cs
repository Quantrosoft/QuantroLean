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
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using QuantroLean.NinjaShim;
using QuantroLean.NinjaShim.Hosting;
using NinjaTrader.NinjaScript;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data.Consolidators;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;
using Strategy = NinjaTrader.NinjaScript.Strategies.Strategy;

namespace QuantroLean.NinjaHost
{
   /// <summary>The primary series of the hosted strategy, from the "bars" parameter: Minute,N | Second,N | Tick,N.</summary>
   public sealed class PrimarySeriesSpec
   {
      public NinjaTrader.Data.BarsPeriodType Type { get; private set; }
      public int Value { get; private set; }

      /// <summary>Bar length of a time series, zero for tick-count bars.</summary>
      public TimeSpan Span => Type == NinjaTrader.Data.BarsPeriodType.Minute ? TimeSpan.FromMinutes(Value)
                            : Type == NinjaTrader.Data.BarsPeriodType.Second ? TimeSpan.FromSeconds(Value)
                            : TimeSpan.Zero;

      /// <summary>A hosted NT8 bars type (Range/Renko): built by feeding ticks to NinjaTrader's own bars type,
      /// available only when the NinjaShim is built with NINJASHIM_NT8_BARSTYPES. Neither time- nor
      /// tick-count-based, so it takes its own attach path.</summary>
      // Tick bars too: NinjaTrader's own TickBarsType counts the ticks of a bar and restarts the count at a new
      // trading day when the series resets; a home-grown counter closes the bar inside its N-th tick instead.
      public bool IsHosted => Type == NinjaTrader.Data.BarsPeriodType.Range
                           || Type == NinjaTrader.Data.BarsPeriodType.Renko
                           || Type == NinjaTrader.Data.BarsPeriodType.Tick;

      public static PrimarySeriesSpec Parse(string text)
      {
         var parts = (text ?? "").Split(',');
         if (parts.Length != 2
             || !int.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
             || value <= 0)
            throw new ArgumentException($"[NINJAHOST] parameter 'bars' = '{text}': expected Minute,N | Second,N | Tick,N with N > 0.");
         var type = parts[0].Trim().ToLowerInvariant() switch
         {
            "minute" => NinjaTrader.Data.BarsPeriodType.Minute,
            "second" => NinjaTrader.Data.BarsPeriodType.Second,
            "tick" => NinjaTrader.Data.BarsPeriodType.Tick,
            // Range (and Renko) are hosted NT8 bars types: only available when the NinjaShim is built with
            // NINJASHIM_NT8_BARSTYPES (a local NinjaTrader 8 install linked at build time; see NinjaTrader8.props).
            "range" => NinjaTrader.Data.BarsPeriodType.Range,
            "renko" => NinjaTrader.Data.BarsPeriodType.Renko,
            _ => throw new ArgumentException($"[NINJAHOST] parameter 'bars' = '{text}': the type must be Minute, Second, Tick, Range or Renko."),
         };
         return new PrimarySeriesSpec { Type = type, Value = value };
      }

      public override string ToString() => Type + "," + Value.ToString(CultureInfo.InvariantCulture);
   }

   /// <summary>Hosts one NinjaTrader 8 strategy on a Lean algorithm: builds the strategy's bars from Lean's ticks,
   /// drives its NinjaScript lifecycle, and mirrors its orders as Lean market orders (LeanShimOrderRouter).
   ///
   /// Ticks: a Lean trade tick is a NinjaTrader "Last" (price, size and the current bid/ask); a Lean quote tick
   /// updates the bid/ask (a side with price 0 keeps its previous value). Inside one time step the algorithm hands
   /// over the quotes before the trades (NinjaScriptAlgorithm.OnData). Order fills follow NinjaTrader's rules
   /// in the shim: a buy fills at the ask, a sell at the bid, a long stop triggers on the bid, a short stop on the
   /// ask. The shim's own position is checked against Lean's after every tick in a backtest; a difference stops
   /// the run.</summary>
   public sealed class LeanNinjaHost
   {
      private readonly QCAlgorithm _algorithm;
      private readonly Security _security;
      private readonly Strategy _strategy;
      private readonly ShimRuntime _runtime;
      private readonly LeanShimOrderRouter _router;
      private readonly NinjaTrader.Data.Bars _bars;
      private readonly PrimarySeriesSpec _barsSpec;
      private readonly EquityWatermark _equity = new EquityWatermark();
      private readonly bool _verifyBars;
      private readonly bool _resetOnNewTradingDay;
      private readonly TickConsolidator _leanBars;
      private readonly Queue<TradeBar> _leanBarQueue = new Queue<TradeBar>();
      private readonly string _strategyName;
      private readonly bool _marketReplay;
      private readonly bool _formingBar;
      private readonly bool _exitOnSessionClose;
      private readonly bool _quotesUpdateBookOnly;
      private int _sessionClosesBeforeEnd;

      private bool _started;
      private bool _finished;
      private double _bid, _ask;
      private long _tradeTicks, _quoteTicks;
      private int _barsVerified;
      private int _barsSeen;
      private int _leanFills;
      private DateTime _lastTickUtc;

      public LeanNinjaHost(QCAlgorithm algorithm, Security security, string strategyName, PrimarySeriesSpec bars,
                           IEnumerable<KeyValuePair<string, string>> strategyOverrides, bool fillAtNextTick,
                           bool localBarTimes, bool verifyBars, bool resetOnNewTradingDay = false,
                           bool marketReplay = false, bool formingBar = false, bool nt8SecondGrid = false,
                           bool gridEntryAtGridBook = false, bool gridStopFillWorst = false, bool exitOnSessionClose = false,
                           bool gridStrategyBookAtBatchEnd = true, bool quotesUpdateBookOnly = false)
      {
         _algorithm = algorithm ?? throw new ArgumentNullException(nameof(algorithm));
         _security = security ?? throw new ArgumentNullException(nameof(security));
         _barsSpec = bars ?? throw new ArgumentNullException(nameof(bars));
         _strategyName = strategyName;
         _verifyBars = verifyBars;
         _resetOnNewTradingDay = resetOnNewTradingDay;
         _marketReplay = marketReplay;
         _formingBar = formingBar;
         _exitOnSessionClose = exitOnSessionClose;
         _quotesUpdateBookOnly = quotesUpdateBookOnly;
         if ((gridEntryAtGridBook || gridStopFillWorst) && !nt8SecondGrid)
            throw new ArgumentException("[NINJAHOST] nt8-second-grid-entry-at-grid-book and nt8-second-grid-stop-fill-worst "
                                      + "need nt8-second-grid.");
         if (marketReplay && algorithm.LiveMode)
            throw new ArgumentException("[NINJAHOST] market-replay is a backtest mode; a live run is live already.");
         if (verifyBars && bars.IsHosted)
            throw new ArgumentException(
               "[NINJAHOST] verify-bars is not supported for a hosted bars type (Tick/Range/Renko): Lean's "
             + "TickConsolidator cannot reproduce it. Set verify-bars=false for these series.");

         _strategy = (Strategy)Activator.CreateInstance(FindStrategyType(strategyName));

         var sessionEndMinute = RegularSessionEndMinute(security, algorithm.StartDate);
         var spec = new ShimInstrumentSpec
         {
            SymbolName = security.Symbol.Value,
            TickSize = (double)security.SymbolProperties.MinimumPriceVariation,
            PointValue = (double)security.SymbolProperties.ContractMultiplier,
            SessionEndTimeZoneId = security.Exchange.TimeZone.Id,
            SessionEndMinuteOfDay = sessionEndMinute,
         };
         _runtime = new ShimRuntime
         {
            WriteLine = line => _algorithm.Log(line),
            VerboseErrors = true,
            DefaultInstrument = spec.ToInstrument(),
            SessionEndTimeZone = spec.ResolveSessionEndTimeZone(),
            SessionEndMinuteOfDay = sessionEndMinute,
         };
         _runtime.FillAtBbo = true;
         _runtime.ExitTriggerAtBbo = true;
         _runtime.FillAtNextTick = fillAtNextTick;
         _runtime.Nt8LocalBarTimes = localBarTimes;
         _runtime.IsBacktest = !algorithm.LiveMode && !marketReplay;
         _runtime.LiveMode = algorithm.LiveMode;
         // Set before the primary series is attached: the bar stream takes the switch when it is created.
         _runtime.EmulateFormingBar = formingBar;
         _runtime.HostedIndicatorsFollowStrategyCalculate = formingBar;
         _runtime.Nt8SecondGrid = nt8SecondGrid;
         _runtime.Nt8SecondGridEntryAtGridBook = gridEntryAtGridBook;
         _runtime.Nt8SecondGridStopFillWorst = gridStopFillWorst;
         _runtime.Nt8SecondGridStrategyBookAtBatchEnd = gridStrategyBookAtBatchEnd;
         if (marketReplay)
         {
            // NinjaTrader's Market Replay: the strategy is no Strategy Analyzer run, the playback connection is
            // connected (a strategy's IsPlayback test reads Connection.PlaybackConnection.Status), and the replay
            // account sits on that connection.
            _runtime.PlaybackMode = true;
            var playback = new NinjaTrader.Data.PlaybackConnectionInfo { Status = NinjaTrader.Data.ConnectionStatus.Connected };
            _strategy.Connection.PlaybackConnection = playback;
            _strategy.Account.Connection = playback;
         }
         else
            _strategy.Account.Connection = new NinjaTrader.Data.PlaybackConnectionInfo
            {
               Status = algorithm.LiveMode ? NinjaTrader.Data.ConnectionStatus.Connected : NinjaTrader.Data.ConnectionStatus.Disconnected,
            };
         // NinjaTrader's accounts always carry a name (the Strategy Analyzer's is "Backtest", Market Replay's
         // "Playback101"); strategies read it, e.g. Account.Name.ToLower(), and a null name throws there.
         if (string.IsNullOrEmpty(_strategy.Account.Name))
         {
            _strategy.Account.Name = algorithm.LiveMode ? "Live" : marketReplay ? "Playback101" : "Backtest";
            _strategy.Account.DisplayName = _strategy.Account.Name;
         }

         _runtime.Host(_strategy);
         _runtime.DriveLifecycle(_strategy, State.SetDefaults);
         ShimParamOverrides.Apply(_strategy, strategyOverrides ?? Array.Empty<KeyValuePair<string, string>>(),
            line => _algorithm.Log("[NINJAHOST] " + line));
         // NinjaTrader's order semantics follow the strategy's own declaration
         _runtime.UnmanagedNt8Events = _strategy.IsUnmanaged;
         _runtime.ExecutionsFromLedger = !_strategy.IsUnmanaged;

         _bars = new NinjaTrader.Data.Bars
         {
            Instrument = _runtime.DefaultInstrument,
            BarsPeriod = new NinjaTrader.Data.BarsPeriod { BarsPeriodType = bars.Type, Value = bars.Value },
            FromDate = algorithm.StartDate,
            ToDate = algorithm.EndDate,
         };
         if (bars.IsHosted) _runtime.AttachPrimaryHostedBars(_bars, bars.Type, bars.Value, _resetOnNewTradingDay);
         else _runtime.AttachPrimaryBars(_bars, bars.Span);

         _router = new LeanShimOrderRouter(algorithm, security.Symbol);
         _runtime.OrderRouter = _router;
         _runtime.DriveLifecycle(_strategy, State.Configure);

         if (_verifyBars)
         {
            _leanBars = bars.Type == NinjaTrader.Data.BarsPeriodType.Tick
               ? new TickConsolidator(bars.Value)
               : new TickConsolidator(bars.Span);
            _leanBars.DataConsolidated += (sender, bar) => _leanBarQueue.Enqueue(bar);
         }

         _algorithm.Log(string.Format(CultureInfo.InvariantCulture,
            "[NINJAHOST] strategy {0} on {1} ({2}, tick size {3}, point value {4}), bars {5}, session end {6:D2}:{7:D2} {8}, "
            + "{9} order handling, fill at next tick {10}, local bar times {11}, verify bars {12}, reset on new day {13}, "
            + "market replay {14}, forming bar {15}, second grid {16} (entry at grid book {17}, stop fill worst {18}, "
            + "strategy book at batch end {20}), exit on session close {19}, quotes update the book only {21}",
            _strategy.GetType().Name, security.Symbol.Value, security.Type, spec.TickSize, spec.PointValue, bars,
            sessionEndMinute / 60, sessionEndMinute % 60, spec.SessionEndTimeZoneId,
            _strategy.IsUnmanaged ? "unmanaged" : "managed", fillAtNextTick, localBarTimes, verifyBars,
            resetOnNewTradingDay, marketReplay, formingBar, nt8SecondGrid, gridEntryAtGridBook, gridStopFillWorst,
            exitOnSessionClose, gridStrategyBookAtBatchEnd, quotesUpdateBookOnly));
      }

      /// <summary>One Lean tick, with its time in UTC.</summary>
      public void OnTick(Tick tick, DateTime utc)
      {
         if (_finished) return;
         if (!_started)
         {
            _started = true;
            _runtime.DriveLifecycle(_strategy, State.DataLoaded);
            _runtime.DriveLifecycle(_strategy, State.Historical);
            if (_algorithm.LiveMode || _marketReplay)
            {
               _runtime.DriveLifecycle(_strategy, State.Transition);
               _runtime.DriveLifecycle(_strategy, State.Realtime);
            }
            _algorithm.Log(string.Format(CultureInfo.InvariantCulture, "[NINJAHOST] first tick {0:yyyy-MM-dd HH:mm:ss.fff}Z", utc));
         }

         if (tick.TickType == TickType.Quote)
         {
            _quoteTicks++;
            if (tick.BidPrice > 0) _bid = (double)tick.BidPrice;
            if (tick.AskPrice > 0) _ask = (double)tick.AskPrice;
            if (_bid <= 0 || _ask <= 0) return;   // no complete book yet
            if (_quotesUpdateBookOnly) return;     // the next trade tick carries this book
            _runtime.PumpTick(new ShimTick { Ts = utc, Price = 0, Bid = _bid, Ask = _ask, Volume = 0, IsLast = false });
         }
         else if (tick.TickType == TickType.Trade)
         {
            if (tick.Price <= 0)
               throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                  "[NINJAHOST] trade tick without a price at {0:yyyy-MM-dd HH:mm:ss.fff}Z. No fallback.", utc));
            _tradeTicks++;
            var shimTick = new ShimTick
            {
               Ts = utc, Price = (double)tick.Price, Bid = _bid, Ask = _ask, Volume = (long)tick.Quantity, IsLast = true,
            };
            CloseBeforeSessionEnd(shimTick);
            _runtime.PumpTick(shimTick);
            if (_verifyBars) _leanBars.Update(tick);
         }
         else
         {
            return;   // open interest and other tick types carry no price for the strategy
         }

         _lastTickUtc = utc;
         if (_verifyBars) VerifyNewBars();
         _equity.Sample(EquityAtExitPrices(), utc);
         if (!_algorithm.LiveMode) CheckPositionsAgree(utc);
      }

      public void OnOrderEvent(OrderEvent orderEvent)
      {
         if (orderEvent.Status == OrderStatus.Invalid)
            throw new InvalidOperationException(
               $"[NINJAHOST] Lean marked order {orderEvent.OrderId} invalid: {orderEvent.Message}. No fallback.");
         if (orderEvent.Status == OrderStatus.Filled || orderEvent.Status == OrderStatus.PartiallyFilled)
            _leanFills++;
      }

      /// <summary>exit-on-session-close: NinjaTrader's rule - a strategy with IsExitOnSessionCloseStrategy closes its open
      /// positions at the first trade from ExitOnSessionCloseSeconds before the session end on (session end =
      /// SessionEndMinuteOfDay in the session zone), at that trade. Checked before the trade is pumped.</summary>
      private void CloseBeforeSessionEnd(ShimTick t)
      {
         if (!_exitOnSessionClose || !_strategy.IsExitOnSessionCloseStrategy || _runtime.OpenPositionCount == 0) return;
         var local = _runtime.ToSessionLocal(t.Ts);
         var end = local.Date.AddMinutes(_runtime.SessionEndMinuteOfDay);
         if (local >= end) return;   // at or after the end: the shim's own tick-driven close
         if (local >= end.AddSeconds(-Math.Max(0, _strategy.ExitOnSessionCloseSeconds)))
            _sessionClosesBeforeEnd += _runtime.CloseSessionPositionsBeforeClose(t);
      }

      /// <summary>End of data: open shim positions are recorded as "EndOfData" trades (Lean keeps its holdings),
      /// the strategy is terminated, the trade list is written and the summary printed.</summary>
      public void Finish()
      {
         if (_finished) return;
         _finished = true;
         _runtime.FlushOpenPositionsAtEndOfData();
         if (_algorithm.LiveMode) _runtime.DriveLifecycle(_strategy, State.Realtime);
         _runtime.DriveLifecycle(_strategy, State.Terminated);

         var path = WriteTradeList();
         var currency = _algorithm.Portfolio.CashBook.AccountCurrency;
         _algorithm.Log("[NINJAHOST][EQUITY] " + _equity.Summary(currency));
         if (_exitOnSessionClose)
            _algorithm.Log(string.Format(CultureInfo.InvariantCulture,
               "[NINJAHOST][SESSION-CLOSE] positions closed before the session end {0}, closed by the first tick after it {1}",
               _sessionClosesBeforeEnd, _runtime.TickDrivenSessionCloses));
         _algorithm.Log(string.Format(CultureInfo.InvariantCulture,
            "[NINJAHOST][END] trade ticks {0}, quote ticks {1}, last tick {2:yyyy-MM-dd HH:mm:ss.fff}Z, primary bars {3}, "
            + "bars verified against Lean {4}, strategy trades {5}, entries routed {6}, exits routed {7}, Lean fills {8}, "
            + "trade list {9}",
            _tradeTicks, _quoteTicks, _lastTickUtc, _bars.Count, _verifyBars ? _barsVerified.ToString(CultureInfo.InvariantCulture) : "off",
            _runtime.TradeLedger.Count, _router.EntryOrders, _router.ExitOrders, _leanFills, path));
      }

      private static Type FindStrategyType(string name)
      {
         if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("[NINJAHOST] parameter 'strategy' is empty.");
         var candidates = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a =>
            {
               try { return a.GetTypes(); }
               catch (System.Reflection.ReflectionTypeLoadException e) { return e.Types.Where(t => t != null); }
            })
            .Where(t => typeof(Strategy).IsAssignableFrom(t) && !t.IsAbstract
                        && (t.Name == name || t.FullName == name))
            .ToList();
         if (candidates.Count != 1)
            throw new ArgumentException($"[NINJAHOST] strategy '{name}': {candidates.Count} matching NinjaScript strategy "
               + "classes found (expected exactly one; give the full name when the class name is ambiguous).");
         return candidates[0];
      }

      /// <summary>Minute of day (exchange time) at which the regular session ends, read from Lean's market hours
      /// for the first trading day from the start date on that is not an early close. Early closes of single days
      /// are not taken over: until 15.10.2026 the first trading day was taken as it came, so a run starting the
      /// weekend before Labor Day (early close 13:00) ran every day with the session end 13:00 (measured with a
      /// strategy on NQ, start 31.08.2025, "session end 13:00", bar sessions from 13:00 and two positions closed at
      /// 12:59:28 that NinjaTrader held).</summary>
      private static int RegularSessionEndMinute(Security security, DateTime startDate)
      {
         var hours = security.Exchange.Hours;
         var close = hours.GetNextMarketClose(startDate.Date, false);
         for (int i = 0; i < 31 && hours.EarlyCloses.ContainsKey(close.Date); i++)
            close = hours.GetNextMarketClose(close.Date.AddDays(1), false);
         if (hours.EarlyCloses.ContainsKey(close.Date))
            throw new InvalidOperationException($"[NINJAHOST] no regular session end within 31 trading days from {startDate:yyyy-MM-dd}: "
               + "every market close Lean's market hours give for that span is an early close.");
         return (int)close.TimeOfDay.TotalMinutes;
      }

      /// <summary>Account equity with the open position valued at its market exit price.</summary>
      private double EquityAtExitPrices()
      {
         var equity = (double)_algorithm.Portfolio.TotalPortfolioValue;
         var quantity = (double)_algorithm.Portfolio[_security.Symbol].Quantity;
         if (quantity == 0) return equity;
         var exitPrice = quantity > 0 ? (double)_security.BidPrice : (double)_security.AskPrice;
         if (exitPrice <= 0) return equity;
         return equity + quantity * (exitPrice - (double)_security.Price)
                * (double)_security.SymbolProperties.ContractMultiplier
                * (double)_security.QuoteCurrency.ConversionRate;
      }

      private void CheckPositionsAgree(DateTime utc)
      {
         var position = _strategy.Position;
         var shim = position.MarketPosition == NinjaTrader.Cbi.MarketPosition.Long ? position.Quantity
                  : position.MarketPosition == NinjaTrader.Cbi.MarketPosition.Short ? -position.Quantity : 0;
         var expected = shim + _runtime.PendingEntryNetQuantity;
         var lean = _algorithm.Portfolio[_security.Symbol].Quantity;
         if (lean != expected)
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
               "[NINJAHOST] position mismatch at {0:yyyy-MM-dd HH:mm:ss.fff}Z: Lean holds {1}, the strategy {2} "
               + "(+{3} submitted, not yet filled). No fallback.", utc, lean, shim, _runtime.PendingEntryNetQuantity));
      }

      /// <summary>Compare every primary bar the shim closed with the bar Lean's own TickConsolidator built from the
      /// same trade ticks: open, high, low, close, volume and end time must be identical.</summary>
      private void VerifyNewBars()
      {
         while (_barsSeen < _bars.Count)
         {
            var i = _barsSeen++;
            if (_leanBarQueue.Count == 0)
               throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                  "[NINJAHOST][VERIFY-BARS] the shim closed bar {0} (end {1:yyyy-MM-dd HH:mm:ss}Z), Lean's consolidator "
                  + "has no bar for it. No fallback.", i, _bars.GetTime(i)));
            var lean = _leanBarQueue.Dequeue();
            var leanEndUtc = lean.EndTime.ConvertToUtc(_security.Exchange.TimeZone);
            var shimEnd = _bars.GetTime(i);
            bool timeSame = _barsSpec.Type == NinjaTrader.Data.BarsPeriodType.Tick || shimEnd == leanEndUtc;
            if (!timeSame
                || _bars.GetOpen(i) != (double)lean.Open || _bars.GetHigh(i) != (double)lean.High
                || _bars.GetLow(i) != (double)lean.Low || _bars.GetClose(i) != (double)lean.Close
                || _bars.GetVolume(i) != (long)lean.Volume)
               throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
                  "[NINJAHOST][VERIFY-BARS] bar {0} differs: shim end {1:yyyy-MM-dd HH:mm:ss}Z O {2} H {3} L {4} C {5} V {6}; "
                  + "Lean end {7:yyyy-MM-dd HH:mm:ss}Z O {8} H {9} L {10} C {11} V {12}. No fallback.",
                  i, shimEnd, _bars.GetOpen(i), _bars.GetHigh(i), _bars.GetLow(i), _bars.GetClose(i), _bars.GetVolume(i),
                  leanEndUtc, lean.Open, lean.High, lean.Low, lean.Close, lean.Volume));
            _barsVerified++;
         }
      }

      private string WriteTradeList()
      {
         var folder = QuantConnect.Configuration.Config.Get("results-destination-folder", Directory.GetCurrentDirectory());
         Directory.CreateDirectory(folder);
         var path = Path.Combine(folder, $"{_strategyName}-{_security.Symbol.Value}-trades.csv");
         var ic = CultureInfo.InvariantCulture;
         var sb = new StringBuilder();
         sb.Append("trade,signal,direction,quantity,entry_utc,entry_price,exit_utc,exit_price,exit_reason,points,commission\n");
         foreach (var t in _runtime.TradeLedger)
         {
            var points = (t.ExitPrice - t.EntryPrice) * (t.IsLong ? 1 : -1);
            // points rounded to 10 decimals: prices are tick multiples, the rest is binary floating point noise
            sb.Append(string.Format(ic, "{0},{1},{2},{3},{4:yyyy-MM-dd HH:mm:ss.fff},{5},{6:yyyy-MM-dd HH:mm:ss.fff},{7},{8},{9},{10}\n",
               t.Idx, Csv(t.SignalName), t.IsLong ? "long" : "short", t.Quantity, t.EntryTs, t.EntryPrice, t.ExitTs, t.ExitPrice,
               Csv(t.ExitReason), Math.Round(points, 10), t.Commission));
         }
         var tmp = path + ".tmp";
         File.WriteAllText(tmp, sb.ToString());
         File.Move(tmp, path, overwrite: true);
         return path;
      }

      /// <summary>Quotes a CSV field (RFC 4180) when it holds a comma, a quote or a line break - the signal and the
      /// exit reason are free text from the strategy and may carry a comma. Other fields are numbers, timestamps or a
      /// fixed word and need no quoting.</summary>
      private static string Csv(string field)
      {
         if (string.IsNullOrEmpty(field)) return string.Empty;
         if (field.IndexOf(',') < 0 && field.IndexOf('"') < 0 && field.IndexOf('\n') < 0 && field.IndexOf('\r') < 0)
            return field;
         return "\"" + field.Replace("\"", "\"\"") + "\"";
      }
   }
}

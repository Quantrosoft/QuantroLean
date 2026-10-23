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
using System.Linq;
using QuantConnect;
using QuantConnect.Algorithm;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Orders;
using QuantConnect.Securities;

namespace MyLean.NinjaHost
{
   /// <summary>Lean algorithm that runs one NinjaTrader 8 strategy on Lean's tick data. The strategy's NinjaScript
   /// source is compiled into the same assembly as MyLean.NinjaShim and this host (see src/NinjaHost/NinjaHost.props).
   ///
   /// Parameters (config.json "parameters"):
   ///   strategy           class name of the NinjaScript strategy
   ///   symbol             ticker, e.g. SPY or NQ
   ///   security-type      Equity, Forex, Cfd or Future
   ///   market             Lean market, e.g. usa, oanda, cme
   ///   contract-expiry    futures only: the dated contract's expiry, yyyy-MM-dd (e.g. 2025-09-19 for NQU25)
   ///   start, end         first and last day, yyyy-MM-dd
   ///   cash               starting cash in account currency
   ///   bars               primary series of the strategy: Minute,N | Second,N | Tick,N | Range,N | Renko,N
   ///                      (Range/Renko are hosted NT8 bars types: build the NinjaShim with NINJASHIM_NT8_BARSTYPES)
   ///   reset-on-new-day   true/false, default false: hosted bars (Tick/Range/Renko) restart each trading day
   ///   fill-at-next-tick  true/false, default false: a market order fills on the tick after the one it was sent on
   ///   local-bar-times    true/false, default true: the strategy's Time[] in the time zone of this PC, as NinjaTrader shows it
   ///   verify-bars        true/false, default false: compare every primary bar with Lean's own consolidation of the ticks
   ///   extended-market-hours true/false, default false: also the data outside the regular session (a futures
   ///                      exchange's overnight session, as NinjaTrader's trading hours of the instrument include it)
   ///   market-replay      true/false, default false (backtests only): run the strategy as NinjaTrader's Market Replay
   ///                      runs it - the playback connection is connected (IsPlayback), the strategy is no Strategy
   ///                      Analyzer run, and it goes through Transition to State.Realtime at the first tick
   ///   forming-bar        true/false, default false: NinjaTrader's view for a Calculate.OnEachTick strategy - [0] is the
   ///                      bar that is forming, the strategy and its hosted indicators are driven on every tick of it
   ///   nt8-second-grid    true/false, default false: NinjaTrader Market Replay's order timing - orders become marketable at
   ///                      the next whole second and fill on the next event of their side
   ///   nt8-second-grid-entry-at-grid-book true/false, default false (needs nt8-second-grid): a market entry fills at the
   ///                      book of its submit second, stamped at the submit
   ///   nt8-second-grid-stop-fill-worst    true/false, default false (needs nt8-second-grid): a triggered stop fills at the
   ///                      worse of its level, the next event and that event's book side
   ///   nt8-second-grid-strategy-book-at-batch-end true/false, default true (with nt8-second-grid): the strategy's
   ///                      GetCurrentBid/Ask answer the book at the end of the second's batch; false = the book of the
   ///                      event being dispatched
   ///   quotes-update-book-only true/false, default false: a quote tick only updates the book the next trade tick carries
   ///                      and is no event of its own - for data whose quotes are the book of each trade print (a trade
   ///                      tape with its book), so that one print is one event as in the tape
   ///   exit-on-session-close true/false, default false: a strategy with IsExitOnSessionCloseStrategy closes its positions
   ///                      from ExitOnSessionCloseSeconds before the session end on, as NinjaTrader does (otherwise the
   ///                      first tick after the end closes them at the last price before it)
   ///   p_&lt;Property&gt;    value for a public property of the strategy, set after State.SetDefaults
   /// </summary>
   public class NinjaScriptAlgorithm : QCAlgorithm
   {
      private LeanNinjaHost _host;
      private Security _security;

      public override void Initialize()
      {
         var parameters = GetParameters();

         string Required(string key)
         {
            if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
               throw new ArgumentException($"[NINJAHOST] parameter '{key}' is missing.");
            return value.Trim();
         }

         bool Flag(string key, bool defaultValue)
         {
            if (!parameters.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value)) return defaultValue;
            if (!bool.TryParse(value.Trim(), out var flag))
               throw new ArgumentException($"[NINJAHOST] parameter '{key}' = '{value}' is not true/false.");
            return flag;
         }

         DateTime Day(string key) => DateTime.ParseExact(Required(key), "yyyy-MM-dd", CultureInfo.InvariantCulture);

         SetStartDate(Day("start"));
         SetEndDate(Day("end"));
         SetCash(decimal.Parse(Required("cash"), NumberStyles.Number, CultureInfo.InvariantCulture));

         var securityType = (SecurityType)Enum.Parse(typeof(SecurityType), Required("security-type"), ignoreCase: true);
         if (securityType == SecurityType.Future)
         {
            // One dated futures contract (e.g. NQ Sep-2025 = NQU25, expiry 2025-09-19). A benchmark window that
            // sits inside a single contract needs no continuous-contract roll; when a window crosses NT8's roll
            // date, run it per contract. 'contract-expiry' = the contract's expiry date, yyyy-MM-dd.
            var contract = QuantConnect.Symbol.CreateFuture(Required("symbol"), Required("market"), Day("contract-expiry"));
            _security = AddFutureContract(contract, Resolution.Tick, fillForward: false,
               extendedMarketHours: Flag("extended-market-hours", false));
         }
         else if (securityType == SecurityType.Equity || securityType == SecurityType.Forex || securityType == SecurityType.Cfd)
         {
            _security = AddSecurity(securityType, Required("symbol"), Resolution.Tick, Required("market"),
               fillForward: false, leverage: Security.NullLeverage, extendedMarketHours: Flag("extended-market-hours", false),
               dataNormalizationMode: securityType == SecurityType.Equity ? DataNormalizationMode.Raw : null);
         }
         else
            throw new ArgumentException($"[NINJAHOST] security-type '{securityType}' is not supported (Equity, Forex, Cfd, Future).");

         var overrides = parameters.Where(kv => kv.Key.StartsWith("p_", StringComparison.Ordinal)).ToList();
         _host = new LeanNinjaHost(this, _security, Required("strategy"), PrimarySeriesSpec.Parse(Required("bars")),
            overrides, Flag("fill-at-next-tick", false), Flag("local-bar-times", true), Flag("verify-bars", false),
            Flag("reset-on-new-day", false), Flag("market-replay", false),
            Flag("forming-bar", false), Flag("nt8-second-grid", false), Flag("nt8-second-grid-entry-at-grid-book", false),
            Flag("nt8-second-grid-stop-fill-worst", false), Flag("exit-on-session-close", false),
            Flag("nt8-second-grid-strategy-book-at-batch-end", true), Flag("quotes-update-book-only", false));
      }

      public override void OnData(Slice slice)
      {
         if (!slice.Ticks.TryGetValue(_security.Symbol, out var ticks)) return;
         // Trades and quotes come from separate recordings: inside one time step their order is unknown. The quotes of
         // the step go first, so the strategy's fills see the book at the end of the step - the book Lean fills the
         // mirrored orders against.
         foreach (var tick in ticks)
            if (tick.TickType == TickType.Quote) Forward(tick, slice);
         foreach (var tick in ticks)
            if (tick.TickType != TickType.Quote) Forward(tick, slice);
      }

      private void Forward(Tick tick, Slice slice)
      {
         // a tick's time is in the exchange time zone; every tick of a slice carries the slice's time
         var utc = tick.EndTime.ConvertToUtc(_security.Exchange.TimeZone);
         if (utc != slice.UtcTime)
            throw new InvalidOperationException(string.Format(CultureInfo.InvariantCulture,
               "[NINJAHOST] tick time {0:yyyy-MM-dd HH:mm:ss.fff} ({1}) = {2:yyyy-MM-dd HH:mm:ss.fff}Z differs from the "
               + "slice time {3:yyyy-MM-dd HH:mm:ss.fff}Z. No fallback.", tick.EndTime, _security.Exchange.TimeZone.Id, utc,
               slice.UtcTime));
         _host.OnTick(tick, utc);
      }

      public override void OnOrderEvent(OrderEvent orderEvent) => _host.OnOrderEvent(orderEvent);

      public override void OnEndOfAlgorithm() => _host.Finish();
   }
}

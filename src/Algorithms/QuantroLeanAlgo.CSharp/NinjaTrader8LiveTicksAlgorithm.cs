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
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Read-only live example: streams live ticks of one NinjaTrader 8 futures contract and prints them to the
    /// console. It places no orders and changes nothing on the account - it only subscribes and logs. Run it live
    /// against a local NinjaTrader 8 (a Demo/Sim connection is enough) through the NinjaTrader 8 brokerage:
    ///   QuantroLeanLauncher --environment live-ninjatrader8 --algorithm-type-name NinjaTrader8LiveTicksAlgorithm
    /// with ninjatrader8-gateway / -connection / -account set in config.json. The contract is the parameter
    /// "nt8-contract", a NinjaTrader contract name "ROOT MM-YY" (for example "MNQ 12-26").
    /// </summary>
    public class NinjaTrader8LiveTicksAlgorithm : QCAlgorithm
    {
        private Symbol _symbol;

        /// <summary>Subscribes to the configured futures contract at tick resolution. No cash or orders are set.</summary>
        public override void Initialize()
        {
            // Dates are ignored in live mode; set for the case someone runs it as a (data-less) backtest.
            SetStartDate(2024, 1, 1);

            var contract = GetParameter("nt8-contract", "MNQ 12-26");
            _symbol = ParseNinjaTraderContract(contract);
            AddFutureContract(_symbol, Resolution.Tick);
            Log($"NinjaTrader8LiveTicks: streaming live ticks for '{contract}' = {_symbol.Value} (read-only, no orders).");
        }

        /// <summary>Logs every incoming tick with its data-stream time; does not trade.</summary>
        public override void OnData(Slice slice)
        {
            if (!slice.Ticks.TryGetValue(_symbol, out var ticks))
            {
                return;
            }
            foreach (var tick in ticks)
            {
                if (tick.TickType == TickType.Quote)
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff} {1} quote bid={2} x{3} ask={4} x{5}",
                        tick.EndTime, _symbol.Value, tick.BidPrice, tick.BidSize, tick.AskPrice, tick.AskSize));
                }
                else
                {
                    Log(string.Format(CultureInfo.InvariantCulture, "{0:yyyy-MM-dd HH:mm:ss.fff} {1} {2} price={3} qty={4}",
                        tick.EndTime, _symbol.Value, tick.TickType, tick.Price, tick.Quantity));
                }
            }
        }

        /// <summary>Turns a NinjaTrader contract name "ROOT MM-YY" into the Lean future symbol (same mapping as the brokerage).</summary>
        private static Symbol ParseNinjaTraderContract(string name)
        {
            var parts = (name ?? "").Trim().Split(' ');
            if (parts.Length != 2 ||
                !DateTime.TryParseExact(parts[1], "MM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var contractMonth))
            {
                throw new ArgumentException(
                    $"the parameter 'nt8-contract' must be a NinjaTrader future name 'ROOT MM-YY', for example 'MNQ 12-26', not '{name}'");
            }
            var root = parts[0];
            var database = SymbolPropertiesDatabase.FromDataFolder();
            if (!database.TryGetMarket(root, SecurityType.Future, out var market))
            {
                throw new ArgumentException($"the symbol-properties database lists no future '{root}'");
            }
            var expiry = FuturesExpiryUtilityFunctions.GetFutureExpirationFromContractMonth(root, market, contractMonth);
            return QuantConnect.Symbol.CreateFuture(root, market, expiry);
        }
    }
}

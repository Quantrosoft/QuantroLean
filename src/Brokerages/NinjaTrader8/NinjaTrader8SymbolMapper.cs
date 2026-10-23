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
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Securities;
using QuantConnect.Securities.Future;

namespace MyLean.Brokerages.NinjaTrader8
{
    /// <summary>
    /// Lean symbols and NinjaTrader 8 instrument names. Futures only: NinjaTrader names a contract "ROOT MM-YY" by its
    /// contract month; Lean names it by its expiry. Both directions go through Lean's own contract month functions
    /// (FuturesExpiryUtilityFunctions), and the market of a root is the one Lean's symbol properties database lists.
    /// </summary>
    public sealed class NinjaTrader8SymbolMapper : ISymbolMapper
    {
        private readonly SymbolPropertiesDatabase _symbolProperties = SymbolPropertiesDatabase.FromDataFolder();

        /// <summary>"MNQ 12-26" for the Lean symbol of the December 2026 contract.</summary>
        public string GetBrokerageSymbol(Symbol symbol)
        {
            if (symbol == null) throw new ArgumentNullException(nameof(symbol));
            if (symbol.SecurityType != SecurityType.Future || symbol.IsCanonical())
                throw new ArgumentException("NinjaTrader 8: only future contracts are supported, not " + symbol.ToString() + " (" + symbol.SecurityType + ")");
            DateTime contractMonth = FuturesExpiryUtilityFunctions.GetFutureContractMonth(symbol);
            return symbol.ID.Symbol + " " + contractMonth.ToString("MM-yy", CultureInfo.InvariantCulture);
        }

        /// <summary>The Lean symbol of a NinjaTrader contract name "ROOT MM-YY".</summary>
        public Symbol GetLeanSymbol(string brokerageSymbol, SecurityType securityType, string market,
            DateTime expirationDate = default, decimal strike = 0, OptionRight optionRight = OptionRight.Call)
        {
            if (securityType != SecurityType.Future)
                throw new ArgumentException("NinjaTrader 8: only futures are supported, not " + securityType);
            return GetLeanSymbol(brokerageSymbol);
        }

        /// <summary>The Lean symbol of a NinjaTrader contract name "ROOT MM-YY".</summary>
        public Symbol GetLeanSymbol(string brokerageSymbol)
        {
            string[] parts = (brokerageSymbol ?? "").Trim().Split(' ');
            if (parts.Length != 2 || !DateTime.TryParseExact(parts[1], "MM-yy", CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime contractMonth))
                throw new ArgumentException("NinjaTrader 8: '" + brokerageSymbol + "' is not a future contract name 'ROOT MM-YY'");
            string root = parts[0];
            if (!_symbolProperties.TryGetMarket(root, SecurityType.Future, out string market))
                throw new ArgumentException("NinjaTrader 8: Lean's symbol properties database lists no future '" + root + "'");
            DateTime expiry = FuturesExpiryUtilityFunctions.GetFutureExpirationFromContractMonth(root, market, contractMonth);
            return Symbol.CreateFuture(root, market, expiry);
        }
    }
}

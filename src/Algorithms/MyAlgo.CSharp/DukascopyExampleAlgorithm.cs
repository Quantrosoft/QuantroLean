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

using QuantConnect.Data;
using QuantConnect.Indicators;

namespace QuantConnect.Algorithm.CSharp
{
    /// <summary>
    /// Example algorithm on free Dukascopy data: a simple moving average crossover on the EUR_USD CFD, minute quote
    /// bars. Download the data first with MyDownloader (see README, "Dukascopy"):
    ///   MyDownloader --tickers EUR_USD --data-downloader DukascopyDataDownloader --data-type Quote
    ///                --security-type Cfd --market dukascopy --resolution Tick --start-date 20240102 --end-date 20240103
    /// then run this algorithm (--algorithm-type-name DukascopyExampleAlgorithm). The averages' periods are the
    /// parameters "fast-period" and "slow-period".
    /// </summary>
    public class DukascopyExampleAlgorithm : QCAlgorithm
    {
        private Symbol _symbol;
        private SimpleMovingAverage _fast;
        private SimpleMovingAverage _slow;

        /// <summary>
        /// Sets the dates, cash, the EUR_USD Dukascopy CFD and the two averages.
        /// </summary>
        public override void Initialize()
        {
            SetStartDate(2024, 1, 2);
            SetEndDate(2024, 1, 3);
            SetAccountCurrency("USD");
            SetCash(100000);

            _symbol = AddCfd("EUR_USD", Resolution.Minute, Market.Dukascopy).Symbol;
            _fast = SMA(_symbol, GetParameter("fast-period", 20), Resolution.Minute);
            _slow = SMA(_symbol, GetParameter("slow-period", 60), Resolution.Minute);
        }

        /// <summary>
        /// Trades the crossover once both averages are ready: long when the fast average is above the slow one,
        /// flat when it is below.
        /// </summary>
        /// <param name="slice">The data of the current time step</param>
        public override void OnData(Slice slice)
        {
            if (!_slow.IsReady || !slice.ContainsKey(_symbol))
            {
                return;
            }

            var fastAboveSlow = _fast.Current.Value > _slow.Current.Value;
            if (!Portfolio.Invested && fastAboveSlow)
            {
                SetHoldings(_symbol, 0.5);
            }
            else if (Portfolio.Invested && !fastAboveSlow)
            {
                Liquidate(_symbol);
            }
        }
    }
}

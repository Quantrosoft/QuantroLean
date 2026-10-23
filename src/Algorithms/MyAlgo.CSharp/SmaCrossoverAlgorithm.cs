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
    /// Example algorithm: simple moving average crossover on SPY minute bars, 2013-10-07 to 2013-10-11
    /// (Lean's sample data, put into data/ by tools/SampleData). Goes long when the fast average is above the slow one
    /// and flat when it is below. The periods are the parameters "fast-period" and "slow-period"
    /// (config.json "parameters", or set per backtest by MyOptimizer).
    /// </summary>
    public class SmaCrossoverAlgorithm : QCAlgorithm
    {
        private Symbol _symbol;
        private SimpleMovingAverage _fast;
        private SimpleMovingAverage _slow;

        /// <summary>
        /// Sets dates, cash, the security and the two averages.
        /// </summary>
        public override void Initialize()
        {
            SetStartDate(2013, 10, 7);
            SetEndDate(2013, 10, 11);
            SetCash(100000);

            _symbol = AddEquity("SPY", Resolution.Minute).Symbol;
            _fast = SMA(_symbol, GetParameter("fast-period", 10), Resolution.Minute);
            _slow = SMA(_symbol, GetParameter("slow-period", 30), Resolution.Minute);
        }

        /// <summary>
        /// Trades the crossover once both averages are ready.
        /// </summary>
        /// <param name="slice">The data of the current time step</param>
        public override void OnData(Slice slice)
        {
            if (!_slow.IsReady)
            {
                return;
            }

            var fastAboveSlow = _fast.Current.Value > _slow.Current.Value;
            if (!Portfolio.Invested && fastAboveSlow)
            {
                SetHoldings(_symbol, 1);
            }
            else if (Portfolio.Invested && !fastAboveSlow)
            {
                Liquidate(_symbol);
            }
        }
    }
}

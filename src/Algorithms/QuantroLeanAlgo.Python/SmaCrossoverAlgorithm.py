# MIT License
# Copyright (c) 2025 Quantrosoft Pte. Ltd.
#
# Permission is hereby granted, free of charge, to any person obtaining a copy
# of this software and associated documentation files (the "Software"), to deal
# in the Software without restriction, including without limitation the rights
# to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
# copies of the Software, and to permit persons to whom the Software is
# furnished to do so, subject to the following conditions:
#
# The above copyright notice and this permission notice shall be included in all
# copies or substantial portions of the Software.
#
# THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
# IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
# FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
# AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
# LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
# OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
# SOFTWARE.

from AlgorithmImports import *


class SmaCrossoverAlgorithm(QCAlgorithm):
    '''Example algorithm, same logic as SmaCrossoverAlgorithm.cs: simple moving average crossover on SPY minute
    bars, 2013-10-07 to 2013-10-11 (Lean's sample data, put into data/ by tools/SampleData). Goes long when the fast average is
    above the slow one and flat when it is below. The periods are the parameters "fast-period" and "slow-period"
    (config.json "parameters", or set per backtest by QuantroLeanOptimizer).'''

    def initialize(self):
        self.set_start_date(2013, 10, 7)
        self.set_end_date(2013, 10, 11)
        self.set_cash(100000)

        self._symbol = self.add_equity("SPY", Resolution.MINUTE).symbol
        self._fast = self.sma(self._symbol, self.get_parameter("fast-period", 10), Resolution.MINUTE)
        self._slow = self.sma(self._symbol, self.get_parameter("slow-period", 30), Resolution.MINUTE)

    def on_data(self, slice):
        if not self._slow.is_ready:
            return

        fast_above_slow = self._fast.current.value > self._slow.current.value
        if not self.portfolio.invested and fast_above_slow:
            self.set_holdings(self._symbol, 1)
        elif self.portfolio.invested and not fast_above_slow:
            self.liquidate(self._symbol)

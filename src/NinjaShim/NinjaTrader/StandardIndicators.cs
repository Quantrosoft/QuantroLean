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
using NinjaTrader.Data;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.NinjaScript.Indicators
{

   public class VWAP         : Indicator { public VWAP() { Values[0] = new Series<double>(); } }
   public class OrderFlowCumulativeDelta : Indicator
   {
      public Series<double> DeltaClose { get; } = new Series<double>();
      public Series<double> DeltaOpen  { get; } = new Series<double>();
      public Series<double> DeltaHigh  { get; } = new Series<double>();
      public Series<double> DeltaLow   { get; } = new Series<double>();
      public OrderFlowCumulativeDelta() { Values[0] = DeltaClose; }
   }
   public class CrossAbove   : Indicator { public CrossAbove() { Values[0] = new Series<double>(); } }
   public class CrossBelow   : Indicator { public CrossBelow() { Values[0] = new Series<double>(); } }
   public class Rising       : Indicator { public Rising() { Values[0] = new Series<double>(); } }
   public class Falling      : Indicator { public Falling() { Values[0] = new Series<double>(); } }

}

namespace NinjaTrader.NinjaScript
{
   public partial class NinjaScriptBase
   {
      // ---- Standard-indicator cache + hosted registration ----------
      // NT8 caches indicators per (params, input) so repeated `RSI(macdLine,
      // 14,3)` calls inside OnBarUpdate return the SAME long-lived instance
      // (preserving recursive state). Mirror that + register for per-bar driving.
      private readonly System.Collections.Generic.List<NinjaTrader.NinjaScript.Indicators.Indicator> _stdCache
         = new System.Collections.Generic.List<NinjaTrader.NinjaScript.Indicators.Indicator>();
      private T StdCache<T>(ISeries<double> input, System.Func<T, bool> match, System.Func<T> create)
         where T : NinjaTrader.NinjaScript.Indicators.Indicator
      {
         var inp = input ?? Input;
         foreach (var c in _stdCache)
            if (c is T t && object.ReferenceEquals(t.Input, inp) && match(t)) return t;
         var ni = create();
         ni.Input = inp;
         _stdCache.Add(ni);
         _runtime?.RegisterHostedIndicator(ni, inp, this);
         return ni;
      }

      public NinjaTrader.NinjaScript.Indicators.SMA SMA(int period) => SMA(Input, period);
      public NinjaTrader.NinjaScript.Indicators.SMA SMA(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.SMA { Period = period });
      public NinjaTrader.NinjaScript.Indicators.EMA EMA(int period) => EMA(Input, period);
      public NinjaTrader.NinjaScript.Indicators.EMA EMA(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.EMA { Period = period });
      public NinjaTrader.NinjaScript.Indicators.WMA WMA(int period) => new NinjaTrader.NinjaScript.Indicators.WMA();
      public NinjaTrader.NinjaScript.Indicators.WMA WMA(ISeries<double> input, int period) => new NinjaTrader.NinjaScript.Indicators.WMA();
      public NinjaTrader.NinjaScript.Indicators.HMA HMA(int period) => new NinjaTrader.NinjaScript.Indicators.HMA();
      public NinjaTrader.NinjaScript.Indicators.TEMA TEMA(int period) => new NinjaTrader.NinjaScript.Indicators.TEMA();
      public NinjaTrader.NinjaScript.Indicators.DEMA DEMA(int period) => new NinjaTrader.NinjaScript.Indicators.DEMA();
      public NinjaTrader.NinjaScript.Indicators.RSI RSI(int period, int smooth) => RSI(Input, period, smooth);
      public NinjaTrader.NinjaScript.Indicators.RSI RSI(ISeries<double> input, int period, int smooth)
         => StdCache(input, t => t.Period == period && t.Smooth == smooth,
                     () => new NinjaTrader.NinjaScript.Indicators.RSI { Period = period, Smooth = smooth });
      public NinjaTrader.NinjaScript.Indicators.MACD MACD(int fast, int slow, int smooth) => new NinjaTrader.NinjaScript.Indicators.MACD();
      public NinjaTrader.NinjaScript.Indicators.MACD MACD(ISeries<double> input, int fast, int slow, int smooth) => new NinjaTrader.NinjaScript.Indicators.MACD();
      public NinjaTrader.NinjaScript.Indicators.ATR ATR(int period) => ATR(Input, period);
      public NinjaTrader.NinjaScript.Indicators.ATR ATR(NinjaTrader.Data.Bars bars, int period)
         => ATR(bars != null ? bars.Close : Input, period);
      public NinjaTrader.NinjaScript.Indicators.ATR ATR(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.ATR { Period = period });
      public NinjaTrader.NinjaScript.Indicators.ADX ADX(int period) => new NinjaTrader.NinjaScript.Indicators.ADX();
      public NinjaTrader.NinjaScript.Indicators.ADX ADX(NinjaTrader.Data.Bars bars, int period)
         => ADX(bars != null ? bars.Close : Input, period);
      public NinjaTrader.NinjaScript.Indicators.ADX ADX(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.ADX { Period = period });
      public NinjaTrader.NinjaScript.Indicators.Swing Swing(NinjaTrader.Data.Bars bars, int strength)
         => Swing(bars != null ? bars.Close : Input, strength);
      public NinjaTrader.NinjaScript.Indicators.Swing Swing(ISeries<double> input, int strength)
         => StdCache(input, t => t.Strength == strength, () => new NinjaTrader.NinjaScript.Indicators.Swing { Strength = strength });
      public NinjaTrader.NinjaScript.Indicators.CCI CCI(int period) => new NinjaTrader.NinjaScript.Indicators.CCI();
      public NinjaTrader.NinjaScript.Indicators.MFI MFI(int period) => new NinjaTrader.NinjaScript.Indicators.MFI();
      public NinjaTrader.NinjaScript.Indicators.OBV OBV() => new NinjaTrader.NinjaScript.Indicators.OBV();
      public NinjaTrader.NinjaScript.Indicators.StdDev StdDev(int period) => new NinjaTrader.NinjaScript.Indicators.StdDev();
      public NinjaTrader.NinjaScript.Indicators.StdDev StdDev(ISeries<double> input, int period) => new NinjaTrader.NinjaScript.Indicators.StdDev();
      public NinjaTrader.NinjaScript.Indicators.Bollinger Bollinger(double stdDev, int period) => new NinjaTrader.NinjaScript.Indicators.Bollinger();
      public NinjaTrader.NinjaScript.Indicators.Bollinger Bollinger(ISeries<double> input, double stdDev, int period) => new NinjaTrader.NinjaScript.Indicators.Bollinger();
      public NinjaTrader.NinjaScript.Indicators.Stochastics Stochastics(int periodD, int periodK, int smooth) => new NinjaTrader.NinjaScript.Indicators.Stochastics();
      public NinjaTrader.NinjaScript.Indicators.StochasticsFast StochasticsFast(int periodD, int periodK) => new NinjaTrader.NinjaScript.Indicators.StochasticsFast();
      public NinjaTrader.NinjaScript.Indicators.VOL VOL() => new NinjaTrader.NinjaScript.Indicators.VOL();
      public NinjaTrader.NinjaScript.Indicators.VOL VOL(ISeries<double> input) => new NinjaTrader.NinjaScript.Indicators.VOL();
      public NinjaTrader.NinjaScript.Indicators.VWAP VWAP() => new NinjaTrader.NinjaScript.Indicators.VWAP();
      public NinjaTrader.NinjaScript.Indicators.MIN MIN(int period) => MIN(Input, period);
      public NinjaTrader.NinjaScript.Indicators.MIN MIN(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.MIN { Period = period });
      public NinjaTrader.NinjaScript.Indicators.MAX MAX(int period) => MAX(Input, period);
      public NinjaTrader.NinjaScript.Indicators.MAX MAX(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.MAX { Period = period });
      public NinjaTrader.NinjaScript.Indicators.SUM SUM(int period) => new NinjaTrader.NinjaScript.Indicators.SUM();
      public NinjaTrader.NinjaScript.Indicators.SUM SUM(ISeries<double> input, int period) => new NinjaTrader.NinjaScript.Indicators.SUM();
      public NinjaTrader.NinjaScript.Indicators.Range Range() => new NinjaTrader.NinjaScript.Indicators.Range();
      public NinjaTrader.NinjaScript.Indicators.Momentum Momentum(int period) => new NinjaTrader.NinjaScript.Indicators.Momentum();
      public NinjaTrader.NinjaScript.Indicators.TSI TSI(int fast, int slow) => new NinjaTrader.NinjaScript.Indicators.TSI();
      public NinjaTrader.NinjaScript.Indicators.Swing Swing(int strength) => new NinjaTrader.NinjaScript.Indicators.Swing();

      public NinjaTrader.NinjaScript.Indicators.DM DM(int period) => DM(Input, period);
      public NinjaTrader.NinjaScript.Indicators.DM DM(NinjaTrader.Data.Bars bars, int period)
         => DM(bars != null ? bars.Close : Input, period);
      public NinjaTrader.NinjaScript.Indicators.DM DM(ISeries<double> input, int period)
         => StdCache(input, t => t.Period == period, () => new NinjaTrader.NinjaScript.Indicators.DM { Period = period });
      public NinjaTrader.NinjaScript.Indicators.VolumeUpDown VolumeUpDown() => VolumeUpDown(Input);
      public NinjaTrader.NinjaScript.Indicators.VolumeUpDown VolumeUpDown(ISeries<double> input)
         => StdCache(input, t => true, () => new NinjaTrader.NinjaScript.Indicators.VolumeUpDown());

      public bool CrossAbove(ISeries<double> series1, ISeries<double> series2, int lookBack) => false;
      public bool CrossAbove(ISeries<double> series1, double value, int lookBack)            => false;
      public bool CrossBelow(ISeries<double> series1, ISeries<double> series2, int lookBack) => false;
      public bool CrossBelow(ISeries<double> series1, double value, int lookBack)            => false;
      public bool Rising (ISeries<double> series) => false;
      public bool Falling(ISeries<double> series) => false;
   }
}

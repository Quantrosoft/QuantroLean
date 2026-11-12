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
using NinjaTrader.NinjaScript.Indicators;   // Series<T>

namespace QuantroLean.NinjaShim
{
   public sealed class BarStream
   {
      private readonly TimeSpan _period;
      private readonly Bars     _bars;
      private DateTime _currentBarTs = DateTime.MinValue;
      private DateTime _lastTickTs;
      private double   _o, _h, _l, _c;
      private long     _vol, _askVol, _bidVol;
      private bool     _hasOpenBar;

      public bool EmulateFormingBar { get; set; }
      private bool _formingSlotOpen;

      private readonly int _ticksPerBar;
      private int _ticksInBar;

      private readonly double _tickSize;

      private readonly NinjaTrader.NinjaScript.BarsType _hostedType;
      private readonly Bars _shadow;
      private int _mirrored;          // count of FINISHED shadow bars already mirrored
      private bool _hostedFormingOpen; // [FORMING BAR] transient slot of the forming bar
      private int _hostedTickCount;   // data points in the forming shadow bar

      private DateTime _lastSessionEnd;
      private int      _sessionStartIdx = -1; // shadow index of the first bar of the running session
      private int      _sessionFlags; // only for the trace of the first advances
      public int SessionAdvances { get; private set; }
      public int SessionStartIndex => _sessionStartIdx;
      /// <summary>Output channel for the session-advance trace (the host sets it).</summary>
      public Action<string> SessionTrace { get; set; }

      public int Count { get; private set; }    // count of bars in the mirrored series - INCLUDING a mirrored forming slot
      public int ClosedCount => Count - ((_hostedFormingOpen || _formingSlotOpen) ? 1 : 0);

      // Exposed for ShimRuntime so it can round Now → bar-start UTC when needed.
      public long PeriodTicks => _period.Ticks;

      public BarStream(Bars bars, TimeSpan period)
      {
         _bars = bars;
         _period = period;
      }

      public BarStream(Bars bars, int ticksPerBar)
      {
         if (ticksPerBar <= 0) throw new ArgumentOutOfRangeException(nameof(ticksPerBar));
         _bars = bars;
         _period = TimeSpan.Zero;
         _ticksPerBar = ticksPerBar;
      }

      public BarStream(Bars bars, NinjaTrader.NinjaScript.BarsType hostedType, Bars shadow)
      {
         _bars       = bars   ?? throw new ArgumentNullException(nameof(bars));
         _hostedType = hostedType ?? throw new ArgumentNullException(nameof(hostedType));
         _shadow     = shadow ?? throw new ArgumentNullException(nameof(shadow));
         _period     = TimeSpan.Zero;
         _tickSize   = shadow.Instrument?.MasterInstrument?.TickSize ?? 0.0;
         _shadow.OnBeforeRemoveLastBar = idx =>
         {
            if (idx < _mirrored)
               throw new InvalidOperationException(
                  $"[BARSTYPE] {_hostedType.GetType().Name}.RemoveLastBar wants to withdraw "
                  + $"bar {idx}, which is already mirrored as finished (mirrored: "
                  + $"{_mirrored}). The mirroring rule 'finished = one more bar behind it' "
                  + "does not hold for this bars type. No fallback.");
         };
      }

      /// <summary>
      /// Push one tick. Returns true if this tick closed at least one bar.
      /// Thin wrapper over the callback form (used by secondary/MTF streams,
      /// which are time-based and close at most one bar per tick).
      /// </summary>
      public bool OnTick(DateTime ts, double price, double bid, double ask, long volume)
      {
         bool closed = false;
         OnTick(ts, price, bid, ask, volume, () => closed = true);
         return closed;
      }

      /// <summary>
      /// Push one tick, invoking <paramref name="onBarClosed"/> once for EACH
      /// bar this tick closes (a hosted bars type can finish several).
      /// The just-closed bar is the last element appended to the Bars series
      /// when the callback fires; the new forming bar is held in _o.._c and is
      /// not yet in the series.
      /// </summary>
      public void OnTick(DateTime ts, double price, double bid, double ask, long volume, Action onBarClosed)
      {
         _lastTickTs = ts;

         if (_hostedType != null)
         {
            if (price <= 0) return;   // quote-only ticks carry no trade price
            _shadow.TickCount = _hostedTickCount;
            int before = _shadow.Count;
            // NT8 feeds a last tick as open=high=low=close=price
            // (isBar=false: a data point, not a finished foreign bar).
            _hostedType.DispatchDataPoint(_shadow, price, price, price, price,
                                          ts, volume, false, bid, ask);
            _hostedTickCount = _shadow.Count != before ? 1 : _hostedTickCount + 1;

            var se = _hostedType.SessionIterator?.ActualSessionEnd ?? default;
            if (se != _lastSessionEnd)
            {
               if (_lastSessionEnd != default)
               {
                  SessionAdvances++;
                  // The first advances in detail: only that way is it traceable
                  // WHETHER the bars type could apply the reset at all.
                  if (SessionAdvances <= 3)
                     SessionTrace?.Invoke(
                        $"[BARSTYPE][SESSION] advance #{SessionAdvances} at {ts:u} — "
                        + $"shadow bars {_shadow.Count}, TickCount {_shadow.TickCount}, "
                        + $"IsResetOnNewTradingDay={_shadow.IsResetOnNewTradingDay}, "
                        + $"new boundary {se:u} (bar added: {_shadow.Count != before})");
               }
               _sessionStartIdx = _shadow.Count - 1;
               _lastSessionEnd = se;
            }

            if (_hostedFormingOpen)
            {
               DropLastMirroredSlot();
               _hostedFormingOpen = false;
               _bars.FormingSlotMirrored = false;   // [FORMING-BLIND]
            }

            while (_mirrored < _shadow.Count - 1)
            {
               int idxMirrored = _mirrored;
               MirrorShadowBar(_mirrored);
               _mirrored++;
               bool firstOfSession = idxMirrored == _sessionStartIdx;
               if (firstOfSession && ++_sessionFlags <= 6)
                  SessionTrace?.Invoke(
                     $"[BARSTYPE][SESSION] first bar of session #{_sessionFlags}: "
                     + $"{_bars.GetTime(_mirrored - 1):u}  O={_bars.GetOpen(_mirrored - 1)} "
                     + $"(boundary {_lastSessionEnd:u})");
               _bars.IsFirstBarOfSession = firstOfSession;
               onBarClosed();
               _bars.IsFirstBarOfSession = false;
            }

            if (EmulateFormingBar && _shadow.Count > _mirrored)
            {
               MirrorShadowBar(_shadow.Count - 1);
               _hostedFormingOpen = true;
               _bars.FormingSlotMirrored = true;   // [FORMING-BLIND]
            }
            return;
         }

         if (_ticksPerBar > 0)
         {
            if (price <= 0) return;   // quote-only ticks don't count
            if (!_hasOpenBar)
            {
               StartBar(ts, price);
               _hasOpenBar = true;
            }
            if (_o <= 0)               _o = price;
            if (_h <= 0 || price > _h) _h = price;
            if (_l <= 0 || price < _l) _l = price;
            _c = price;
            _vol += volume;
            Classify(price, bid, ask, volume);
            if (++_ticksInBar >= _ticksPerBar)
            {
               CloseBarAndAppend();
               _hasOpenBar = false;
               _ticksInBar = 0;
               onBarClosed();
            }
            return;
         }

         // ---- Time mode -------------------------------------------------
         long periodTicks = _period.Ticks;
         var barTs = new DateTime((ts.Ticks / periodTicks) * periodTicks, DateTimeKind.Utc);

         if (!_hasOpenBar)
         {
            // First tick — open the very first bar
            StartBar(barTs, price);
            _hasOpenBar = true;
            if (EmulateFormingBar) OpenFormingSlot(price, barTs);
         }
         else if (barTs != _currentBarTs)
         {
            // Bar boundary crossed — close the previous bar, fire, start new
            if (_formingSlotOpen) FinalizeFormingSlot();   // slot 0 becomes the finished bar
            else                  CloseBarAndAppend();
            // Same order as in NT8/OnEachTick: first the forming bar exists on
            // index 0, THEN OnBarUpdate fires. Only that way is [1] the bar that
            // just closed. The dispatch must therefore come AFTER OpenFormingSlot.
            StartBar(barTs, price);
            if (EmulateFormingBar) OpenFormingSlot(price, barTs);
            onBarClosed();
         }

         // Update the OPEN bar's OHLC from TRADE (Last) prices only. Quote-only
         // ticks carry price<=0; including them corrupts Open/Low — a 0-seeded
         // Low never updates (price>0 is never < 0) → Low stuck at 0, range
         // garbage. Close-only signals were unaffected (the last tick is a
         // Last), but everything that reads Open/High/Low broke on it.
         if (price > 0)
         {
            if (_o <= 0)               _o = price;   // lazy open if bar started on a quote
            if (_h <= 0 || price > _h) _h = price;
            if (_l <= 0 || price < _l) _l = price;
            _c = price;
         }
         _vol += volume;
         Classify(price, bid, ask, volume);
         if (_formingSlotOpen) MirrorIntoFormingSlot();
      }

      // ---- Forming bar (only with EmulateFormingBar) ------------------------

      /// <summary>Create a new slot 0 for the bar that has just begun, with the
      /// first tick as O=H=L=C. From here on [0] is the forming bar and [1] the
      /// one that just closed — as in NT8 under Calculate.OnEachTick.</summary>
      private void OpenFormingSlot(double seedPrice, DateTime barTs)
      {
         AppendBar(seedPrice, seedPrice, seedPrice, seedPrice, 0,
                   _period > TimeSpan.Zero ? barTs + _period : barTs);
         _formingSlotOpen = true;
         _bars.FormingSlotMirrored = true;   // [FORMING-BLIND]
      }

      /// <summary>Overwrite the open slot 0 with the final OHLC/volume of the
      /// bar that has now closed. No append — the slot already exists, otherwise
      /// the bar would stand in the series twice.</summary>
      private void FinalizeFormingSlot()
      {
         MirrorIntoFormingSlot();
         DateTime closeTs = _period > TimeSpan.Zero ? _currentBarTs + _period : _lastTickTs;
         ((Series<DateTime>)_bars.Time)[0] = closeTs;
         _formingSlotOpen = false;
         _bars.FormingSlotMirrored = false;   // [FORMING-BLIND] the slot is now a finished bar
      }

      /// <summary>Mirror the running state of the open bar into slot 0.</summary>
      private void MirrorIntoFormingSlot()
      {
         ((Series<double>)_bars.Open) [0] = _o;
         ((Series<double>)_bars.High) [0] = _h;
         ((Series<double>)_bars.Low)  [0] = _l;
         ((Series<double>)_bars.Close)[0] = _c;
         ((Series<double>)_bars.Volume)[0] = _vol;
      }

      private void Classify(double price, double bid, double ask, long volume)
      {
         if (ask > bid)
         {
            if (price >= ask)      _askVol += volume;
            else if (price <= bid) _bidVol += volume;
         }
      }

      private void StartBar(DateTime barTs, double seedPrice)
      {
         _currentBarTs = barTs;
         _o = _h = _l = _c = seedPrice;
         _vol = _askVol = _bidVol = 0;
      }

      private void CloseBarAndAppend()
      {
         DateTime closeTs = _period > TimeSpan.Zero ? _currentBarTs + _period : _lastTickTs;
         AppendBar(_o, _h, _l, _c, _vol, closeTs);
      }

      // Append one fully-specified closed bar to every series.
      private void AppendBar(double o, double h, double l, double c, long vol, DateTime closeTs)
      {
         ((Series<double>)_bars.Open) .Append(o);
         ((Series<double>)_bars.High) .Append(h);
         ((Series<double>)_bars.Low)  .Append(l);
         ((Series<double>)_bars.Close).Append(c);
         ((Series<double>)_bars.Volume).Append(vol);
         ((Series<DateTime>)_bars.Time).Append(closeTs);
         _bars.Count = ++Count;
      }

      private void MirrorShadowBar(int idx)
      {
         AppendBar(SnapToTickGrid(_shadow.GetOpen(idx)),  SnapToTickGrid(_shadow.GetHigh(idx)),
                   SnapToTickGrid(_shadow.GetLow(idx)),   SnapToTickGrid(_shadow.GetClose(idx)),
                   _shadow.GetVolume(idx), _shadow.GetTime(idx));
      }

      private double SnapToTickGrid(double px)
      {
         if (_tickSize <= 0) return px;
         return Math.Round(px / _tickSize, MidpointRounding.AwayFromZero) * _tickSize;
      }

      private void DropLastMirroredSlot()
      {
         if (_bars.Count <= _mirrored)
            throw new InvalidOperationException(
               $"[BARSTYPE] the forming slot is to be removed, but the series "
               + $"holds only {_bars.Count} bars with {_mirrored} finished. "
               + "No fallback.");
         ((Series<double>)_bars.Open) .RemoveLast();
         ((Series<double>)_bars.High) .RemoveLast();
         ((Series<double>)_bars.Low)  .RemoveLast();
         ((Series<double>)_bars.Close).RemoveLast();
         ((Series<double>)_bars.Volume).RemoveLast();
         ((Series<DateTime>)_bars.Time).RemoveLast();
         _bars.Count = --Count;
      }

   }
}

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

// Shim — NinjaTrader.Data namespace.
// Bars (OHLCV time-series for one subscribed instrument/period), BarsPeriod,
// MarketDataEventArgs, MarketDepthEventArgs, ConnectionStatusEventArgs.
//
// Bars is the per-(instrument, period) container. NinjaScriptBase.BarsArray
// is a Bars[] indexed by AddDataSeries-order (BarsArray[0] = primary instrument).

using System;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;

namespace NinjaTrader.Data
{
   public enum BarsPeriodType
   {
      Tick, Volume, Range, Second, Minute, Hour, Day, Week, Month, Year,
      HeikenAshi, Renko, LineBreak, PointAndFigure, Kagi, Custom,
   }

   public enum MarketDataType { Last, Bid, Ask, DailyHigh, DailyLow, DailyVolume, Unknown }

   public partial class BarsPeriod
   {
      public BarsPeriodType BarsPeriodType { get; set; }
      public string BarsPeriodTypeName { get; set; }
      public int Value { get; set; }
      public int Value2 { get; set; }
      public string MarketDataTypeName { get; set; }
      public int BaseBarsPeriodValue { get; set; }
      public BarsPeriodType BaseBarsPeriodType { get; set; } = BarsPeriodType.Minute;
      public MarketDataType MarketDataType { get; set; } = MarketDataType.Last;
   }

   public partial class Bars : NinjaTrader.NinjaScript.ISeries<double>
   {
      double NinjaTrader.NinjaScript.ISeries<double>.this[int barsAgo] => Close[barsAgo];
      bool NinjaTrader.NinjaScript.ISeries<double>.IsValidDataPoint(int barsAgo)
         => Close.IsValidDataPoint(barsAgo);

      public NinjaTrader.Cbi.Instrument Instrument { get; set; }
      public BarsPeriod BarsPeriod { get; set; }
      public int Count { get; internal set; }
      public int CurrentBar => Count - 1;

      // OHLCV + Time series. The runtime appends to these on each closed bar.
      public Series<double> Open   { get; } = new Series<double>();
      public Series<double> High   { get; } = new Series<double>();
      public Series<double> Low    { get; } = new Series<double>();
      public Series<double> Close  { get; } = new Series<double>();
      public Series<double> Volume { get; } = new Series<double>();
      public Series<DateTime> Time { get; } = new Series<DateTime>();

      internal bool FormingSlotMirrored;
      internal void SetSeriesAnchors(int absIndex)
      {
         Open.SetAnchor(absIndex); High.SetAnchor(absIndex);
         Low.SetAnchor(absIndex);  Close.SetAnchor(absIndex);
         Volume.SetAnchor(absIndex); Time.SetAnchor(absIndex);
      }
      internal void ClearSeriesAnchors()
      {
         Open.ClearAnchor(); High.ClearAnchor(); Low.ClearAnchor();
         Close.ClearAnchor(); Volume.ClearAnchor(); Time.ClearAnchor();
      }

      public double GetOpen(int barIndex)   => Open.GetValueAt(barIndex);
      public double GetHigh(int barIndex)   => High.GetValueAt(barIndex);
      public double GetLow(int barIndex)    => Low.GetValueAt(barIndex);
      public double GetClose(int barIndex)  => Close.GetValueAt(barIndex);
      public long   GetVolume(int barIndex) => (long)Volume.GetValueAt(barIndex);
      public DateTime GetTime(int barIndex) => Time.GetValueAt(barIndex);

      public double   LastPrice   { get; set; }
      public DateTime LastBarTime { get; set; }
      public int      TickCount   { get; set; }
      // Trading hours of the series — source of the session boundaries for SessionIterator.
      // [NO-UTC-DEFAULT] The default object has NO zone (it had UTC, minute 0): ShimRuntime sets the real
      // hours on every series it attaches (NewSessionTradingHours); a series nobody attached stops at its
      // first session boundary instead of cutting at 00:00 UTC.
      public TradingHours TradingHours { get; set; } = new TradingHours();
      // Called BEFORE every RemoveLastBar with the absolute index of the bar to be
      // removed. The shim hooks in here to abort loudly if a bar type wants to
      // retract a bar that has already been mirrored as finished.
      public Action<int> OnBeforeRemoveLastBar { get; set; }

      public bool IsResetOnNewTradingDay { get; set; }
      public bool IsFirstBarOfSession { get; set; }
      public bool IsTickReplay { get; set; }

      public DateTime FromDate { get; set; }
      public DateTime ToDate   { get; set; }
      public string SessionIterator_ToString() => "";

      public BarsType BarsType { get; set; } = new BarsType();
   }

   public class BarsType
   {
      public string Name { get; set; } = "Shim";
      public BarsPeriod BarsPeriod { get; set; } = new BarsPeriod();
      public void SetState(NinjaTrader.NinjaScript.State state) { }
      public bool IsRemoveLastBarSupported { get; set; } = true;
   }

   public class MarketDataEventArgs : EventArgs
   {
      public MarketDataType MarketDataType { get; set; }
      public double Price { get; set; }
      public long Volume { get; set; }
      public DateTime Time { get; set; }
      public double Bid { get; set; }
      public double Ask { get; set; }
      public double Last { get; set; }
      public NinjaTrader.Cbi.Instrument Instrument { get; set; }
      public bool IsReset { get; set; }
   }

   public class MarketDepthEventArgs : EventArgs
   {
      public MarketDataType MarketDataType { get; set; }
      public double Price { get; set; }
      public long Volume { get; set; }
      public int Position { get; set; }
      public Operation Operation { get; set; }
      public DateTime Time { get; set; }
   }

   public enum Operation { Add, Remove, Update }

   public class ConnectionStatusEventArgs : EventArgs
   {
      public ConnectionStatus Status { get; set; }
      public ConnectionStatus PriceStatus { get; set; }
      public ConnectionStatus OrderStatus { get; set; }
   }

   public enum ConnectionStatus { Disconnected, Connecting, Connected, ConnectionLost }

   public class ConnectionInfo
   {
      public PlaybackConnectionInfo PlaybackConnection { get; set; }   // null when not in playback mode
      public ConnectionStatus Status { get; set; } = ConnectionStatus.Connected;
      public ConnectionStatus PriceStatus { get; set; } = ConnectionStatus.Connected;
      public ConnectionStatus OrderStatus { get; set; } = ConnectionStatus.Connected;
   }

   public class PlaybackConnectionInfo
   {
      public ConnectionStatus Status { get; set; } = ConnectionStatus.Disconnected;
   }
}

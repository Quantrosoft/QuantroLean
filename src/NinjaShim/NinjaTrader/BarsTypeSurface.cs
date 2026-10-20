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
using System.Collections.ObjectModel;

namespace NinjaTrader.Core
{
   public static partial class Globals
   {
      public static readonly DateTime MinDate = new DateTime(1800, 1, 1);

      public static long FromCryptocurrencyVolume(double volume)
         => throw new NotSupportedException(
               "[BARSTYPE] Core.Globals.FromCryptocurrencyVolume: NinjaTrader's conversion of a "
               + "cryptocurrency volume is not modelled by the shim. A volume bar series on an "
               + "instrument of the type CryptoCurrency cannot be built. No fallback.");
   }
}

namespace NinjaTrader.Data
{
   public enum ReversalType { Percent = 0, Tick = 1 }

   public enum PointAndFigurePriceType { Close = 0, HighsAndLows = 1 }

   public class Session
   {
      public DayOfWeek BeginDay   { get; set; }
      public int       BeginTime  { get; set; }
      public DayOfWeek EndDay     { get; set; }
      public int       EndTime    { get; set; }
      public DayOfWeek TradingDay { get; set; }
   }

   public partial class TradingHours
   {
      private Collection<Session> _sessions;
      private int _sessionsBuiltFor = -1;

      public Collection<Session> Sessions
      {
         get
         {
            // [NO-UTC-DEFAULT] the times below are local times of SessionEndTimeZone; without a zone they
            // would mean nothing (until then the zone defaulted to UTC).
            if (SessionEndTimeZone == null)
               throw new InvalidOperationException(
                  "[SHIM] TradingHours.Sessions: no SessionEndTimeZone - the series was not attached by "
                + "ShimRuntime (NewSessionTradingHours). No fallback to UTC.");
            if (_sessions == null || _sessionsBuiltFor != SessionEndMinuteOfDay)
            {
               int hhmm = SessionEndMinuteOfDay / 60 * 100 + SessionEndMinuteOfDay % 60;
               var list = new Collection<Session>();
               for (int d = 0; d < 7; d++)
                  list.Add(new Session
                  {
                     BeginDay = (DayOfWeek)d, BeginTime = hhmm,
                     EndDay = (DayOfWeek)((d + 1) % 7), EndTime = hhmm,
                     TradingDay = (DayOfWeek)((d + 1) % 7),
                  });
               _sessions = list;
               _sessionsBuiltFor = SessionEndMinuteOfDay;
            }
            return _sessions;
         }
      }
   }

   public partial class SessionIterator
   {
      public DateTime ActualTradingDayEndLocal
         => throw new NotSupportedException(
               "[BARSTYPE] SessionIterator.ActualTradingDayEndLocal is not modelled by the shim. No fallback.");

      /// <summary>See ActualTradingDayEndLocal.</summary>
      public DateTime GetTradingDayBeginLocal(DateTime tradingDayExchange)
         => throw new NotSupportedException(
               "[BARSTYPE] SessionIterator.GetTradingDayBeginLocal is not modelled by the shim. No fallback.");
   }

   public partial class BarsPeriod
   {
      public ReversalType            ReversalType            { get; set; }
      public PointAndFigurePriceType PointAndFigurePriceType { get; set; }
   }

   public partial class Bars
   {
      public int DayCount
      {
         get => throw new NotSupportedException(
                   "[BARSTYPE] Bars.DayCount: NinjaTrader maintains the day count of the forming bar "
                   + "inside its core; the rule is not modelled by the shim. A bar series built on days "
                   + "(Day, or a base period of Day) cannot be hosted. No fallback.");
         set => throw new NotSupportedException(
                   "[BARSTYPE] Bars.DayCount is not modelled by the shim. No fallback.");
      }
   }
}

namespace NinjaTrader.NinjaScript
{
   public abstract partial class BarsType
   {
      // How much history NinjaTrader loads for a chart of the type. The bar
      // types assign them in OnStateChange; headless nothing reads them.
      public int WeeksToLoad  { get; set; }
      public int MonthsToLoad { get; set; }
      public int YearsToLoad  { get; set; }

      public virtual object Clone() => MemberwiseClone();

      protected static BarsType BarsTypeDay    => Stock("DayBarsType");
      protected static BarsType BarsTypeMinute => Stock("MinuteBarsType");
      protected static BarsType BarsTypeMonth  => Stock("MonthBarsType");
      protected static BarsType BarsTypeSecond => Stock("SecondBarsType");
      protected static BarsType BarsTypeTick   => Stock("TickBarsType");
      protected static BarsType BarsTypeYear   => Stock("YearBarsType");

      private static BarsType Stock(string className)
      {
         var t = typeof(BarsType).Assembly.GetType("NinjaTrader.NinjaScript.BarsTypes." + className, false);
         if (t == null)
            throw new InvalidOperationException(
               "[BARSTYPE] " + className + " is not part of this build: its NinjaTrader source file "
               + "(bin\\Custom\\BarsTypes) is not linked. No fallback.");
         return (BarsType)Activator.CreateInstance(t);
      }
   }
}

namespace NinjaTrader.NinjaScript.BarsTypes
{
   internal static class Resource
   {
      public const string GuiDaily   = "Daily";
      public const string GuiDay     = "Day";
      public const string GuiMonthly = "Monthly";
      public const string GuiMonth   = "Month";
      public const string GuiSecond  = "Second";
      public const string GuiSeconds = "Seconds";
      public const string GuiWeekly  = "Weekly";
      public const string GuiWeeks   = "Weeks";
      public const string GuiYearly  = "Yearly";
      public const string GuiYears   = "Years";
   }
}

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

// Shim — additional NinjaTrader.Data helpers used by NT8 source code.
// SessionIterator, CommonTimeZones, and TradingHours.

using System;

namespace NinjaTrader.Data
{
   public partial class SessionIterator
   {
      public Bars Bars { get; set; }
      public DateTime ActualSessionBegin { get; set; }
      public DateTime ActualSessionEnd   { get; set; }
      public DateTime ActualTradingDayExchange { get; set; }

      public SessionIterator() { }
      public SessionIterator(Bars bars) { Bars = bars; GetNextSession(DateTime.MinValue, false); }

      // [NO-UTC-DEFAULT] was `Bars?.TradingHours ?? _fallback` with a static `new TradingHours()`
      // (zone UTC, minute 0) - a series without hours now stops at its first session boundary.
      private TradingHours Th => Bars?.TradingHours ?? throw new InvalidOperationException(
         "[SHIM] SessionIterator: no Bars / Bars.TradingHours - the session boundary needs the exchange's local "
       + "zone and session end (ShimRuntime.NewSessionTradingHours). No fallback to UTC.");

      // The exchange's local zone of the series; the session end is LOCAL time, its UTC instant moves with
      // the zone's daylight-saving changes (BoundaryUtcFor converts per day).
      private TimeZoneInfo Zone => Th.SessionEndTimeZone ?? throw new InvalidOperationException(
         "[SHIM] SessionIterator: Bars.TradingHours has no SessionEndTimeZone (the exchange's local zone, e.g. "
       + "\"Eastern Standard Time\" for CME) - the series was not attached by ShimRuntime. No fallback to UTC.");

      /// <summary>true as soon as <paramref name="time"/> lies past the end of
      /// the currently held session. The caller acknowledges that with
      /// GetNextSession — exactly the order the NT8 bar types run.</summary>
      public bool IsNewSession(DateTime time, bool includesEndTime)
         => ActualSessionEnd == default || time >= ActualSessionEnd;

      /// <summary>Sets the window to the session that contains <paramref name="time"/>:
      /// [last boundary <= time, next boundary > time).</summary>
      public void GetNextSession(DateTime time, bool includesEndTime)
      {
         if (time == DateTime.MinValue)
         {
            // Before the first data point there is no session — the first tick
            // then triggers exactly one change via IsNewSession(ActualSessionEnd
            // == default), as with NT8's first bar of a run.
            ActualSessionBegin = ActualSessionEnd = default;
            return;
         }
         ActualSessionEnd   = NextBoundaryUtc(time);
         ActualSessionBegin = PrevBoundaryUtc(time);
         ActualTradingDayExchange = ActualSessionEnd.Date;
      }

      public void CalculateTradingDay(DateTime time, bool includesEndTime)
      {
         ActualTradingDayExchange = NextBoundaryUtc(time).Date;
      }

      // ---- Boundary calculation ----------------------------------------------
      // The session boundary is a point in time LOCAL to the exchange zone (e.g.
      // 17:00 New York). Convert UTC -> local -> form the boundary -> back to UTC,
      // so that the switch to daylight saving time carries the boundary along
      // instead of shifting it.
      private DateTime BoundaryUtcFor(DateTime localDate)
      {
         var tz = Zone;
         var local = localDate.Date.AddMinutes(Th.SessionEndMinuteOfDay);
         // During the changeover gap the local time does not exist; then take
         // the next valid minute instead of throwing (the day still has a
         // boundary).
         while (tz.IsInvalidTime(local)) local = local.AddMinutes(1);
         return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), tz);
      }

      private DateTime NextBoundaryUtc(DateTime timeUtc)
      {
         var tz = Zone;
         var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc), tz);
         var b = BoundaryUtcFor(local);
         return b > timeUtc ? b : BoundaryUtcFor(local.AddDays(1));
      }

      private DateTime PrevBoundaryUtc(DateTime timeUtc)
      {
         var tz = Zone;
         var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(timeUtc, DateTimeKind.Utc), tz);
         var b = BoundaryUtcFor(local);
         return b <= timeUtc ? b : BoundaryUtcFor(local.AddDays(-1));
      }
   }

   public partial class TradingHours
   {
      public string Name { get; set; } = "Default";
      // [NO-UTC-DEFAULT] No default zone any more (it was UTC): every series ShimRuntime attaches gets it
      // (NewSessionTradingHours); one without it stops at its first session boundary (SessionIterator.Zone).
      public TimeZoneInfo SessionEndTimeZone   { get; set; }
      public int          SessionEndMinuteOfDay { get; set; }
   }

   // [NO-UTC-DEFAULT] The three named zones were all TimeZoneInfo.Utc and an unknown name fell back to UTC.
   // Now the real Windows zones (with their daylight-saving rules), and an unknown name stops.
   public static class CommonTimeZones
   {
      public static TimeZoneInfo EasternStandardTime { get; } = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
      public static TimeZoneInfo CentralStandardTime { get; } = TimeZoneInfo.FindSystemTimeZoneById("Central Standard Time");
      public static TimeZoneInfo CentralEuropeanStandardTime { get; } = TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time");
      public static TimeZoneInfo UtcTime { get; } = TimeZoneInfo.Utc;

      public static TimeZoneInfo GetTimeZoneInfo(string name)
      {
         try { return TimeZoneInfo.FindSystemTimeZoneById(name); }
         catch (Exception ex)
         {
            throw new InvalidOperationException(
               $"[SHIM] CommonTimeZones.GetTimeZoneInfo: '{name}' is no Windows time-zone id. No fallback to UTC.", ex);
         }
      }
   }
}

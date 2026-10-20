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

namespace MyLean.NinjaShim.Hosting
{
   public sealed class ShimInstrumentSpec
   {
      public string SymbolName { get; set; }

      public double TickSize { get; set; }

      public double PointValue { get; set; }

      public string SessionEndTimeZoneId { get; set; }

      public int SessionEndMinuteOfDay { get; set; } = 1020;

      /// <summary>Build the NinjaTrader.Cbi.Instrument the shim core reads
      /// TickSize/PointValue from. Hard-breaks on an incomplete spec — no
      /// fallback defaults.</summary>
      public NinjaTrader.Cbi.Instrument ToInstrument()
      {
         if (string.IsNullOrWhiteSpace(SymbolName))
            throw new InvalidOperationException(
               "[SHIM-HOSTING] ShimInstrumentSpec: SymbolName is null/empty.");
         if (TickSize <= 0)
            throw new InvalidOperationException(
               $"[SHIM-HOSTING] ShimInstrumentSpec '{SymbolName}': TickSize = {TickSize} " +
               "is invalid (must be > 0). No fallback.");
         if (PointValue <= 0)
            throw new InvalidOperationException(
               $"[SHIM-HOSTING] ShimInstrumentSpec '{SymbolName}': PointValue = {PointValue} " +
               "is invalid (must be > 0). No fallback.");

         var masterName = SymbolName.Split(' ')[0];
         return new NinjaTrader.Cbi.Instrument
         {
            FullName = SymbolName,
            MasterInstrument_Name = masterName,
            MasterInstrument = new NinjaTrader.Cbi.MasterInstrument
            {
               Name = masterName,
               TickSize = TickSize,
               PointValue = PointValue,
            },
         };
      }

      /// <summary>Resolve SessionEndTimeZoneId to a TimeZoneInfo for
      /// ShimRuntime.SessionEndTimeZone. FindSystemTimeZoneById throws on an
      /// unknown id — intentional hard break, no fallback to UTC.</summary>
      public TimeZoneInfo ResolveSessionEndTimeZone()
      {
         if (string.IsNullOrWhiteSpace(SessionEndTimeZoneId))
            throw new InvalidOperationException(
               $"[SHIM-HOSTING] ShimInstrumentSpec '{SymbolName}': SessionEndTimeZoneId " +
               "is null/empty. Set the trading session's local timezone. No fallback.");
         return TimeZoneInfo.FindSystemTimeZoneById(SessionEndTimeZoneId);
      }
   }
}

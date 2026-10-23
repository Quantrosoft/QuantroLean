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

namespace NinjaTrader.Core
{
   public static partial class Globals
   {
      public static CultureInfo GeneralOptions_CurrentCulture { get; set; } = CultureInfo.InvariantCulture;
      public static DateTime Now => DateTime.UtcNow;
      public static string UserDataDir { get; set; } =
         System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Documents", "NinjaTrader 8");
      public static string InstallDir { get; set; } = "";
      public static string TradingTimeZoneInfo_DisplayName { get; set; } = "UTC";

      // NT8 source uses `Globals.GeneralOptions.TimeZoneInfo` — namespace
      // pattern, single nested object holding general-options config.
      public static GeneralOptionsBundle GeneralOptions { get; } = new();

      public static AssemblyRegistryStub AssemblyRegistry { get; } = new();

      public static object ToLocalizedObject(object value, CultureInfo culture)
         => value?.ToString() ?? string.Empty;

      private static readonly NumberFormatInfo CurrencyNumberFormat =
         new NumberFormatInfo
         {
            NumberGroupSeparator = ".",
            NumberDecimalSeparator = ",",
            NumberGroupSizes = new[] { 3 },
            NumberDecimalDigits = 2,
            NegativeSign = "-",
         };

      private static string CurrencySymbol(Cbi.Currency currency)
      {
         switch (currency)
         {
            case Cbi.Currency.BritishPound:        return "£";   // £
            case Cbi.Currency.Euro:                return "€";   // €
            case Cbi.Currency.JapaneseYen:         return "¥";   // ¥
            case Cbi.Currency.ChinaYuan:           return "¥";   // ¥
            case Cbi.Currency.SwissFranc:          return "CHF";
            case Cbi.Currency.KoreanWon:           return "₩";   // ₩
            case Cbi.Currency.IndianRupee:         return "₹";   // ₹
            case Cbi.Currency.SwedishKrona:        return "kr";
            case Cbi.Currency.NorwayKrone:         return "kr";
            case Cbi.Currency.DenmarkKrone:        return "kr";
            case Cbi.Currency.BrazilianReal:       return "R$";
            case Cbi.Currency.SingaporeDollar:     return "S$";
            case Cbi.Currency.MalaysiaRinggit:     return "RM";
            case Cbi.Currency.ThailandBaht:        return "฿";   // ฿
            case Cbi.Currency.TaiwanNewDollar:     return "NT$";
            case Cbi.Currency.SouthAfricanRand:    return "R";
            case Cbi.Currency.CzechRepublicKoruna: return "Kč";  // Kč
            case Cbi.Currency.HungaryForint:       return "Ft";
            case Cbi.Currency.PolandZloty:         return "zł";  // zł
            case Cbi.Currency.TurkeyLira:          return "₺";   // ₺
            case Cbi.Currency.IsraeliShekel:       return "₪";   // ₪
            case Cbi.Currency.RussiaRuble:         return "руб"; // руб
            case Cbi.Currency.SaudiRiyal:          return "SR";
            default:                               return "$";
         }
      }

      public static string FormatCurrency(double value, Cbi.Currency currency)
      {
         decimal rounded;
         try
         {
            rounded = Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero);
         }
         catch (OverflowException)
         {
            return value.ToString("N2", CurrencyNumberFormat) + " " + CurrencySymbol(currency);
         }
         if (rounded == 0m) rounded = 0m;
         return rounded.ToString("N2", CurrencyNumberFormat)
              + " " + CurrencySymbol(currency);
      }
   }

   public class AssemblyRegistryStub
   {
      public Type[] GetDerivedTypes(Type baseType) => Array.Empty<Type>();
      public Type GetType(string fullName) => null;
   }

   public class GeneralOptionsBundle
   {
      public TimeZoneInfo TimeZoneInfo { get; set; } = TimeZoneInfo.Utc;
      public CultureInfo  Culture      { get; set; } = CultureInfo.InvariantCulture;
      public CultureInfo  CurrentCulture   { get; set; } = CultureInfo.InvariantCulture;
      public CultureInfo  CurrentUICulture { get; set; } = CultureInfo.InvariantCulture;
   }
}

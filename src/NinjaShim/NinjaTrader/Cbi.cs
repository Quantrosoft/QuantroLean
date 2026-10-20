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

// Shim — NinjaTrader.Cbi namespace.
// Common-business-infrastructure types: Order, Execution, Position, Account,
// Instrument, plus order-related enums (OrderState, OrderAction, OrderType,
// MarketPosition, TimeInForce, ErrorCode, EntryHandling, LogLevel).

using System;

namespace NinjaTrader.Cbi
{
   public enum OrderState { Initialized, Submitted, Accepted, Working, ChangePending,
      CancelPending, CancelSubmitted, ChangeSubmitted, Cancelled, Filled, PartFilled,
      Rejected, TriggerPending, Suspended, AcceptedByRisk, Unknown }

   public enum OrderAction { Buy, Sell, SellShort, BuyToCover }

   public enum OrderType { Market, Limit, MIT, Stop, StopLimit, StopMarket }

   public enum MarketPosition { Flat, Long, Short }

   public enum TimeInForce { Day, Gtc }

   public enum ErrorCode
   {
      NoError, LogOnFailed, OrderRejected, UnableToCancelOrder, UnableToChangeOrder,
      UnableToSubmitOrder, UserAbort, OrderRejectedByRisk, LoginExpired, Panic,
      Unknown,
   }

   public enum EntryHandling { AllEntries, UniqueEntries }

   public enum LogLevel { Information, Warning, Error, Alert }

   // ---- Order / Execution / Position --------------------------------------
   public partial class Order
   {
      public string Name { get; set; }
      public OrderState OrderState { get; set; } = OrderState.Initialized;
      public OrderAction OrderAction { get; set; }
      public OrderType OrderType { get; set; }
      public int Quantity { get; set; }
      public int Filled { get; set; }
      public double LimitPrice { get; set; }
      public double StopPrice { get; set; }
      public double AverageFillPrice { get; set; }
      public DateTime Time { get; set; }
      public string OrderId { get; set; }
      public string Oco { get; set; }
      public Instrument Instrument { get; set; }
      public Account Account { get; set; }
      public TimeInForce TimeInForce { get; set; } = TimeInForce.Gtc;
      public string FromEntrySignal { get; set; }
      public string SignalName { get; set; }

      public static bool IsTerminalState(OrderState state)
         => state == OrderState.Cancelled || state == OrderState.Filled
         || state == OrderState.Rejected  || state == OrderState.Unknown;
   }

   public partial class Execution
   {
      public string ExecutionId { get; set; }
      public Order Order { get; set; }
      public double Price { get; set; }
      public int Quantity { get; set; }
      public DateTime Time { get; set; }
      public MarketPosition MarketPosition { get; set; }
      public Instrument Instrument { get; set; }
      public string Name { get; set; }
      public OrderAction OrderAction { get; set; }
      public Position Position { get; set; }
      public Account Account { get; set; }
      public Commission Commission { get; set; }
   }

   public class Commission
   {
      public double Amount { get; set; }
      public string Currency { get; set; } = "USD";

      public static implicit operator double(Commission c) => c?.Amount ?? 0;

      public string ToString(string format, System.IFormatProvider provider)
         => Amount.ToString(format, provider);
   }

   public class Position
   {
      public Instrument Instrument { get; set; }
      public MarketPosition MarketPosition { get; set; } = MarketPosition.Flat;
      public int Quantity { get; set; }
      public double AveragePrice { get; set; }
      public Account Account { get; set; }

      public double GetUnrealizedProfitLoss(PerformanceUnit unit)
         => throw new System.NotSupportedException(
               "[SHIM] Position.GetUnrealizedProfitLoss(unit) without a price: the shim has no live "
               + "mark - call GetUnrealizedProfitLoss(unit, currentPrice). No fallback.");
      public double GetUnrealizedProfitLoss(PerformanceUnit unit, double currentPrice)
      {
         if (MarketPosition == MarketPosition.Flat || Quantity == 0) return 0;
         if (unit != PerformanceUnit.Currency)
            throw new System.NotSupportedException(
               $"[SHIM] Position.GetUnrealizedProfitLoss({unit}) is not implemented - only Currency is "
               + "(measured semantics: points x quantity x point value). No fallback.");
         double pointValue = Instrument?.MasterInstrument?.PointValue
            ?? throw new System.InvalidOperationException(
               "[SHIM] Position.GetUnrealizedProfitLoss(Currency): the position has no instrument "
               + "with a point value. No fallback.");
         double dir = MarketPosition == MarketPosition.Long ? 1.0 : -1.0;
         return (currentPrice - AveragePrice) * dir * Quantity * pointValue;
      }
   }

   public enum PerformanceUnit { Points, Ticks, Currency, Percent, Pips }

   public partial class Account
   {
      public NinjaTrader.Data.PlaybackConnectionInfo Connection { get; set; }

      public string Name { get; set; }
      public string AccountId { get; set; }
      public string DisplayName { get; set; }
      public Currency Denomination { get; set; } = Currency.UsDollar;
      public double Get(AccountItem item, Currency currency) => 0;

      public System.Collections.ObjectModel.Collection<Order> Orders { get; } = new System.Collections.ObjectModel.Collection<Order>();
      public System.Collections.ObjectModel.Collection<Position> Positions { get; } = new System.Collections.ObjectModel.Collection<Position>();
      public void Flatten(System.Collections.Generic.ICollection<Instrument> instruments) { }
   }

   public enum AccountItem
   {
      CashValue, RealizedProfitLoss, UnrealizedProfitLoss, BuyingPower,
      ExcessIntradayMargin, ExcessInitialMargin, GrossRealizedProfitLoss,
      InitialMargin, NetLiquidation, TotalCashBalance,
   }

   public enum Currency
   {
      AustralianDollar, BritishPound, CanadianDollar, Euro, HongKongDollar,
      JapaneseYen, SwissFranc, UsDollar, KoreanWon, IndianRupee, SwedishKrona,
      MexicanPeso, BrazilianReal, SingaporeDollar, ChinaYuan, MalaysiaRinggit,
      ThailandBaht, TaiwanNewDollar, NewZealandDollar, SouthAfricanRand,
      CzechRepublicKoruna, NorwayKrone, DenmarkKrone, HungaryForint,
      PolandZloty, TurkeyLira, IsraeliShekel, RussiaRuble, SaudiRiyal,
   }

   public class AccountItemEventArgs : EventArgs
   {
      public AccountItem AccountItem { get; set; }
      public double Value { get; set; }
      public Currency Currency { get; set; }
   }

   // ---- Instrument ---------------------------------------------------------
   public partial class Instrument
   {
      public string FullName { get; set; }
      public string MasterInstrument_Name { get; set; }
      public MasterInstrument MasterInstrument { get; set; }
      public Exchange Exchange { get; set; }
   }

   public class MasterInstrument
   {
      public string Name { get; set; }
      public InstrumentType InstrumentType { get; set; }
      public double TickSize { get; set; } = 0.01;
      public double PointValue { get; set; } = 1;
      public int Currency { get; set; }

      public double RoundToTickSize(double price)
         => TickSize > 0
            ? Math.Round(price / TickSize, MidpointRounding.AwayFromZero) * TickSize
            : price;

      public string FormatPrice(double price)
      {
         int decimals = 0;
         double t = TickSize;
         while (decimals < 10 && Math.Abs(t - Math.Round(t)) > 1e-9) { t *= 10; decimals++; }
         return price.ToString("F" + decimals, System.Globalization.CultureInfo.InvariantCulture);
      }

      public int Compare(double price1, double price2)
         => price1 > price2 ? 1 : price1 < price2 ? -1 : 0;
   }

   public enum InstrumentType { Stock, Future, Option, Forex, Cfd, Cash, Crypto, CryptoCurrency, Index, Unknown, ETF, Index_, Equity }

   public enum Exchange { Default, Globex, Nyse, Nasdaq, Eurex, Liffe, Other }
}

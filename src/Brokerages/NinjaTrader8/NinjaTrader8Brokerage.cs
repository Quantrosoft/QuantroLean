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
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using NodaTime;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Interfaces;
using QuantConnect.Logging;
using QuantConnect.Orders;
using QuantConnect.Orders.Fees;
using QuantConnect.Orders.TimeInForces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace MyLean.Brokerages.NinjaTrader8
{
    /// <summary>
    /// Lean brokerage and live data source for NinjaTrader 8. It starts the NinjaTrader 8 gateway companion process
    /// (`serve`), which drives a local NinjaTrader 8 installation, connects one of the connections configured in
    /// NinjaTrader and trades one of its accounts. Futures only.
    ///
    /// Market data: every trade NinjaTrader reports becomes a trade tick, every bid or ask change a quote tick with
    /// the current bid and ask; the tick time is NinjaTrader's own event time in the exchange time zone. What
    /// NinjaTrader hands over from before the subscription is not passed on (the gateway drops it).
    ///
    /// Orders: market, limit, stop market and stop limit orders, Day or GoodTilCanceled, whole contracts. The gateway
    /// order id is the broker id. Fills come from NinjaTrader's executions, one order event per execution, with the
    /// execution's commission as fee.
    /// </summary>
    [BrokerageFactory(typeof(NinjaTrader8BrokerageFactory))]
    public sealed class NinjaTrader8Brokerage : Brokerage, IDataQueueHandler
    {
        private readonly object _ordersLock = new();
        private readonly Dictionary<string, List<string[]>> _earlyOrderLines = new();
        private readonly HashSet<string> _executionIds = new();
        private readonly ConcurrentDictionary<string, Symbol> _symbolByName = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<Symbol, Quote> _quotes = new();
        private readonly ConcurrentDictionary<Symbol, DateTimeZone> _exchangeZones = new();
        private readonly NinjaTrader8SymbolMapper _mapper = new();
        private readonly MarketHoursDatabase _marketHours = MarketHoursDatabase.FromDataFolder();
        private EventBasedDataQueueHandlerSubscriptionManager _subscriptionManager;
        private IDataAggregator _aggregator;
        private IOrderProvider _orderProvider;
        private Settings _settings;
        private GatewayClient _gateway;
        private string _currency;
        private volatile bool _connectionUp;
        private bool _historyWarned;

        /// <summary>The settings of the brokerage: the brokerage data of the job (see the factory).</summary>
        internal sealed class Settings
        {
            public string Gateway { get; init; }
            public string Connection { get; init; }
            public string Account { get; init; }
            public string UserDataDir { get; init; }
            public TimeSpan StartTimeout { get; init; }

            public static Settings From(IReadOnlyDictionary<string, string> data)
            {
                string Get(string key) => data.TryGetValue(key, out string v) ? v : null;
                string Require(string key)
                {
                    string v = Get(key);
                    if (string.IsNullOrWhiteSpace(v)) throw new ArgumentException("NinjaTrader 8: '" + key + "' is not set");
                    return v;
                }
                string timeout = Get("ninjatrader8-start-timeout");
                return new Settings
                {
                    Gateway = Require("ninjatrader8-gateway"),
                    // connection and account may be empty: the gateway then shows a dropdown and reports the
                    // choice, which Connect persists back here so the next start passes it and shows no box.
                    Connection = Get("ninjatrader8-connection"),
                    Account = Get("ninjatrader8-account"),
                    UserDataDir = Get("ninjatrader8-user-data-dir"),
                    StartTimeout = TimeSpan.FromSeconds(string.IsNullOrWhiteSpace(timeout) ? 300 : int.Parse(timeout, CultureInfo.InvariantCulture)),
                };
            }
        }

        private sealed class Quote
        {
            public decimal BidPrice, BidSize, AskPrice, AskSize;
        }

        /// <summary>Parameterless constructor, used when Lean creates the brokerage as data queue handler only;
        /// <see cref="SetJob"/> then supplies the settings.</summary>
        public NinjaTrader8Brokerage() : base("NinjaTrader8Brokerage")
        {
        }

        internal NinjaTrader8Brokerage(Settings settings, IOrderProvider orderProvider) : base("NinjaTrader8Brokerage")
        {
            Initialize(settings, orderProvider);
        }

        private void Initialize(Settings settings, IOrderProvider orderProvider)
        {
            if (_settings != null) return;
            _settings = settings;
            _orderProvider = orderProvider;
            _aggregator = Composer.Instance.GetPart<IDataAggregator>()
                ?? Composer.Instance.GetExportedValueByTypeName<IDataAggregator>(
                    Config.Get("data-aggregator", "QuantConnect.Lean.Engine.DataFeeds.AggregationManager"), forceTypeNameOnExisting: false);
            _subscriptionManager = new EventBasedDataQueueHandlerSubscriptionManager();
            _subscriptionManager.SubscribeImpl += (symbols, _) => Subscribe(symbols);
            _subscriptionManager.UnsubscribeImpl += (symbols, _) => Unsubscribe(symbols);
        }

        /// <summary>True while the gateway session is up and NinjaTrader reports the connection as connected.</summary>
        public override bool IsConnected => _gateway != null && _gateway.IsConnected && _connectionUp;

        // ---------------------------------------------------------------- connection

        /// <summary>Starts the gateway and connects to it.</summary>
        public override void Connect()
        {
            if (_gateway != null && _gateway.IsConnected) return;
            if (_settings == null) throw new InvalidOperationException("NinjaTrader 8: no settings (see the factory's brokerage data)");
            _gateway = new GatewayClient();
            _gateway.Pushed += OnPushed;
            _gateway.Closed += reason =>
            {
                _connectionUp = false;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Disconnect, "GatewayClosed", "NinjaTrader 8 gateway: " + reason));
            };
            _gateway.Start(_settings.Gateway, _settings.Connection, _settings.Account, _settings.UserDataDir, _settings.StartTimeout);
            PersistSelectedNames(_gateway.SelectedConnection, _gateway.SelectedAccount);

            string[] hello = _gateway.Request("hello");
            // protocol, connection, account, currency, status, price status, global simulation mode
            _currency = CurrencyCode(hello[3]);
            AccountBaseCurrency = _currency;
            _connectionUp = hello[4] == "Connected" && hello[5] == "Connected";
            Log.Trace("NinjaTrader8Brokerage: connected to '" + hello[1] + "', account " + hello[2] + " (" + _currency + "), status " + hello[4]
                + ", price feed " + hello[5] + ", NinjaTrader global simulation mode " + hello[6]);
        }

        /// <summary>Ends the gateway session; the gateway disconnects NinjaTrader and exits.</summary>
        public override void Disconnect()
        {
            _gateway?.Dispose();
            _gateway = null;
            _connectionUp = false;
        }

        public override void Dispose()
        {
            Disconnect();
            _subscriptionManager?.DisposeSafely();
            base.Dispose();
        }

        private static string CurrencyCode(string ninjaTraderCurrency)
        {
            switch (ninjaTraderCurrency)
            {
                case "UsDollar": return "USD";
                case "Euro": return "EUR";
                case "BritishPound": return "GBP";
                case "JapaneseYen": return "JPY";
                case "SwissFranc": return "CHF";
                case "CanadianDollar": return "CAD";
                case "AustralianDollar": return "AUD";
                case "HongKongDollar": return "HKD";
                case "NewZealandDollar": return "NZD";
                default: throw new NotSupportedException("NinjaTrader 8: account currency '" + ninjaTraderCurrency + "' is not mapped");
            }
        }

        // ---------------------------------------------------------------- account

        /// <summary>The account's cash value in its currency.</summary>
        public override List<CashAmount> GetCashBalance()
        {
            return new List<CashAmount> { new CashAmount(AccountItems()["CashValue"], _currency) };
        }

        private Dictionary<string, decimal> AccountItems()
        {
            var items = new Dictionary<string, decimal>();
            foreach (string field in _gateway.Request("account"))
            {
                int eq = field.IndexOf('=', StringComparison.Ordinal);
                if (eq > 0) items[field[..eq]] = ParseDecimal(field[(eq + 1)..]);
            }
            return items;
        }

        /// <summary>The account's open positions.</summary>
        public override List<Holding> GetAccountHoldings()
        {
            var holdings = new List<Holding>();
            foreach (string field in _gateway.Request("positions"))
            {
                string[] p = field.Split((char)0x1F);
                Symbol symbol = _mapper.GetLeanSymbol(p[0]);
                holdings.Add(new Holding
                {
                    Symbol = symbol,
                    Quantity = ParseDecimal(p[1]),
                    AveragePrice = ParseDecimal(p[2]),
                    CurrencySymbol = Currencies.GetCurrencySymbol(_currency),
                });
            }
            return holdings;
        }

        /// <summary>The account's orders that are not done, as Lean orders with the gateway order id as broker id.</summary>
        public override List<Order> GetOpenOrders()
        {
            var orders = new List<Order>();
            foreach (string field in _gateway.Request("orders"))
            {
                // id, broker order id, name, instrument, action, type, quantity, limit, stop, time in force, state, filled, average fill
                string[] o = field.Split((char)0x1F);
                Symbol symbol = _mapper.GetLeanSymbol(o[3]);
                decimal quantity = ParseDecimal(o[6]) * (o[4] == "Buy" || o[4] == "BuyToCover" ? 1 : -1);
                var properties = new OrderProperties { TimeInForce = o[9] == "Gtc" ? TimeInForce.GoodTilCanceled : TimeInForce.Day };
                Order order = o[5] switch
                {
                    "Market" => new MarketOrder(symbol, quantity, DateTime.UtcNow, o[2], properties),
                    "Limit" => new LimitOrder(symbol, quantity, ParseDecimal(o[7]), DateTime.UtcNow, o[2], properties),
                    "StopMarket" => new StopMarketOrder(symbol, quantity, ParseDecimal(o[8]), DateTime.UtcNow, o[2], properties),
                    "StopLimit" => new StopLimitOrder(symbol, quantity, ParseDecimal(o[8]), ParseDecimal(o[7]), DateTime.UtcNow, o[2], properties),
                    _ => null,
                };
                if (order == null)
                {
                    OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UnsupportedOrderType",
                        "NinjaTrader 8: open order " + o[1] + " of type " + o[5] + " is not supported and was skipped"));
                    continue;
                }
                order.BrokerId.Add(o[0]);
                orders.Add(order);
            }
            return orders;
        }

        // ---------------------------------------------------------------- orders

        /// <summary>Places the order through the gateway; the gateway order id becomes the broker id.</summary>
        public override bool PlaceOrder(Order order)
        {
            string type = order.Type switch
            {
                OrderType.Market => "Market",
                OrderType.Limit => "Limit",
                OrderType.StopMarket => "StopMarket",
                OrderType.StopLimit => "StopLimit",
                _ => null,
            };
            string tif = order.TimeInForce is DayTimeInForce ? "Day" : order.TimeInForce is GoodTilCanceledTimeInForce ? "Gtc" : null;
            decimal quantity = Math.Abs(order.Quantity);
            string problem = type == null ? "order type " + order.Type + " is not supported"
                : tif == null ? "time in force " + order.TimeInForce.GetType().Name + " is not supported"
                : quantity != Math.Floor(quantity) ? "only whole contracts can be traded"
                : order.Symbol.SecurityType != SecurityType.Future ? "only futures are supported"
                : null;
            if (problem != null)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, "NinjaTrader 8: " + problem) { Status = OrderStatus.Invalid });
                return false;
            }
            decimal limit = order is LimitOrder lo ? lo.LimitPrice : order is StopLimitOrder slo ? slo.LimitPrice : 0m;
            decimal stop = order is StopMarketOrder so ? so.StopPrice : order is StopLimitOrder sl ? sl.StopPrice : 0m;
            string[] reply;
            try
            {
                reply = _gateway.Request("place", "Lean " + order.Id.ToStringInvariant(), _mapper.GetBrokerageSymbol(order.Symbol),
                    order.Direction == OrderDirection.Buy ? "Buy" : "Sell", type, quantity.ToStringInvariant(), limit.ToStringInvariant(),
                    stop.ToStringInvariant(), tif);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is ArgumentException)
            {
                OnOrderEvent(new OrderEvent(order, DateTime.UtcNow, OrderFee.Zero, ex.Message) { Status = OrderStatus.Invalid });
                return false;
            }
            string brokerId = reply[0];
            List<string[]> early;
            lock (_ordersLock)
            {
                order.BrokerId.Add(brokerId);
                _earlyOrderLines.Remove(brokerId, out early);
            }
            if (early != null)
                foreach (string[] line in early) HandleOrderLine(line);
            return true;
        }

        /// <summary>Changes quantity, limit and stop of the order.</summary>
        public override bool UpdateOrder(Order order)
        {
            decimal limit = order is LimitOrder lo ? lo.LimitPrice : order is StopLimitOrder slo ? slo.LimitPrice : 0m;
            decimal stop = order is StopMarketOrder so ? so.StopPrice : order is StopLimitOrder sl ? sl.StopPrice : 0m;
            try
            {
                _gateway.Request("change", order.BrokerId.Last(), Math.Abs(order.Quantity).ToStringInvariant(), limit.ToStringInvariant(), stop.ToStringInvariant());
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is ArgumentException)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "UpdateFailed", "NinjaTrader 8: update of order " + order.Id + " failed: " + ex.Message));
                return false;
            }
        }

        /// <summary>Cancels the order.</summary>
        public override bool CancelOrder(Order order)
        {
            try
            {
                _gateway.Request("cancel", order.BrokerId.Last());
                return true;
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is ArgumentException)
            {
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "CancelFailed", "NinjaTrader 8: cancel of order " + order.Id + " failed: " + ex.Message));
                return false;
            }
        }

        private void HandleOrderLine(string[] f)
        {
            string brokerId = f[1];
            Order order;
            lock (_ordersLock)
            {
                order = _orderProvider?.GetOrdersByBrokerageId(brokerId)?.FirstOrDefault();
                if (order == null)
                {
                    // the gateway reports an order before PlaceOrder has the broker id - keep the line until it has
                    if (!_earlyOrderLines.TryGetValue(brokerId, out var list)) _earlyOrderLines[brokerId] = list = new List<string[]>();
                    list.Add(f);
                    return;
                }
            }
            if (f[0] == "O") HandleOrderUpdate(order, f);
            else HandleExecution(order, f);
        }

        // O ID BROKERID STATE FILLED AVGFILL QTY LIMIT STOP TIME ERROR COMMENT
        private void HandleOrderUpdate(Order order, string[] f)
        {
            DateTime time = Utc(f[9]);
            string message = "NinjaTrader 8: " + f[3] + (f.Length > 10 && f[10] != "NoError" ? " " + f[10] : "") + (f.Length > 11 && f[11].Length > 0 ? " " + f[11] : "");
            OrderStatus? status = f[3] switch
            {
                "Submitted" or "Accepted" or "Working" or "TriggerPending" or "AcceptedByRisk" =>
                    order.Status == OrderStatus.New ? OrderStatus.Submitted
                    : order.Status == OrderStatus.UpdateSubmitted ? OrderStatus.Submitted : null,
                "CancelPending" or "CancelSubmitted" => OrderStatus.CancelPending,
                "ChangePending" or "ChangeSubmitted" => OrderStatus.UpdateSubmitted,
                "Cancelled" => OrderStatus.Canceled,
                "Rejected" => OrderStatus.Invalid,
                _ => null,   // Initialized, PartFilled, Filled: the executions carry the fills
            };
            if (status == null || status == order.Status) return;
            OnOrderEvent(new OrderEvent(order, time, OrderFee.Zero, message) { Status = status.Value });
        }

        // X ID EXECID TIME PRICE QTY POSITION COMMISSION
        private void HandleExecution(Order order, string[] f)
        {
            lock (_ordersLock)
                if (!_executionIds.Add(f[2])) return;
            decimal quantity = ParseDecimal(f[5]) * (order.Direction == OrderDirection.Buy ? 1 : -1);
            decimal filled = FilledSoFar(order) + quantity;
            var fee = new OrderFee(new CashAmount(ParseDecimal(f[7]), _currency));
            OnOrderEvent(new OrderEvent(order, Utc(f[3]), fee, "NinjaTrader 8 execution " + f[2])
            {
                Status = Math.Abs(filled) >= Math.Abs(order.Quantity) ? OrderStatus.Filled : OrderStatus.PartiallyFilled,
                FillPrice = ParseDecimal(f[4]),
                FillQuantity = quantity,
            });
        }

        private readonly ConcurrentDictionary<int, decimal> _filled = new();

        private decimal FilledSoFar(Order order) => _filled.TryGetValue(order.Id, out decimal q) ? q : 0m;

        protected override void OnOrderEvent(OrderEvent e)
        {
            if (e.FillQuantity != 0) _filled.AddOrUpdate(e.OrderId, e.FillQuantity, (_, q) => q + e.FillQuantity);
            base.OnOrderEvent(e);
        }

        // ---------------------------------------------------------------- pushed lines

        private void OnPushed(string[] f)
        {
            switch (f[0])
            {
                case "T":
                case "B":
                case "A":
                    OnMarketData(f);
                    break;
                case "O":
                case "X":
                    if (f[1] != "0") HandleOrderLine(f);
                    break;
                case "V":
                    if (f[1] == "CashValue") OnAccountChanged(new AccountEvent(_currency, ParseDecimal(f[2])));
                    break;
                case "C":
                    bool up = f[1] == "Connected" && f[2] == "Connected";
                    if (up != _connectionUp)
                    {
                        _connectionUp = up;
                        OnMessage(new BrokerageMessageEvent(up ? BrokerageMessageType.Reconnect : BrokerageMessageType.Disconnect,
                            "Connection", "NinjaTrader 8 connection: " + f[1] + ", price feed " + f[2] + (f.Length > 3 && f[3] != "NoError" ? ", " + f[3] : "")
                            + (f.Length > 4 && f[4].Length > 0 ? " " + f[4] : "")));
                    }
                    break;
                case "P":
                    Log.Trace("NinjaTrader8Brokerage: position " + f[1] + " " + f[2] + " at " + f[3]);
                    break;
            }
        }

        // ---------------------------------------------------------------- market data

        /// <summary>Subscribes the data of the configuration.</summary>
        public IEnumerator<BaseData> Subscribe(SubscriptionDataConfig dataConfig, EventHandler newDataAvailableHandler)
        {
            if (!CanSubscribe(dataConfig.Symbol)) return null;
            IEnumerator<BaseData> enumerator = _aggregator.Add(dataConfig, newDataAvailableHandler);
            _subscriptionManager.Subscribe(dataConfig);
            return enumerator;
        }

        /// <summary>Removes the subscription of the configuration.</summary>
        public void Unsubscribe(SubscriptionDataConfig dataConfig)
        {
            _subscriptionManager.Unsubscribe(dataConfig);
            _aggregator.Remove(dataConfig);
        }

        /// <summary>Supplies the settings when Lean uses this class as data queue handler only, and connects.</summary>
        public void SetJob(LiveNodePacket job)
        {
            Initialize(Settings.From(job.BrokerageData), null);
            if (!IsConnected) Connect();
        }

        private static bool CanSubscribe(Symbol symbol) =>
            symbol.SecurityType == SecurityType.Future && !symbol.IsCanonical()
            && symbol.Value.IndexOf("universe", StringComparison.OrdinalIgnoreCase) < 0;

        private bool Subscribe(IEnumerable<Symbol> symbols)
        {
            foreach (Symbol symbol in symbols)
            {
                string[] reply = _gateway.Request("sub", _mapper.GetBrokerageSymbol(symbol));
                _symbolByName[reply[0]] = symbol;
                Log.Trace("NinjaTrader8Brokerage: subscribed " + symbol.ToString() + " as " + reply[0] + " (tick size " + reply[3] + ", point value " + reply[4] + ")");
            }
            return true;
        }

        private bool Unsubscribe(IEnumerable<Symbol> symbols)
        {
            foreach (Symbol symbol in symbols)
            {
                string name = _mapper.GetBrokerageSymbol(symbol);
                _gateway.Request("unsub", name);
                _symbolByName.TryRemove(name, out _);
                _quotes.TryRemove(symbol, out _);
            }
            return true;
        }

        // T NAME TIME PRICE SIZE BID ASK / B NAME TIME PRICE SIZE / A NAME TIME PRICE SIZE
        private void OnMarketData(string[] f)
        {
            if (!_symbolByName.TryGetValue(f[1], out Symbol symbol)) return;
            DateTimeZone zone = _exchangeZones.GetOrAdd(symbol, s => _marketHours.GetExchangeHours(s.ID.Market, s, s.SecurityType).TimeZone);
            DateTime time = Utc(f[2]).ConvertFromUtc(zone);
            decimal price = ParseDecimal(f[3]);
            decimal size = ParseDecimal(f[4]);
            if (f[0] == "T")
            {
                _aggregator.Update(new Tick(time, symbol, string.Empty, string.Empty, size, price) { TickType = TickType.Trade });
                return;
            }
            Quote q = _quotes.GetOrAdd(symbol, _ => new Quote());
            Tick quote;
            lock (q)
            {
                if (f[0] == "B") { q.BidPrice = price; q.BidSize = size; }
                else { q.AskPrice = price; q.AskSize = size; }
                if (q.BidPrice == 0 || q.AskPrice == 0) return;
                quote = new Tick(time, symbol, string.Empty, string.Empty, q.BidSize, q.BidPrice, q.AskSize, q.AskPrice) { TickType = TickType.Quote };
            }
            _aggregator.Update(quote);
        }

        /// <summary>NinjaTrader's history is not served through the gateway yet.</summary>
        public override IEnumerable<BaseData> GetHistory(QuantConnect.Data.HistoryRequest request)
        {
            if (!_historyWarned)
            {
                _historyWarned = true;
                OnMessage(new BrokerageMessageEvent(BrokerageMessageType.Warning, "NoHistory", "NinjaTrader 8: history requests are not supported"));
            }
            return null;
        }

        // ---------------------------------------------------------------- helpers

        private static DateTime Utc(string ticks) => new DateTime(long.Parse(ticks, CultureInfo.InvariantCulture), DateTimeKind.Utc);

        private static decimal ParseDecimal(string s) =>
            s == "NaN" ? 0m : decimal.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);

        // ---------------------------------------------------------------- config write-back

        /// <summary>Writes the connection and account the gateway resolved back into the Lean configuration file, so
        /// the next start passes them as --connection/--account and the gateway shows no selection box. Only a value
        /// that actually changed is written. Best effort: a failure here is logged and never breaks the connection.
        /// The user can clear or correct the stored value by hand, then the dropdown returns.</summary>
        private void PersistSelectedNames(string connection, string account)
        {
            bool connChanged = !string.IsNullOrEmpty(connection) && !string.Equals(connection, _settings.Connection, StringComparison.Ordinal);
            bool acctChanged = !string.IsNullOrEmpty(account) && !string.Equals(account, _settings.Account, StringComparison.Ordinal);
            if (!connChanged && !acctChanged) return;

            string path = ConfigFilePath();
            string tmp = path + ".tmp";
            try
            {
                if (!File.Exists(path))
                {
                    Log.Trace("NinjaTrader8Brokerage: no configuration file at " + path + " - the chosen connection/account are not stored");
                    return;
                }
                string text = File.ReadAllText(path);
                if (connChanged) text = SetJsonStringValue(text, "ninjatrader8-connection", connection);
                if (acctChanged) text = SetJsonStringValue(text, "ninjatrader8-account", account);
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Replace(tmp, path, null);
                Log.Trace("NinjaTrader8Brokerage: stored connection '" + connection + "' account '" + account + "' in " + path + " for the next start");
            }
            catch (Exception ex)
            {
                Log.Error("NinjaTrader8Brokerage: could not store the chosen connection/account in " + path + ": " + ex.Message);
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort cleanup */ }
            }
        }

        /// <summary>The configuration file Lean loaded and the factory reads the brokerage data from (config.json in
        /// the current directory unless --config set another). Read from Lean's own loader; falls back to config.json.</summary>
        private static string ConfigFilePath()
        {
            string name = "config.json";
            try
            {
                FieldInfo field = typeof(Config).GetField("ConfigurationFileName", BindingFlags.NonPublic | BindingFlags.Static);
                if (field?.GetValue(null) is string configured && !string.IsNullOrWhiteSpace(configured)) name = configured;
            }
            catch (Exception ex)
            {
                Log.Trace("NinjaTrader8Brokerage: could not read Lean's configuration file name, using '" + name + "': " + ex.Message);
            }
            return Path.GetFullPath(name);
        }

        /// <summary>Sets the top-level JSON string property <paramref name="key"/> to <paramref name="value"/> in the
        /// text of a (possibly commented) JSON document, changing only that one value and leaving everything else -
        /// comments, order, whitespace - byte for byte. Adds the property as the first member if it is absent; leaves
        /// the document unchanged if the key is present but its value is not a plain string.</summary>
        private static string SetJsonStringValue(string json, string key, string value)
        {
            string needle = "\"" + key + "\"";
            int k = json.IndexOf(needle, StringComparison.Ordinal);
            if (k >= 0)
            {
                int i = k + needle.Length;
                while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                if (i < json.Length && json[i] == ':')
                {
                    i++;
                    while (i < json.Length && char.IsWhiteSpace(json[i])) i++;
                    if (i < json.Length && json[i] == '"')
                    {
                        int start = i++;
                        while (i < json.Length)
                        {
                            if (json[i] == '\\') { i += 2; continue; }
                            if (json[i] == '"') { i++; break; }
                            i++;
                        }
                        return json.Substring(0, start) + JsonString(value) + json.Substring(i);
                    }
                }
                return json;   // present but not a plain string value: leave it, do not add a duplicate
            }
            int brace = json.IndexOf('{');
            if (brace < 0) return json;
            string nl = json.Contains("\r\n") ? "\r\n" : "\n";
            return json.Substring(0, brace + 1) + nl + "  " + needle + ": " + JsonString(value) + "," + json.Substring(brace + 1);
        }

        /// <summary>Encodes a string as a JSON string literal, including the surrounding quotes.</summary>
        private static string JsonString(string s)
        {
            var sb = new StringBuilder(s.Length + 2);
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
    }
}

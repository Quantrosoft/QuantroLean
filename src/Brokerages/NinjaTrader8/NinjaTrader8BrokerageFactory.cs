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
using System.Collections.Generic;
using QuantConnect;
using QuantConnect.Brokerages;
using QuantConnect.Configuration;
using QuantConnect.Interfaces;
using QuantConnect.Packets;
using QuantConnect.Securities;
using QuantConnect.Util;

namespace QuantroLean.Brokerages.NinjaTrader8
{
    /// <summary>
    /// Creates the NinjaTrader 8 brokerage. Configuration (config.json or the job's brokerage data):
    ///   ninjatrader8-gateway        path of NinjaTrader8.Gateway.exe
    ///   ninjatrader8-connection     name of the connection as configured in NinjaTrader
    ///   ninjatrader8-account        name of the account on that connection
    ///   ninjatrader8-user-data-dir  optional: NinjaTrader data folder for the gateway (default: NinjaTrader's own;
    ///                               NinjaTrader allows one process per data folder)
    ///   ninjatrader8-start-timeout  optional: seconds until the gateway must be ready (default 300)
    /// </summary>
    public sealed class NinjaTrader8BrokerageFactory : BrokerageFactory
    {
        public NinjaTrader8BrokerageFactory() : base(typeof(NinjaTrader8Brokerage))
        {
        }

        public override Dictionary<string, string> BrokerageData => new()
        {
            { "ninjatrader8-gateway", Config.Get("ninjatrader8-gateway") },
            { "ninjatrader8-connection", Config.Get("ninjatrader8-connection") },
            { "ninjatrader8-account", Config.Get("ninjatrader8-account") },
            { "ninjatrader8-user-data-dir", Config.Get("ninjatrader8-user-data-dir") },
            { "ninjatrader8-start-timeout", Config.Get("ninjatrader8-start-timeout", "300") },
        };

        public override IBrokerageModel GetBrokerageModel(IOrderProvider orderProvider) => new DefaultBrokerageModel(AccountType.Margin);

        public override IBrokerage CreateBrokerage(LiveNodePacket job, IAlgorithm algorithm)
        {
            if (job == null) throw new ArgumentNullException(nameof(job));
            var brokerage = new NinjaTrader8Brokerage(NinjaTrader8Brokerage.Settings.From(job.BrokerageData), algorithm?.Transactions);
            Composer.Instance.AddPart<IDataQueueHandler>(brokerage);
            return brokerage;
        }

        public override void Dispose()
        {
        }
    }
}

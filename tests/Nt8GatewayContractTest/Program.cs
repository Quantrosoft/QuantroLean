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
using System.Globalization;
using System.IO;
using System.Threading;
using MyLean.Brokerages.NinjaTrader8;   // GatewayClient (internal, compiled into this assembly)

// Integration test for the NinjaTrader 8 brokerage client against a running gateway companion.
// It drives the shipped GatewayClient through the whole contract against a chosen connection and account
// (use NinjaTrader's built-in "Simulated Data Feed" / Sim101 for a login-free run), then restart+reconnect
// and a second client (multi-instance). It needs a local gateway exe; see README.md. Not a CI test.
// Args: <gatewayExe> <connection> <account> <symbol> <logFile>
internal static class Program
{
    private static StreamWriter _log;
    private static readonly object _lock = new object();
    private static int _pass, _fail;

    private static void L(string s)
    {
        string line = DateTime.UtcNow.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture) + "  " + s;
        lock (_lock) { Console.WriteLine(line); _log?.WriteLine(line); _log?.Flush(); }
    }

    private static void Step(string name, Action act)
    {
        try { act(); _pass++; L("PASS " + name); }
        catch (Exception ex) { _fail++; L("FAIL " + name + ": " + ex.Message); }
    }

    private static string Join(string[] f) => f == null ? "<null>" : (f.Length == 0 ? "<empty ok>" : string.Join("|", f));

    private static int Main(string[] a)
    {
        if (a.Length < 5) { Console.Error.WriteLine("args: <gatewayExe> <connection> <account> <symbol> <logFile>"); return 2; }
        string exe = a[0], conn = a[1], acct = a[2], sym = a[3], logf = a[4];
        _log = new StreamWriter(logf, false) { AutoFlush = true };
        QuantConnect.Logging.Log.Sink = s => L(s);

        var pushes = new List<string>();
        L("=== NinjaTrader 8 gateway brokerage contract test ===");
        L("exe=" + exe);
        L("conn=" + conn + "  acct=" + acct + "  sym=" + sym);

        // ---- Phase A: single instance, full contract ----
        GatewayClient gc = null;
        string brokerId = null;
        try
        {
            gc = new GatewayClient();
            gc.Pushed += f => { lock (pushes) pushes.Add(string.Join("|", f)); };
            gc.Closed += r => L("Closed: " + r);
            Step("start+connect", () =>
            {
                gc.Start(exe, conn, acct, null, TimeSpan.FromSeconds(120));
                L("  SelectedConnection=" + gc.SelectedConnection + "  SelectedAccount=" + gc.SelectedAccount + "  IsConnected=" + gc.IsConnected);
            });
            if (gc.IsConnected)
            {
                Step("hello", () => L("  hello -> " + Join(gc.Request("hello"))));
                Step("account", () => L("  account -> " + Join(gc.Request("account"))));
                Step("positions", () => L("  positions -> " + Join(gc.Request("positions"))));
                Step("orders", () => L("  orders -> " + Join(gc.Request("orders"))));
                Step("sub", () => L("  sub -> " + Join(gc.Request("sub", sym))));
                Thread.Sleep(3000);
                lock (pushes) L("  pushes after sub: count=" + pushes.Count + (pushes.Count > 0 ? "  first=" + pushes[0] : ""));

                // working limit far below market -> stays open so change/cancel can act on it
                Step("place(limit,working)", () =>
                {
                    string[] r = gc.Request("place", "Lean 1", sym, "Buy", "Limit", "1", "100", "0", "Day");
                    brokerId = r.Length > 0 ? r[0] : null;
                    L("  place-limit -> " + Join(r) + "  brokerId=" + brokerId);
                });
                if (!string.IsNullOrEmpty(brokerId))
                {
                    Step("change", () => L("  change -> " + Join(gc.Request("change", brokerId, "1", "150", "0"))));
                    Step("cancel", () => L("  cancel -> " + Join(gc.Request("cancel", brokerId))));
                }
                else L("  (skip change/cancel - no brokerId)");

                // market order + flatten -> exercises a fill and position round-trip
                Step("place(market)", () => L("  place-market -> " + Join(gc.Request("place", "Lean 2", sym, "Buy", "Market", "1", "0", "0", "Day"))));
                Thread.Sleep(2500);
                Step("positions(after buy)", () => L("  positions2 -> " + Join(gc.Request("positions"))));
                Step("flatten(market sell)", () => L("  flatten -> " + Join(gc.Request("place", "Lean 3", sym, "Sell", "Market", "1", "0", "0", "Day"))));
                Thread.Sleep(2500);
                Step("positions(after flat)", () => L("  positions3 -> " + Join(gc.Request("positions"))));
                Step("unsub", () => L("  unsub -> " + Join(gc.Request("unsub", sym))));
                lock (pushes) L("  total pushes during phase A: " + pushes.Count);
            }
        }
        catch (Exception ex) { _fail++; L("FATAL phase A: " + ex); }
        finally { Step("shutdown(dispose)", () => { gc?.Dispose(); }); }

        // ---- Phase B: restart + reconnect ----
        GatewayClient gc2 = null;
        try
        {
            gc2 = new GatewayClient();
            Step("restart+reconnect", () =>
            {
                gc2.Start(exe, conn, acct, null, TimeSpan.FromSeconds(120));
                string[] h = gc2.Request("hello");
                L("  reconnect hello -> " + Join(h) + "  IsConnected=" + gc2.IsConnected);
            });
        }
        catch (Exception ex) { _fail++; L("FATAL phase B: " + ex); }
        finally { Step("shutdown2(dispose)", () => { gc2?.Dispose(); }); }

        // ---- Phase C: multi-instance (two clients at once, same data folder) ----
        GatewayClient m1 = null, m2 = null;
        try
        {
            m1 = new GatewayClient();
            Step("multi: first instance", () =>
            {
                m1.Start(exe, conn, acct, null, TimeSpan.FromSeconds(120));
                L("  m1 hello -> " + Join(m1.Request("hello")) + "  IsConnected=" + m1.IsConnected);
            });
            m2 = new GatewayClient();
            Step("multi: second instance (same folder)", () =>
            {
                m2.Start(exe, conn, acct, null, TimeSpan.FromSeconds(45));
                L("  m2 hello -> " + Join(m2.Request("hello")) + "  IsConnected=" + m2.IsConnected);
                L("  BOTH connected simultaneously: m1=" + m1.IsConnected + " m2=" + m2.IsConnected);
            });
        }
        catch (Exception ex) { _fail++; L("FATAL phase C: " + ex); }
        finally { Step("multi: shutdown both", () => { m2?.Dispose(); m1?.Dispose(); }); }

        L("=== SUMMARY pass=" + _pass + " fail=" + _fail + " ===");
        _log.Flush(); _log.Close();
        return _fail == 0 ? 0 : 1;
    }
}

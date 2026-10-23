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
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyLean.Tests.NinjaHostSmokeTest
{
   /// <summary>End-to-end test of the NinjaScript host: runs the example NinjaScript strategy (SmaCrossoverStrategy)
   /// with MyLauncher.NinjaTrader on Lean's SPY ticks of 2013-10-07 and checks
   ///   - the run ends with exit code 0 (the host itself stops on a tick-time, position or bar mismatch),
   ///   - every primary bar was compared with Lean's own consolidation of the same ticks, and there are bars at all,
   ///   - the strategy traded, every entry and exit was routed to Lean, and Lean filled each one,
   ///   - every fill of the strategy (NinjaTrader rules) equals Lean's fill of the mirrored order: time, quantity, price.
   /// Needs the sample data in data/ (tools/SampleData) and the built launcher.
   /// Usage: NinjaHost.SmokeTest [--launcher-dir &lt;MyLauncher.NinjaTrader output folder&gt;] [--config &lt;config.json&gt;]
   /// (--config runs the launcher with another configuration, e.g. to prove that the checks fail on a difference)</summary>
   internal static class Program
   {
      private static int Main(string[] args)
      {
         string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "src", "Lean",
                                                    "MyLauncher.NinjaTrader", "bin", "Debug"));
         string config = null;
         for (int i = 0; i < args.Length; i++)
         {
            if (args[i] == "--launcher-dir" && i + 1 < args.Length) dir = Path.GetFullPath(args[++i]);
            else if (args[i] == "--config" && i + 1 < args.Length) config = Path.GetFullPath(args[++i]);
            else return Fail($"unknown argument '{args[i]}'");
         }
         var exe = Path.Combine(dir, "MyLauncher.NinjaTrader.exe");
         if (!File.Exists(exe)) return Fail($"launcher not built: {exe}");
         if (config != null && !File.Exists(config)) return Fail($"config file not found: {config}");

         var results = Path.Combine(Path.GetTempPath(), "MyLean.NinjaHost.SmokeTest");
         if (Directory.Exists(results)) Directory.Delete(results, recursive: true);
         Directory.CreateDirectory(results);

         var arguments = $"--close-automatically true --results-destination-folder \"{results}\""
                         + (config != null ? $" --config \"{config}\"" : "");
         var start = new ProcessStartInfo(exe, arguments)
         {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
         };
         var lines = new List<string>();
         using (var p = Process.Start(start))
         {
            p.OutputDataReceived += (s, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
            p.ErrorDataReceived += (s, e) => { if (e.Data != null) lock (lines) lines.Add(e.Data); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            if (!p.WaitForExit(15 * 60 * 1000))
            {
               p.Kill(entireProcessTree: true);
               return Fail("the backtest did not end within 15 minutes");
            }
            p.WaitForExit();
            File.WriteAllLines(Path.Combine(results, "launcher.log"), lines);
            if (p.ExitCode != 0) return Fail($"launcher exit code {p.ExitCode} (log {results}\\launcher.log)");
         }

         var errors = lines.Where(l => l.Contains(" ERROR::") || l.Contains("Exception")).ToList();
         if (errors.Count > 0) return Fail("errors in the log: " + errors[0]);
         var end = lines.LastOrDefault(l => l.Contains("[NINJAHOST][END]"));
         if (end == null) return Fail("no [NINJAHOST][END] line - the host did not finish");
         Console.WriteLine(end.Substring(end.IndexOf("[NINJAHOST]", StringComparison.Ordinal)));

         int Number(string label)
         {
            var m = Regex.Match(end, Regex.Escape(label) + " (\\d+)");
            if (!m.Success) throw new InvalidOperationException($"'{label}' missing in the END line");
            return int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
         }

         int bars = Number("primary bars"), verified = Number("bars verified against Lean"), trades = Number("strategy trades");
         int entries = Number("entries routed"), exits = Number("exits routed"), fills = Number("Lean fills");
         if (bars == 0) return Fail("no primary bar was built");
         if (verified != bars) return Fail($"{verified} of {bars} bars verified against Lean");
         if (trades == 0) return Fail("the strategy did not trade");
         if (entries != trades || exits != trades) return Fail($"trades {trades}, entries routed {entries}, exits routed {exits}");
         if (fills != entries + exits) return Fail($"Lean filled {fills} of {entries + exits} orders");

         // fill by fill: the strategy's trade list against Lean's order events
         var tradeList = Path.Combine(results, "SmaCrossoverStrategy-SPY-trades.csv");
         if (!File.Exists(tradeList)) return Fail($"trade list missing: {tradeList}");
         var expected = new List<(string Time, decimal Quantity, decimal Price)>();
         foreach (var row in File.ReadAllLines(tradeList).Skip(1))
         {
            var c = row.Split(',');
            var qty = decimal.Parse(c[3], CultureInfo.InvariantCulture) * (c[2] == "long" ? 1 : -1);
            expected.Add((c[4], qty, decimal.Parse(c[5], CultureInfo.InvariantCulture)));
            expected.Add((c[6], -qty, decimal.Parse(c[7], CultureInfo.InvariantCulture)));
         }
         var eventsFile = Directory.GetFiles(results, "*-order-events.json").SingleOrDefault();
         if (eventsFile == null) return Fail("Lean's order events file is missing");
         var leanFills = new List<(string Time, decimal Quantity, decimal Price)>();
         using (var doc = JsonDocument.Parse(File.ReadAllText(eventsFile)))
         {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
               if (e.GetProperty("status").GetString() != "filled") continue;
               var time = DateTimeOffset.FromUnixTimeMilliseconds(
                  (long)Math.Round(e.GetProperty("time").GetDouble() * 1000)).UtcDateTime;
               leanFills.Add((time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture),
                  e.GetProperty("fillQuantity").GetDecimal(), e.GetProperty("fillPrice").GetDecimal()));
            }
         }
         if (leanFills.Count != expected.Count) return Fail($"strategy fills {expected.Count}, Lean fills {leanFills.Count}");
         static string Show((string Time, decimal Quantity, decimal Price) f)
            => string.Format(CultureInfo.InvariantCulture, "{0}Z {1:0.########} @ {2:0.########}", f.Time, f.Quantity, f.Price);
         for (int i = 0; i < expected.Count; i++)
            if (expected[i] != leanFills[i])
               return Fail($"fill {i + 1}: strategy {Show(expected[i])}, Lean {Show(leanFills[i])}");

         Console.WriteLine($"PASS: {bars} bars equal to Lean's, {trades} trades, {expected.Count} fills equal to Lean's "
                           + "in time, quantity and price.");
         return 0;
      }

      private static int Fail(string message)
      {
         Console.Error.WriteLine("FAIL: " + message);
         return 1;
      }
   }
}

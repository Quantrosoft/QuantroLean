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

Independent implementation. The synthetic price path method: resample the bar returns, rebuild a synthetic
price series, re-run the whole backtest on it, and analyse the distribution of the result metrics.
*/

using System.Globalization;
using MyLean.MonteCarlo;
using MyLean.MonteCarlo.Spp;
using QuantConnect;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var o = ParseArgs(args);
            if (o.ContainsKey("help") || o.Count == 0)
            {
                PrintUsage();
                return o.ContainsKey("help") ? 0 : 2;
            }

            var dataFolder = Required(o, "data-folder");
            var launcher = Required(o, "launcher");
            var algo = o.GetValueOrDefault("algorithm-type-name", "DukascopyExampleAlgorithm");
            var symbolValue = o.GetValueOrDefault("symbol", "EUR_USD");
            var market = o.GetValueOrDefault("market", Market.Dukascopy);
            var securityType = Enum.Parse<SecurityType>(o.GetValueOrDefault("security-type", "Cfd"), ignoreCase: true);
            var startDate = ParseDate(Required(o, "start-date"));
            var endDate = ParseDate(Required(o, "end-date")); // exclusive

            var settings = new MonteCarloSettings
            {
                Method = Resamplers.Parse(o.GetValueOrDefault("method", "block")),
                Iterations = GetInt(o, "iterations", 200),
                Seed = GetULong(o, "seed", 42),
                BlockSize = GetInt(o, "block-size", 0),
                RuinLevel = GetDouble(o, "ruin-level", 0.5),
                VarConfidence = GetDouble(o, "var-confidence", 0.95),
                HistogramBins = GetInt(o, "bins", 20)
            };
            var initialCapital = GetDouble(o, "initial-capital", 100000.0);

            var symbol = Symbol.Create(symbolValue, securityType, market);

            // Read the base minute quote bars for every day in the range, keeping them grouped by day.
            var perDay = new List<(DateTime Day, List<LeanQuoteData.QuoteRow> Bars)>();
            for (var day = startDate; day < endDate; day = day.AddDays(1))
            {
                try
                {
                    var bars = LeanQuoteData.ReadMinuteQuotes(dataFolder, symbol, day);
                    if (bars.Count > 0) perDay.Add((day, bars));
                }
                catch (FileNotFoundException)
                {
                    // A day without data (weekend / holiday) is simply skipped.
                }
            }
            if (perDay.Count == 0)
            {
                Console.Error.WriteLine($"MonteCarloSpp: no minute quote data for {symbolValue} in {dataFolder} between {startDate:yyyy-MM-dd} and {endDate:yyyy-MM-dd}.");
                return 2;
            }

            var totalBars = perDay.Sum(d => d.Bars.Count);
            var baseMids = new double[totalBars];
            var k = 0;
            foreach (var d in perDay)
                foreach (var b in d.Bars)
                    baseMids[k++] = b.MidClose;

            // Log returns of the base mid-close series.
            var returns = new double[totalBars - 1];
            for (var i = 0; i < returns.Length; i++)
            {
                returns[i] = Math.Log(baseMids[i + 1] / baseMids[i]);
            }
            var blockSize = settings.BlockSize > 0 ? settings.BlockSize : MonteCarloEngine.DefaultBlockSize(returns.Length);

            Console.WriteLine($"MonteCarloSpp: {symbolValue} {market}, {perDay.Count} day(s), {totalBars} minute bars, " +
                $"method={o.GetValueOrDefault("method", "block")} iterations={settings.Iterations} seed={settings.Seed} blockSize={blockSize}");

            // Working folders: one overlay data folder (a copy of the real one) and one results folder, both reused.
            var workDir = o.GetValueOrDefault("work-dir", Path.Combine(Path.GetTempPath(), "MyLean.MonteCarloSpp"));
            var overlay = Path.Combine(workDir, "data");
            var resultsDir = Path.Combine(workDir, "res");
            if (Directory.Exists(workDir)) Directory.Delete(workDir, recursive: true);
            Directory.CreateDirectory(workDir);
            CopyDirectory(dataFolder, overlay);

            var runner = new BacktestRunner(launcher, algo);

            // The real run: the backtest on the original prices.
            var realProfits = runner.RunAndGetProfits(dataFolder, Path.Combine(workDir, "res_real"));
            var realMetrics = TradeSeries.ComputeMetrics(realProfits.Length > 0 ? realProfits : [0.0], initialCapital);
            Console.WriteLine($"  real run: {realProfits.Length} trades, net {realProfits.Sum():0.##}");

            // The simulations: one synthetic price path per iteration, the whole backtest re-run on each.
            var rng = new DeterministicRandom(settings.Seed);
            var simMetrics = new Metrics[settings.Iterations];
            var minEquity = new double[settings.Iterations];
            for (var it = 0; it < settings.Iterations; it++)
            {
                var resampled = Resamplers.Resample(settings.Method, returns, rng, blockSize);

                // Rebuild the synthetic mid-close path from the resampled returns, same first price.
                var synthMid = new double[totalBars];
                synthMid[0] = baseMids[0];
                for (var i = 1; i < totalBars; i++)
                {
                    synthMid[i] = synthMid[i - 1] * Math.Exp(resampled[i - 1]);
                }

                // Write the synthetic bars day by day into the overlay, then re-run the backtest.
                var offset = 0;
                foreach (var d in perDay)
                {
                    var slice = new double[d.Bars.Count];
                    Array.Copy(synthMid, offset, slice, 0, d.Bars.Count);
                    offset += d.Bars.Count;
                    var synthBars = LeanQuoteData.ApplyMidPath(d.Bars, slice);
                    LeanQuoteData.WriteMinuteQuotes(overlay, symbol, d.Day, synthBars);
                }

                var profits = runner.RunAndGetProfits(overlay, resultsDir);
                var metrics = TradeSeries.ComputeMetrics(profits.Length > 0 ? profits : [0.0], initialCapital);
                simMetrics[it] = metrics;

                var path = TradeSeries.EquityPath(profits.Length > 0 ? profits : [0.0], initialCapital);
                var mn = path[0];
                foreach (var e in path) if (e < mn) mn = e;
                minEquity[it] = mn;

                if ((it + 1) % 25 == 0 || it + 1 == settings.Iterations)
                {
                    Console.WriteLine($"  {it + 1}/{settings.Iterations} paths done");
                }
            }

            var result = MonteCarloEngine.Summarize(settings, realProfits.Length, initialCapital, blockSize,
                realMetrics, simMetrics, minEquity, paths: null);

            var outDir = o.GetValueOrDefault("out-dir", Directory.GetCurrentDirectory());
            Directory.CreateDirectory(outDir);
            var prefix = o.GetValueOrDefault("prefix", "montecarlo_spp_" + settings.Method.ToString().ToLowerInvariant());
            var label = $"{symbolValue} synthetic price path";
            Report.WriteSimulationsCsv(result, Path.Combine(outDir, prefix + "_simulations.csv"));
            Report.WriteSummaryJson(result, Path.Combine(outDir, prefix + "_summary.json"), label);
            Console.Write(Report.ConsoleSummary(result));
            Console.WriteLine($"wrote {Path.Combine(outDir, prefix + "_simulations.csv")}");
            Console.WriteLine($"wrote {Path.Combine(outDir, prefix + "_summary.json")}");
            if (o.ContainsKey("html"))
            {
                Report.WriteHtml(result, Path.Combine(outDir, prefix + ".html"), label);
                Console.WriteLine($"wrote {Path.Combine(outDir, prefix + ".html")}");
            }

            if (!o.ContainsKey("keep-work"))
            {
                try { Directory.Delete(workDir, recursive: true); } catch { /* best effort */ }
            }
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MonteCarloSpp: {ex.Message}");
            return 1;
        }
    }

    private static void CopyDirectory(string source, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (var dir in Directory.GetDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(dir.Replace(source, dest));
        }
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, file.Replace(source, dest), overwrite: true);
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var flags = new HashSet<string> { "html", "help", "keep-work" };
        var o = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{args[i]}'.");
            var key = args[i][2..];
            if (flags.Contains(key)) o[key] = "true";
            else
            {
                if (i + 1 >= args.Length) throw new ArgumentException($"Option --{key} needs a value.");
                o[key] = args[++i];
            }
        }
        return o;
    }

    private static string Required(Dictionary<string, string> o, string key)
        => o.TryGetValue(key, out var v) ? v : throw new ArgumentException($"--{key} is required.");

    private static DateTime ParseDate(string s)
        => DateTime.ParseExact(s, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static double GetDouble(Dictionary<string, string> o, string key, double fb)
        => o.TryGetValue(key, out var v) ? double.Parse(v, NumberStyles.Any, CultureInfo.InvariantCulture) : fb;

    private static int GetInt(Dictionary<string, string> o, string key, int fb)
        => o.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fb;

    private static ulong GetULong(Dictionary<string, string> o, string key, ulong fb)
        => o.TryGetValue(key, out var v) ? ulong.Parse(v, CultureInfo.InvariantCulture) : fb;

    private static void PrintUsage()
    {
        Console.WriteLine(
@"MyLean Monte Carlo - Synthetic Price Path.

Resamples the bar returns of a backtest's price series into synthetic price paths, writes each as Lean data, and
re-runs the whole backtest on it, then analyses the distribution of the result metrics (the same statistics and
risk measures as the trade-resampling MonteCarlo tool). Deterministic for a given seed.

Usage:
  MonteCarloSpp --data-folder <dir> --launcher <MyLauncher.exe> --start-date YYYYMMDD --end-date YYYYMMDD [options]

Required:
  --data-folder <dir>         the Lean data folder that holds the base minute quote data
  --launcher <path>           path to MyLauncher.exe (its folder must hold the algorithm assembly and config.json)
  --start-date / --end-date   the day range to synthesize (minute quote data must exist; end is exclusive)

Options:
  --algorithm-type-name <n>   algorithm to run (default: DukascopyExampleAlgorithm)
  --symbol <s>                Lean symbol (default: EUR_USD)
  --market <m>                market (default: dukascopy)
  --security-type <t>         security type (default: Cfd)
  --method <m>                reshuffle | bootstrap | block | stationary (default: block)
  --iterations <n>            number of synthetic paths (default: 200)
  --seed <n>                  random seed; same seed = identical result (default: 42)
  --block-size <n>            block length for block/stationary (default: round(sqrt(returns)))
  --initial-capital <n>       starting equity (default: 100000)
  --ruin-level <f>            fraction of initial capital whose loss is ruin (default: 0.5)
  --var-confidence <f>        VaR/CVaR confidence (default: 0.95)
  --bins <n>                  histogram bins (default: 20)
  --out-dir <dir>             output directory (default: current)
  --prefix <name>             output prefix (default: montecarlo_spp_<method>)
  --work-dir <dir>            scratch folder for the data overlay and per-run results (default: temp)
  --keep-work                 keep the scratch folder
  --html                      also write an HTML report
  --help                      this text

Note: each path re-runs the whole backtest, so this is much slower than the trade-resampling tool; use a modest
iteration count.");
    }
}

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

Independent implementation.
*/

using System.Globalization;
using QuantroLean.MonteCarlo;

internal static class Program
{
    private static int Main(string[] args)
    {
        try
        {
            var options = ParseArgs(args);
            if (options.Count == 0 || options.ContainsKey("help"))
            {
                PrintUsage();
                return options.ContainsKey("help") ? 0 : 2;
            }

            if (!options.TryGetValue("trades", out var tradesFile))
            {
                Console.Error.WriteLine("MonteCarlo: --trades <file> is required.");
                PrintUsage();
                return 2;
            }
            if (!File.Exists(tradesFile))
            {
                Console.Error.WriteLine($"MonteCarlo: trades file not found: {tradesFile}");
                return 2;
            }

            var initialCapital = GetDouble(options, "initial-capital", 100000.0);
            var profitColumn = options.GetValueOrDefault("profit-column");

            var series = TradeSeries.LoadCsv(tradesFile, initialCapital, profitColumn);

            var settings = new MonteCarloSettings
            {
                Method = Resamplers.Parse(options.GetValueOrDefault("method", "bootstrap")),
                Iterations = GetInt(options, "iterations", 2000),
                Seed = GetULong(options, "seed", 42),
                BlockSize = GetInt(options, "block-size", 0),
                RuinLevel = GetDouble(options, "ruin-level", 0.5),
                VarConfidence = GetDouble(options, "var-confidence", 0.95),
                HistogramBins = GetInt(options, "bins", 20)
            };

            var result = MonteCarloEngine.Run(series, settings);

            var outDir = options.GetValueOrDefault("out-dir", Directory.GetCurrentDirectory());
            Directory.CreateDirectory(outDir);
            var prefix = options.GetValueOrDefault("prefix", "montecarlo_" + settings.Method.ToString().ToLowerInvariant());

            var csvPath = Path.Combine(outDir, prefix + "_simulations.csv");
            var jsonPath = Path.Combine(outDir, prefix + "_summary.json");
            Report.WriteSimulationsCsv(result, csvPath);
            Report.WriteSummaryJson(result, jsonPath, tradesFile);

            Console.Write(Report.ConsoleSummary(result));
            Console.WriteLine($"wrote {csvPath}");
            Console.WriteLine($"wrote {jsonPath}");

            if (options.ContainsKey("html"))
            {
                var htmlPath = Path.Combine(outDir, prefix + ".html");
                Report.WriteHtml(result, htmlPath, tradesFile);
                Console.WriteLine($"wrote {htmlPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"MonteCarlo: {ex.Message}");
            return 1;
        }
    }

    private static Dictionary<string, string> ParseArgs(string[] args)
    {
        var flags = new HashSet<string> { "html", "help" };
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (!a.StartsWith("--", StringComparison.Ordinal))
            {
                throw new ArgumentException($"Unexpected argument '{a}'. Options start with --.");
            }
            var key = a[2..];
            if (flags.Contains(key))
            {
                options[key] = "true";
            }
            else
            {
                if (i + 1 >= args.Length)
                {
                    throw new ArgumentException($"Option --{key} needs a value.");
                }
                options[key] = args[++i];
            }
        }
        return options;
    }

    private static double GetDouble(Dictionary<string, string> o, string key, double fallback)
        => o.TryGetValue(key, out var v) ? double.Parse(v, NumberStyles.Any, CultureInfo.InvariantCulture) : fallback;

    private static int GetInt(Dictionary<string, string> o, string key, int fallback)
        => o.TryGetValue(key, out var v) ? int.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static ulong GetULong(Dictionary<string, string> o, string key, ulong fallback)
        => o.TryGetValue(key, out var v) ? ulong.Parse(v, CultureInfo.InvariantCulture) : fallback;

    private static void PrintUsage()
    {
        Console.WriteLine(
@"QuantroLean Monte Carlo - resamples a strategy's closed-trade profits.

Usage:
  MonteCarlo --trades <file.csv> [options]

The trades file is a CSV with one closed trade per row and a profit column (in account currency).
The column is taken by --profit-column, or the first of profit/pnl/netprofit/profit_loss. A file
without a header is read as one profit per line.

Options:
  --trades <file>          trades CSV (required)
  --profit-column <name>   name of the profit column (default: auto-detect)
  --initial-capital <n>    starting equity (default: 100000)
  --method <m>             reshuffle | bootstrap | block | stationary (default: bootstrap)
  --iterations <n>         number of simulations (default: 2000)
  --seed <n>               random seed; same seed = identical result (default: 42)
  --block-size <n>         block length for block/stationary (default: round(sqrt(trades)))
  --ruin-level <f>         fraction of initial capital whose loss counts as ruin (default: 0.5)
  --var-confidence <f>     confidence level of VaR/CVaR (default: 0.95)
  --bins <n>               histogram bins (default: 20)
  --out-dir <dir>          output directory (default: current)
  --prefix <name>          output file prefix (default: montecarlo_<method>)
  --html                   also write an HTML report
  --help                   this text

Outputs: <prefix>_simulations.csv, <prefix>_summary.json, and with --html <prefix>.html.");
    }
}

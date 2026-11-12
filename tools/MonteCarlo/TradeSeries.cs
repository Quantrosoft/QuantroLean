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

Independent implementation. The metrics are computed from their textbook definitions.
*/

using System.Globalization;

namespace QuantroLean.MonteCarlo;

/// <summary>
/// The result metrics of one equity path: all five that the Monte Carlo reports, plus the drawdown in percent.
/// Drawdown is the equity drawdown (peak to trough of the running equity), per the house rule.
/// </summary>
public readonly record struct Metrics(
    double NetProfit,
    double MaxDrawdown,
    double MaxDrawdownPercent,
    double ProfitFactor,
    double SharpeRatio,
    double FinalEquity)
{
    /// <summary>The metric names reported as distributions, in output order.</summary>
    public static readonly string[] Names =
        ["NetProfit", "MaxDrawdown", "MaxDrawdownPercent", "ProfitFactor", "SharpeRatio", "FinalEquity"];

    /// <summary>Returns a metric by its name (see <see cref="Names"/>).</summary>
    public double this[string name] => name switch
    {
        "NetProfit" => NetProfit,
        "MaxDrawdown" => MaxDrawdown,
        "MaxDrawdownPercent" => MaxDrawdownPercent,
        "ProfitFactor" => ProfitFactor,
        "SharpeRatio" => SharpeRatio,
        "FinalEquity" => FinalEquity,
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown metric.")
    };
}

/// <summary>
/// A strategy's closed-trade profits (in account currency) and the starting capital. This is the input to the
/// Monte Carlo: the real run's profits, in trade order.
/// </summary>
public sealed class TradeSeries
{
    /// <summary>The closed-trade profits, in the original order.</summary>
    public double[] Profits { get; }

    /// <summary>The starting equity before the first trade.</summary>
    public double InitialCapital { get; }

    public TradeSeries(double[] profits, double initialCapital)
    {
        Profits = profits ?? throw new ArgumentNullException(nameof(profits));
        if (profits.Length == 0)
        {
            throw new ArgumentException("The trade series is empty; Monte Carlo needs at least one closed trade.", nameof(profits));
        }
        InitialCapital = initialCapital;
    }

    /// <summary>Number of closed trades.</summary>
    public int Count => Profits.Length;

    /// <summary>
    /// Builds the equity path of a profit sequence: element 0 is the starting capital, element i+1 is the
    /// equity after the (i+1)-th trade. Length is <c>profits.Length + 1</c>.
    /// </summary>
    public static double[] EquityPath(double[] profits, double initialCapital)
    {
        var path = new double[profits.Length + 1];
        path[0] = initialCapital;
        var equity = initialCapital;
        for (var i = 0; i < profits.Length; i++)
        {
            equity += profits[i];
            path[i + 1] = equity;
        }
        return path;
    }

    /// <summary>
    /// Computes the result metrics of a profit sequence against the starting capital.
    /// </summary>
    public static Metrics ComputeMetrics(double[] profits, double initialCapital)
    {
        double net = 0.0, grossProfit = 0.0, grossLoss = 0.0;
        foreach (var p in profits)
        {
            net += p;
            if (p > 0) grossProfit += p;
            else if (p < 0) grossLoss -= p; // make positive
        }

        // Equity drawdown: largest peak-to-trough fall of the running equity (money and percent of the peak).
        var equity = initialCapital;
        var peak = initialCapital;
        double maxDd = 0.0, maxDdPct = 0.0;
        foreach (var p in profits)
        {
            equity += p;
            if (equity > peak) peak = equity;
            var dd = peak - equity;
            if (dd > maxDd) maxDd = dd;
            if (peak > 0)
            {
                var ddPct = dd / peak * 100.0;
                if (ddPct > maxDdPct) maxDdPct = ddPct;
            }
        }

        // Profit factor: gross profit over gross loss. No losses -> infinite (flagged, not a number trick).
        double profitFactor;
        if (grossLoss > 0) profitFactor = grossProfit / grossLoss;
        else profitFactor = grossProfit > 0 ? double.PositiveInfinity : 0.0;

        // Sharpe ratio of the per-trade profits (mean / sample standard deviation), not annualized.
        var n = profits.Length;
        var mean = net / n;
        double sharpe;
        if (n > 1)
        {
            double sumSq = 0.0;
            foreach (var p in profits)
            {
                var d = p - mean;
                sumSq += d * d;
            }
            var std = Math.Sqrt(sumSq / (n - 1));
            sharpe = std > 0 ? mean / std : 0.0;
        }
        else
        {
            sharpe = 0.0;
        }

        return new Metrics(net, maxDd, maxDdPct, profitFactor, sharpe, initialCapital + net);
    }

    /// <summary>
    /// Loads a trade series from a CSV file. The profit column is taken by name (<paramref name="profitColumn"/>);
    /// if that is not given, the first of profit / pnl / netprofit / profit_loss / profitloss (case-insensitive)
    /// is used. A file without a header is read as one profit per line.
    /// </summary>
    public static TradeSeries LoadCsv(string path, double initialCapital, string? profitColumn)
    {
        var lines = File.ReadAllLines(path);
        if (lines.Length == 0)
        {
            throw new ArgumentException($"'{path}' is empty.");
        }

        var profits = new List<double>();
        var header = SplitCsv(lines[0]);
        var looksLikeHeader = header.Any(h => !double.TryParse(h, NumberStyles.Any, CultureInfo.InvariantCulture, out _));

        if (!looksLikeHeader)
        {
            // Headerless: one profit per line, first column.
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                profits.Add(ParseProfit(SplitCsv(line)[0], path));
            }
            return new TradeSeries(profits.ToArray(), initialCapital);
        }

        var candidates = profitColumn != null
            ? new[] { profitColumn }
            : new[] { "profit", "pnl", "netprofit", "net_profit", "profit_loss", "profitloss" };

        var col = -1;
        foreach (var candidate in candidates)
        {
            col = Array.FindIndex(header, h => string.Equals(h.Trim(), candidate, StringComparison.OrdinalIgnoreCase));
            if (col >= 0) break;
        }
        if (col < 0)
        {
            throw new ArgumentException(
                $"'{path}' has no profit column (looked for {string.Join(", ", candidates)}). Header: {string.Join(", ", header)}.");
        }

        for (var i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            var fields = SplitCsv(lines[i]);
            if (col >= fields.Length) continue;
            profits.Add(ParseProfit(fields[col], path));
        }
        return new TradeSeries(profits.ToArray(), initialCapital);
    }

    private static double ParseProfit(string field, string path)
    {
        if (!double.TryParse(field.Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out var value))
        {
            throw new ArgumentException($"'{path}': cannot parse profit value '{field}'.");
        }
        return value;
    }

    // Minimal RFC-4180 CSV splitter (handles quoted fields with commas and doubled quotes).
    private static string[] SplitCsv(string line)
    {
        var fields = new List<string>();
        var sb = new System.Text.StringBuilder();
        var inQuotes = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                    else inQuotes = false;
                }
                else sb.Append(c);
            }
            else
            {
                if (c == '"') inQuotes = true;
                else if (c == ',') { fields.Add(sb.ToString()); sb.Clear(); }
                else sb.Append(c);
            }
        }
        fields.Add(sb.ToString());
        return fields.ToArray();
    }
}

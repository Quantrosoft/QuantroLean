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
using System.Text;

namespace MyLean.MonteCarlo;

/// <summary>
/// Writes a Monte Carlo result as a per-simulation CSV, a summary JSON, an optional HTML page, and a console
/// summary. Numbers use the invariant culture and the round-trip format, so the same result is written byte for
/// byte identically. No wall-clock time is written (it would break that and is not a data timestamp).
/// </summary>
public static class Report
{
    /// <summary>Formats a number invariantly and round-trippably; non-finite values become words.</summary>
    public static string Num(double v)
    {
        if (double.IsNaN(v)) return "NaN";
        if (double.IsPositiveInfinity(v)) return "Infinity";
        if (double.IsNegativeInfinity(v)) return "-Infinity";
        return v.ToString("R", CultureInfo.InvariantCulture);
    }

    /// <summary>A number for JSON: finite as a bare number, non-finite as a quoted string (JSON has no infinity).</summary>
    private static string Json(double v)
    {
        if (double.IsFinite(v)) return v.ToString("R", CultureInfo.InvariantCulture);
        return "\"" + Num(v) + "\"";
    }

    private static string MethodName(ResampleMethod m) => m switch
    {
        ResampleMethod.Reshuffle => "reshuffle",
        ResampleMethod.Bootstrap => "bootstrap",
        ResampleMethod.Block => "block",
        ResampleMethod.Stationary => "stationary",
        _ => m.ToString().ToLowerInvariant()
    };

    public static void WriteSimulationsCsv(MonteCarloResult r, string path)
    {
        var sb = new StringBuilder();
        sb.Append("simulation,NetProfit,MaxDrawdown,MaxDrawdownPercent,ProfitFactor,SharpeRatio,FinalEquity\n");
        for (var i = 0; i < r.SimMetrics.Length; i++)
        {
            var m = r.SimMetrics[i];
            sb.Append(i.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(Num(m.NetProfit)).Append(',')
              .Append(Num(m.MaxDrawdown)).Append(',')
              .Append(Num(m.MaxDrawdownPercent)).Append(',')
              .Append(Num(m.ProfitFactor)).Append(',')
              .Append(Num(m.SharpeRatio)).Append(',')
              .Append(Num(m.FinalEquity)).Append('\n');
        }
        WriteAtomic(path, sb.ToString());
    }

    public static void WriteSummaryJson(MonteCarloResult r, string path, string tradesFile)
    {
        var s = r.Settings;
        var sb = new StringBuilder();
        sb.Append("{\n");
        sb.Append("  \"tradesFile\": ").Append(JsonString(Path.GetFileName(tradesFile))).Append(",\n");
        sb.Append("  \"tradeCount\": ").Append(r.TradeCount).Append(",\n");
        sb.Append("  \"initialCapital\": ").Append(Json(r.InitialCapital)).Append(",\n");
        sb.Append("  \"settings\": {\n");
        sb.Append("    \"method\": ").Append(JsonString(MethodName(s.Method))).Append(",\n");
        sb.Append("    \"iterations\": ").Append(s.Iterations).Append(",\n");
        sb.Append("    \"seed\": ").Append(s.Seed).Append(",\n");
        sb.Append("    \"blockSize\": ").Append(r.EffectiveBlockSize).Append(",\n");
        sb.Append("    \"ruinLevel\": ").Append(Json(s.RuinLevel)).Append(",\n");
        sb.Append("    \"varConfidence\": ").Append(Json(s.VarConfidence)).Append(",\n");
        sb.Append("    \"histogramBins\": ").Append(s.HistogramBins).Append("\n");
        sb.Append("  },\n");

        sb.Append("  \"distributions\": [\n");
        for (var i = 0; i < r.Distributions.Length; i++)
        {
            var d = r.Distributions[i];
            sb.Append("    {\n");
            sb.Append("      \"name\": ").Append(JsonString(d.Name)).Append(",\n");
            sb.Append("      \"mean\": ").Append(Json(d.Mean)).Append(",\n");
            sb.Append("      \"median\": ").Append(Json(d.Median)).Append(",\n");
            sb.Append("      \"min\": ").Append(Json(d.Min)).Append(",\n");
            sb.Append("      \"max\": ").Append(Json(d.Max)).Append(",\n");
            sb.Append("      \"p5\": ").Append(Json(d.P5)).Append(",\n");
            sb.Append("      \"p95\": ").Append(Json(d.P95)).Append(",\n");
            sb.Append("      \"real\": ").Append(Json(d.RealValue)).Append(",\n");
            sb.Append("      \"realPercentile\": ").Append(Json(d.RealPercentile)).Append(",\n");
            sb.Append("      \"histogram\": { \"edges\": [").Append(JoinNums(d.HistogramEdges))
              .Append("], \"counts\": [").Append(JoinInts(d.HistogramCounts)).Append("] }\n");
            sb.Append(i < r.Distributions.Length - 1 ? "    },\n" : "    }\n");
        }
        sb.Append("  ],\n");

        sb.Append("  \"risk\": {\n");
        sb.Append("    \"probabilityProfitable\": ").Append(Json(r.ProbabilityProfitable)).Append(",\n");
        sb.Append("    \"riskOfRuin\": ").Append(Json(r.RiskOfRuin)).Append(",\n");
        sb.Append("    \"ruinThreshold\": ").Append(Json(r.RuinThreshold)).Append(",\n");
        sb.Append("    \"valueAtRisk\": ").Append(Json(r.ValueAtRisk)).Append(",\n");
        sb.Append("    \"conditionalValueAtRisk\": ").Append(Json(r.ConditionalValueAtRisk)).Append("\n");
        sb.Append("  },\n");

        sb.Append("  \"equityBands\": [\n");
        for (var i = 0; i < r.EquityBands.Length; i++)
        {
            var b = r.EquityBands[i];
            sb.Append("    { \"tradeIndex\": ").Append(b.TradeIndex)
              .Append(", \"p5\": ").Append(Json(b.P5))
              .Append(", \"p50\": ").Append(Json(b.P50))
              .Append(", \"p95\": ").Append(Json(b.P95)).Append(" }");
            sb.Append(i < r.EquityBands.Length - 1 ? ",\n" : "\n");
        }
        sb.Append("  ]\n");
        sb.Append("}\n");
        WriteAtomic(path, sb.ToString());
    }

    public static string ConsoleSummary(MonteCarloResult r)
    {
        var s = r.Settings;
        var sb = new StringBuilder();
        sb.Append($"Monte Carlo: method={MethodName(s.Method)} iterations={s.Iterations} seed={s.Seed} ")
          .Append($"blockSize={r.EffectiveBlockSize} trades={r.TradeCount} initialCapital={Num(r.InitialCapital)}\n");
        sb.Append(string.Format(CultureInfo.InvariantCulture,
            "{0,-20} {1,14} {2,14} {3,14} {4,14} {5,14} {6,10}\n",
            "metric", "real", "mean", "median", "p5", "p95", "pct"));
        foreach (var d in r.Distributions)
        {
            sb.Append(string.Format(CultureInfo.InvariantCulture,
                "{0,-20} {1,14} {2,14} {3,14} {4,14} {5,14} {6,9:P0}\n",
                d.Name, Short(d.RealValue), Short(d.Mean), Short(d.Median), Short(d.P5), Short(d.P95), d.RealPercentile));
        }
        sb.Append($"P(profitable)={r.ProbabilityProfitable.ToString("P1", CultureInfo.InvariantCulture)}  ")
          .Append($"RiskOfRuin={r.RiskOfRuin.ToString("P1", CultureInfo.InvariantCulture)} (<= {Num(r.RuinThreshold)})  ")
          .Append($"VaR{(int)(s.VarConfidence * 100)}={Num(r.ValueAtRisk)}  CVaR{(int)(s.VarConfidence * 100)}={Num(r.ConditionalValueAtRisk)}\n");
        return sb.ToString();
    }

    private static string Short(double v)
    {
        if (!double.IsFinite(v)) return Num(v);
        return v.ToString("0.####", CultureInfo.InvariantCulture);
    }

    public static void WriteHtml(MonteCarloResult r, string path, string tradesFile)
    {
        var s = r.Settings;
        var sb = new StringBuilder();
        sb.Append("<!doctype html>\n<html lang=\"en\"><head><meta charset=\"utf-8\">\n");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        sb.Append("<title>MyLean Monte Carlo</title>\n<style>\n");
        sb.Append("body{font-family:system-ui,Segoe UI,Arial,sans-serif;background:#0f1420;color:#e6e6e6;margin:0;padding:24px;line-height:1.45}\n");
        sb.Append("h1{font-size:20px;margin:0 0 4px}.sub{color:#9fb0d0;font-size:13px;margin-bottom:18px}\n");
        sb.Append("table{border-collapse:collapse;width:100%;font-size:13px;margin-bottom:22px}\n");
        sb.Append("th,td{border-bottom:1px solid #2a3350;padding:7px 10px;text-align:right}th:first-child,td:first-child{text-align:left}\n");
        sb.Append("thead th{background:#1a2238;color:#7aa2ff}h2{font-size:15px;margin:18px 0 8px}\n");
        sb.Append("svg{background:#141b2b;border:1px solid #2a3350;border-radius:8px}\n");
        sb.Append(".bar{fill:#7aa2ff}.real{stroke:#f7c948;stroke-width:2}\n");
        sb.Append("</style></head><body>\n");
        sb.Append("<h1>MyLean Monte Carlo</h1>\n");
        sb.Append("<div class=\"sub\">method <b>").Append(MethodName(s.Method)).Append("</b> &middot; ")
          .Append(s.Iterations).Append(" iterations &middot; seed ").Append(s.Seed)
          .Append(" &middot; block size ").Append(r.EffectiveBlockSize)
          .Append(" &middot; ").Append(r.TradeCount).Append(" trades &middot; initial capital ")
          .Append(Num(r.InitialCapital)).Append(" &middot; ").Append(Html(Path.GetFileName(tradesFile))).Append("</div>\n");

        // Metrics table.
        sb.Append("<table><thead><tr><th>metric</th><th>real</th><th>mean</th><th>median</th><th>min</th><th>max</th><th>P5</th><th>P95</th><th>percentile</th></tr></thead><tbody>\n");
        foreach (var d in r.Distributions)
        {
            sb.Append("<tr><td>").Append(d.Name).Append("</td>")
              .Append("<td>").Append(Short(d.RealValue)).Append("</td>")
              .Append("<td>").Append(Short(d.Mean)).Append("</td>")
              .Append("<td>").Append(Short(d.Median)).Append("</td>")
              .Append("<td>").Append(Short(d.Min)).Append("</td>")
              .Append("<td>").Append(Short(d.Max)).Append("</td>")
              .Append("<td>").Append(Short(d.P5)).Append("</td>")
              .Append("<td>").Append(Short(d.P95)).Append("</td>")
              .Append("<td>").Append(d.RealPercentile.ToString("P0", CultureInfo.InvariantCulture)).Append("</td></tr>\n");
        }
        sb.Append("</tbody></table>\n");

        // Risk table.
        sb.Append("<table><thead><tr><th>risk measure</th><th>value</th></tr></thead><tbody>\n");
        sb.Append(RiskRow("P(profitable)", r.ProbabilityProfitable.ToString("P1", CultureInfo.InvariantCulture)));
        sb.Append(RiskRow($"Risk of ruin (equity &le; {Num(r.RuinThreshold)})", r.RiskOfRuin.ToString("P1", CultureInfo.InvariantCulture)));
        sb.Append(RiskRow($"Value at risk ({(int)(s.VarConfidence * 100)}%)", Num(r.ValueAtRisk)));
        sb.Append(RiskRow($"Conditional VaR ({(int)(s.VarConfidence * 100)}%)", Num(r.ConditionalValueAtRisk)));
        sb.Append("</tbody></table>\n");

        // Histograms.
        foreach (var d in r.Distributions)
        {
            sb.Append("<h2>").Append(d.Name).Append("</h2>\n");
            sb.Append(HistogramSvg(d));
        }

        // Equity bands.
        sb.Append("<h2>Equity confidence bands (P5 / P50 / P95)</h2>\n");
        sb.Append(EquityBandSvg(r));

        sb.Append("</body></html>\n");
        WriteAtomic(path, sb.ToString());
    }

    private static string RiskRow(string label, string value)
        => $"<tr><td>{label}</td><td>{value}</td></tr>\n";

    private static string HistogramSvg(MetricDistribution d)
    {
        const int w = 720, h = 160, pad = 24;
        var bins = d.HistogramCounts.Length;
        var maxCount = 0;
        foreach (var c in d.HistogramCounts) if (c > maxCount) maxCount = c;
        if (maxCount == 0) maxCount = 1;

        var sb = new StringBuilder();
        sb.Append($"<svg width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">\n");
        var plotW = w - 2 * pad;
        var plotH = h - 2 * pad;
        var barW = (double)plotW / bins;
        for (var i = 0; i < bins; i++)
        {
            var bh = plotH * d.HistogramCounts[i] / maxCount;
            var x = pad + i * barW;
            var y = pad + plotH - bh;
            sb.Append($"<rect class=\"bar\" x=\"{Svg(x)}\" y=\"{Svg(y)}\" width=\"{Svg(barW - 1)}\" height=\"{Svg(bh)}\"/>\n");
        }
        // Real-run marker.
        if (double.IsFinite(d.RealValue) && double.IsFinite(d.Min) && double.IsFinite(d.Max) && d.Max > d.Min)
        {
            var rx = pad + plotW * (d.RealValue - d.Min) / (d.Max - d.Min);
            rx = Math.Clamp(rx, pad, pad + plotW);
            sb.Append($"<line class=\"real\" x1=\"{Svg(rx)}\" y1=\"{pad}\" x2=\"{Svg(rx)}\" y2=\"{pad + plotH}\"/>\n");
        }
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string EquityBandSvg(MonteCarloResult r)
    {
        const int w = 720, h = 220, pad = 30;
        var bands = r.EquityBands;
        var n = bands.Length;
        double lo = double.PositiveInfinity, hi = double.NegativeInfinity;
        foreach (var b in bands)
        {
            if (double.IsFinite(b.P5)) { lo = Math.Min(lo, b.P5); hi = Math.Max(hi, b.P5); }
            if (double.IsFinite(b.P95)) { lo = Math.Min(lo, b.P95); hi = Math.Max(hi, b.P95); }
        }
        if (!double.IsFinite(lo) || !double.IsFinite(hi) || hi <= lo) { lo = 0; hi = 1; }

        var plotW = w - 2 * pad;
        var plotH = h - 2 * pad;
        double X(int i) => pad + (n <= 1 ? 0 : (double)plotW * i / (n - 1));
        double Y(double v) => pad + plotH - plotH * (v - lo) / (hi - lo);

        var p95 = new StringBuilder();
        var p50 = new StringBuilder();
        var p5 = new StringBuilder();
        for (var i = 0; i < n; i++)
        {
            p95.Append(Svg(X(i))).Append(',').Append(Svg(Y(bands[i].P95))).Append(' ');
            p50.Append(Svg(X(i))).Append(',').Append(Svg(Y(bands[i].P50))).Append(' ');
            p5.Append(Svg(X(i))).Append(',').Append(Svg(Y(bands[i].P5))).Append(' ');
        }

        var sb = new StringBuilder();
        sb.Append($"<svg width=\"{w}\" height=\"{h}\" viewBox=\"0 0 {w} {h}\">\n");
        sb.Append("<polyline fill=\"none\" stroke=\"#4ade80\" stroke-width=\"1.5\" points=\"").Append(p95).Append("\"/>\n");
        sb.Append("<polyline fill=\"none\" stroke=\"#7aa2ff\" stroke-width=\"2\" points=\"").Append(p50).Append("\"/>\n");
        sb.Append("<polyline fill=\"none\" stroke=\"#ff6b6b\" stroke-width=\"1.5\" points=\"").Append(p5).Append("\"/>\n");
        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static string Svg(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string JoinNums(double[] values)
        => string.Join(", ", values.Select(Json));

    private static string JoinInts(int[] values)
        => string.Join(", ", values.Select(v => v.ToString(CultureInfo.InvariantCulture)));

    private static string JsonString(string s)
        => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private static string Html(string s)
        => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static void WriteAtomic(string path, string content)
    {
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }
}

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

Independent implementation. The statistics are computed from their textbook definitions.
*/

namespace QuantroLean.MonteCarlo;

/// <summary>Summary statistics of one metric's distribution over all simulations.</summary>
public sealed class MetricDistribution
{
    public required string Name { get; init; }
    public double Mean { get; init; }
    public double Median { get; init; }
    public double Min { get; init; }
    public double Max { get; init; }
    public double P5 { get; init; }
    public double P95 { get; init; }

    /// <summary>The real run's value of this metric.</summary>
    public double RealValue { get; init; }

    /// <summary>Fraction of simulations with a value at or below the real run's value (the backtesting percentile).</summary>
    public double RealPercentile { get; init; }

    /// <summary>Histogram bin edges (length = bins + 1) and counts (length = bins).</summary>
    public required double[] HistogramEdges { get; init; }
    public required int[] HistogramCounts { get; init; }
}

/// <summary>Textbook statistics helpers.</summary>
public static class Statistics
{
    /// <summary>
    /// Linear-interpolation percentile (the "type 7" definition, as used by NumPy's default) of an
    /// ascending-sorted array. <paramref name="p"/> is in [0, 100]. Infinities are passed through without
    /// arithmetic so a tail percentile stays infinite rather than becoming NaN.
    /// </summary>
    public static double Percentile(double[] sortedAscending, double p)
    {
        var n = sortedAscending.Length;
        if (n == 0) return double.NaN;
        if (n == 1) return sortedAscending[0];

        var rank = p / 100.0 * (n - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        var frac = rank - lo;
        if (frac == 0.0 || sortedAscending[lo] == sortedAscending[hi])
        {
            return sortedAscending[lo];
        }
        return sortedAscending[lo] + (sortedAscending[hi] - sortedAscending[lo]) * frac;
    }

    /// <summary>Arithmetic mean (may be infinite if the data contains an infinity, e.g. an unbeaten profit factor).</summary>
    public static double Mean(double[] values)
    {
        if (values.Length == 0) return double.NaN;
        double sum = 0.0;
        foreach (var v in values) sum += v;
        return sum / values.Length;
    }

    /// <summary>
    /// Fraction of <paramref name="values"/> that are at or below <paramref name="threshold"/>.
    /// With the sorted array this is the empirical cumulative distribution at the threshold.
    /// </summary>
    public static double FractionAtOrBelow(double[] sortedAscending, double threshold)
    {
        // Upper bound: first index strictly greater than threshold == count of values <= threshold.
        var lo = 0;
        var hi = sortedAscending.Length;
        while (lo < hi)
        {
            var mid = (lo + hi) >> 1;
            if (sortedAscending[mid] <= threshold) lo = mid + 1;
            else hi = mid;
        }
        return (double)lo / sortedAscending.Length;
    }

    /// <summary>
    /// Builds a histogram with <paramref name="bins"/> equal-width bins between the finite min and max of the data.
    /// Non-finite values (e.g. an infinite profit factor) are counted in the nearest edge bin.
    /// </summary>
    public static (double[] edges, int[] counts) Histogram(double[] values, int bins)
    {
        double min = double.PositiveInfinity, max = double.NegativeInfinity;
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            if (double.IsFinite(v))
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            // No finite data: a single degenerate bin.
            return ([0.0, 1.0], [values.Length]);
        }
        if (min == max)
        {
            max = min + 1.0; // avoid a zero-width range
        }

        var edges = new double[bins + 1];
        var width = (max - min) / bins;
        for (var i = 0; i <= bins; i++) edges[i] = min + width * i;

        var counts = new int[bins];
        foreach (var v in values)
        {
            if (double.IsNaN(v)) continue;
            int bin;
            if (v <= min) bin = 0;
            else if (v >= max) bin = bins - 1;
            else bin = Math.Min(bins - 1, (int)((v - min) / width));
            counts[bin]++;
        }
        return (edges, counts);
    }
}

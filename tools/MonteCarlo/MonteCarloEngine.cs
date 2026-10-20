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

Independent implementation. The risk measures (probability of profit, risk of ruin, value at risk,
conditional value at risk, equity confidence bands) follow their textbook definitions.
*/

namespace MyLean.MonteCarlo;

/// <summary>The settings of a Monte Carlo run.</summary>
public sealed class MonteCarloSettings
{
    public ResampleMethod Method { get; init; } = ResampleMethod.Bootstrap;
    public int Iterations { get; init; } = 2000;
    public ulong Seed { get; init; } = 42;
    public int BlockSize { get; init; }
    public double RuinLevel { get; init; } = 0.5;
    public int HistogramBins { get; init; } = 20;

    /// <summary>The confidence level of VaR / CVaR (the tail is 1 - this). Default 0.95.</summary>
    public double VarConfidence { get; init; } = 0.95;
}

/// <summary>One point of the equity confidence bands: the P5 / P50 / P95 of the simulated equity after trade <see cref="TradeIndex"/>.</summary>
public readonly record struct EquityBand(int TradeIndex, double P5, double P50, double P95);

/// <summary>The full result of a Monte Carlo run.</summary>
public sealed class MonteCarloResult
{
    public required MonteCarloSettings Settings { get; init; }
    public required int TradeCount { get; init; }
    public required double InitialCapital { get; init; }
    public required int EffectiveBlockSize { get; init; }

    public required Metrics RealMetrics { get; init; }
    public required Metrics[] SimMetrics { get; init; }
    public required MetricDistribution[] Distributions { get; init; }

    public required double ProbabilityProfitable { get; init; }
    public required double RiskOfRuin { get; init; }
    public required double RuinThreshold { get; init; }
    public required double ValueAtRisk { get; init; }
    public required double ConditionalValueAtRisk { get; init; }

    public required EquityBand[] EquityBands { get; init; }

    public MetricDistribution Distribution(string name) => Distributions.First(d => d.Name == name);
}

/// <summary>Runs a Monte Carlo over a strategy's closed-trade profits.</summary>
public static class MonteCarloEngine
{
    /// <summary>
    /// A sensible default block size when none is given: the nearest integer to the square root of the trade count.
    /// </summary>
    public static int DefaultBlockSize(int tradeCount) => Math.Max(1, (int)Math.Round(Math.Sqrt(tradeCount)));

    public static MonteCarloResult Run(TradeSeries series, MonteCarloSettings settings)
    {
        if (settings.Iterations <= 0)
        {
            throw new ArgumentException("Iterations must be positive.", nameof(settings));
        }

        var n = series.Count;
        var initialCapital = series.InitialCapital;
        var blockSize = settings.BlockSize > 0 ? settings.BlockSize : DefaultBlockSize(n);

        var realMetrics = TradeSeries.ComputeMetrics(series.Profits, initialCapital);

        var rng = new DeterministicRandom(settings.Seed);
        var iterations = settings.Iterations;
        var simMetrics = new Metrics[iterations];
        var paths = new double[iterations][];
        var minEquity = new double[iterations];

        for (var it = 0; it < iterations; it++)
        {
            var resampled = Resamplers.Resample(settings.Method, series.Profits, rng, blockSize);
            var path = TradeSeries.EquityPath(resampled, initialCapital);
            simMetrics[it] = TradeSeries.ComputeMetrics(resampled, initialCapital);
            paths[it] = path;

            var mn = path[0];
            for (var t = 1; t < path.Length; t++)
            {
                if (path[t] < mn) mn = path[t];
            }
            minEquity[it] = mn;
        }

        return Summarize(settings, n, initialCapital, blockSize, realMetrics, simMetrics, minEquity, paths);
    }

    /// <summary>
    /// Turns the per-simulation metrics into the full result: per-metric distributions, the net-profit based risk
    /// measures and (when equity paths are given) the equity confidence bands. This is shared by the trade
    /// resampling above and by the synthetic price path method, which produces its metrics from re-run backtests.
    /// </summary>
    /// <param name="minEquity">the lowest equity each simulation reached (for the risk of ruin)</param>
    /// <param name="paths">one equity path per simulation, all of length <c>tradeCount + 1</c>, or null to omit the bands</param>
    public static MonteCarloResult Summarize(MonteCarloSettings settings, int tradeCount, double initialCapital,
        int blockSize, Metrics realMetrics, Metrics[] simMetrics, double[] minEquity, double[][]? paths)
    {
        var iterations = simMetrics.Length;

        // Per-metric distributions.
        var distributions = new MetricDistribution[Metrics.Names.Length];
        for (var m = 0; m < Metrics.Names.Length; m++)
        {
            var name = Metrics.Names[m];
            var values = new double[iterations];
            for (var it = 0; it < iterations; it++) values[it] = simMetrics[it][name];
            var sorted = (double[])values.Clone();
            Array.Sort(sorted);

            var (edges, counts) = Statistics.Histogram(values, settings.HistogramBins);
            distributions[m] = new MetricDistribution
            {
                Name = name,
                Mean = Statistics.Mean(values),
                Median = Statistics.Percentile(sorted, 50),
                Min = sorted[0],
                Max = sorted[^1],
                P5 = Statistics.Percentile(sorted, 5),
                P95 = Statistics.Percentile(sorted, 95),
                RealValue = realMetrics[name],
                RealPercentile = Statistics.FractionAtOrBelow(sorted, realMetrics[name]),
                HistogramEdges = edges,
                HistogramCounts = counts
            };
        }

        // Net-profit based risk measures.
        var netProfits = new double[iterations];
        for (var it = 0; it < iterations; it++) netProfits[it] = simMetrics[it].NetProfit;
        var sortedNet = (double[])netProfits.Clone();
        Array.Sort(sortedNet);

        var probProfitable = 1.0 - Statistics.FractionAtOrBelow(sortedNet, 0.0);

        var ruinThreshold = initialCapital * (1.0 - settings.RuinLevel);
        var ruined = 0;
        for (var it = 0; it < iterations; it++)
        {
            if (minEquity[it] <= ruinThreshold) ruined++;
        }
        var riskOfRuin = (double)ruined / iterations;

        // VaR / CVaR at the configured confidence on the net-profit distribution; reported as positive losses.
        var tailProb = 1.0 - settings.VarConfidence;             // e.g. 0.05
        var valueAtRisk = -Statistics.Percentile(sortedNet, tailProb * 100.0);
        var tailCount = Math.Max(1, (int)Math.Ceiling(tailProb * iterations));
        double tailSum = 0.0;
        for (var i = 0; i < tailCount; i++) tailSum += sortedNet[i];
        var conditionalValueAtRisk = -(tailSum / tailCount);

        // Equity confidence bands over the trade index (only when every simulation has an aligned equity path).
        EquityBand[] bands;
        if (paths != null)
        {
            var pathLength = tradeCount + 1;
            bands = new EquityBand[pathLength];
            var column = new double[iterations];
            for (var t = 0; t < pathLength; t++)
            {
                for (var it = 0; it < iterations; it++) column[it] = paths[it][t];
                Array.Sort(column);
                bands[t] = new EquityBand(
                    t,
                    Statistics.Percentile(column, 5),
                    Statistics.Percentile(column, 50),
                    Statistics.Percentile(column, 95));
            }
        }
        else
        {
            bands = [];
        }

        return new MonteCarloResult
        {
            Settings = settings,
            TradeCount = tradeCount,
            InitialCapital = initialCapital,
            EffectiveBlockSize = blockSize,
            RealMetrics = realMetrics,
            SimMetrics = simMetrics,
            Distributions = distributions,
            ProbabilityProfitable = probProfitable,
            RiskOfRuin = riskOfRuin,
            RuinThreshold = ruinThreshold,
            ValueAtRisk = valueAtRisk,
            ConditionalValueAtRisk = conditionalValueAtRisk,
            EquityBands = bands
        };
    }
}

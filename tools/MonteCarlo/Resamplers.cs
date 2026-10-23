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

Independent implementation of the resampling methods from their textbook definitions:
Fisher-Yates shuffle; the i.i.d. bootstrap (Efron 1979); the circular block bootstrap;
and the stationary bootstrap (Politis and Romano 1994). No code is taken from elsewhere.
*/

namespace MyLean.MonteCarlo;

/// <summary>The resampling method applied to the closed-trade profits.</summary>
public enum ResampleMethod
{
    /// <summary>A random permutation of the trades (no replacement): same trades, new order.</summary>
    Reshuffle,

    /// <summary>The i.i.d. bootstrap: draw n trades with replacement.</summary>
    Bootstrap,

    /// <summary>The circular block bootstrap: draw fixed-length contiguous blocks (wrapping around) with replacement.</summary>
    Block,

    /// <summary>The stationary bootstrap: blocks of random, geometrically distributed length (mean = block size).</summary>
    Stationary
}

/// <summary>
/// Produces one resampled profit sequence of the same length as the input, by the chosen method.
/// </summary>
public static class Resamplers
{
    /// <summary>
    /// Parses a method name (case-insensitive): reshuffle, bootstrap, block, stationary.
    /// </summary>
    public static ResampleMethod Parse(string name) => name.Trim().ToLowerInvariant() switch
    {
        "reshuffle" => ResampleMethod.Reshuffle,
        "bootstrap" => ResampleMethod.Bootstrap,
        "block" or "blockbootstrap" or "block-bootstrap" => ResampleMethod.Block,
        "stationary" or "stationarybootstrap" or "stationary-bootstrap" => ResampleMethod.Stationary,
        _ => throw new ArgumentException($"Unknown method '{name}'. Use reshuffle, bootstrap, block or stationary.")
    };

    /// <summary>
    /// Returns a resampled copy of <paramref name="profits"/>. <paramref name="blockSize"/> is used by the block and
    /// stationary methods (the mean block length for the stationary one).
    /// </summary>
    public static double[] Resample(ResampleMethod method, double[] profits, DeterministicRandom rng, int blockSize)
    {
        return method switch
        {
            ResampleMethod.Reshuffle => Reshuffle(profits, rng),
            ResampleMethod.Bootstrap => Bootstrap(profits, rng),
            ResampleMethod.Block => BlockBootstrap(profits, rng, blockSize),
            ResampleMethod.Stationary => StationaryBootstrap(profits, rng, blockSize),
            _ => throw new ArgumentOutOfRangeException(nameof(method))
        };
    }

    private static double[] Reshuffle(double[] profits, DeterministicRandom rng)
    {
        var n = profits.Length;
        var copy = (double[])profits.Clone();
        // Fisher-Yates: a uniform random permutation.
        for (var i = n - 1; i > 0; i--)
        {
            var j = rng.NextInt(i + 1);
            (copy[i], copy[j]) = (copy[j], copy[i]);
        }
        return copy;
    }

    private static double[] Bootstrap(double[] profits, DeterministicRandom rng)
    {
        var n = profits.Length;
        var sample = new double[n];
        for (var i = 0; i < n; i++)
        {
            sample[i] = profits[rng.NextInt(n)];
        }
        return sample;
    }

    private static double[] BlockBootstrap(double[] profits, DeterministicRandom rng, int blockSize)
    {
        var n = profits.Length;
        var b = Math.Clamp(blockSize, 1, n);
        var sample = new double[n];
        var pos = 0;
        while (pos < n)
        {
            var start = rng.NextInt(n);
            for (var k = 0; k < b && pos < n; k++)
            {
                sample[pos++] = profits[(start + k) % n];
            }
        }
        return sample;
    }

    private static double[] StationaryBootstrap(double[] profits, DeterministicRandom rng, int meanBlock)
    {
        var n = profits.Length;
        var mean = Math.Clamp(meanBlock, 1, n);
        // Probability of starting a new block after each element; geometric block length with the given mean.
        var p = 1.0 / mean;
        var sample = new double[n];
        var idx = rng.NextInt(n);
        for (var i = 0; i < n; i++)
        {
            sample[i] = profits[idx];
            if (rng.NextDouble() < p)
            {
                idx = rng.NextInt(n);          // start a new block at a random place
            }
            else
            {
                idx = (idx + 1) % n;           // continue the current block (wrapping around)
            }
        }
        return sample;
    }
}

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

Independent implementation of the public-domain xoshiro256** generator and the
SplitMix64 seeder (David Blackman and Sebastiano Vigna; see https://prng.di.unimi.it/).
A fixed generator is used on purpose so that a given seed yields byte-identical
results on any machine and .NET version, which the platform Random does not guarantee.
*/

namespace MyLean.MonteCarlo;

/// <summary>
/// Deterministic pseudo-random generator (xoshiro256**), seeded through SplitMix64. The same seed produces the
/// same sequence everywhere, so a Monte Carlo run with a fixed seed is byte-for-byte reproducible.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    /// <summary>
    /// Creates a generator for the given seed.
    /// </summary>
    public DeterministicRandom(ulong seed)
    {
        // SplitMix64 expands the single seed into the four state words.
        _s0 = SplitMix64(ref seed);
        _s1 = SplitMix64(ref seed);
        _s2 = SplitMix64(ref seed);
        _s3 = SplitMix64(ref seed);

        // xoshiro must not start from an all-zero state.
        if ((_s0 | _s1 | _s2 | _s3) == 0)
        {
            _s0 = 0x9E3779B97F4A7C15UL;
        }
    }

    private static ulong SplitMix64(ref ulong x)
    {
        unchecked
        {
            x += 0x9E3779B97F4A7C15UL;
            var z = x;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            return z ^ (z >> 31);
        }
    }

    private static ulong RotateLeft(ulong x, int k) => (x << k) | (x >> (64 - k));

    /// <summary>
    /// Returns the next 64-bit value of the sequence.
    /// </summary>
    public ulong NextULong()
    {
        unchecked
        {
            var result = RotateLeft(_s1 * 5UL, 7) * 9UL;
            var t = _s1 << 17;
            _s2 ^= _s0;
            _s3 ^= _s1;
            _s1 ^= _s2;
            _s0 ^= _s3;
            _s2 ^= t;
            _s3 = RotateLeft(_s3, 45);
            return result;
        }
    }

    /// <summary>
    /// Returns a double in [0, 1) from the top 53 bits (one ulong per call).
    /// </summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / 9007199254740992.0); // 2^53

    /// <summary>
    /// Returns an integer in [0, count) without modulo bias (Lemire's rejection on 2^64 mod count).
    /// </summary>
    public int NextInt(int count)
    {
        if (count <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), "count must be positive.");
        }
        var n = (ulong)count;
        var threshold = (0UL - n) % n; // 2^64 mod n
        ulong r;
        do
        {
            r = NextULong();
        }
        while (r < threshold);
        return (int)(r % n);
    }

    /// <summary>
    /// Returns a standard-normal value (mean 0, standard deviation 1) by the Box-Muller transform.
    /// Used by the synthetic price path method.
    /// </summary>
    public double NextGaussian()
    {
        // u1 in (0, 1] so that Log is finite.
        var u1 = 1.0 - NextDouble();
        var u2 = NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }
}

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
using System.IO.Compression;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using LeanBar = QuantConnect.Data.Market.Bar;

namespace QuantroLean.MonteCarlo.Spp;

/// <summary>
/// Reads and writes Lean minute quote bars for one symbol, and builds synthetic bars by translating each original
/// bar's bid/ask to a synthetic mid level. Reading is a direct read of Lean's own zip/CSV; writing uses Lean's
/// <see cref="LeanDataWriter"/>, so the output is in Lean's exact on-disk format.
/// </summary>
public static class LeanQuoteData
{
    /// <summary>One minute quote bar: bid and ask OHLC and the last sizes, at time <see cref="MsOfDay"/>.</summary>
    public readonly record struct QuoteRow(
        int MsOfDay,
        double BidO, double BidH, double BidL, double BidC, double BidSize,
        double AskO, double AskH, double AskL, double AskC, double AskSize)
    {
        public double MidClose => (BidC + AskC) / 2.0;
    }

    /// <summary>
    /// Reads the minute quote bars of <paramref name="symbol"/> on <paramref name="dateUtc"/> from the Lean data
    /// folder. Expects the standard CFD/forex minute quote CSV: ms, bid O/H/L/C, bid size, ask O/H/L/C, ask size.
    /// </summary>
    public static List<QuoteRow> ReadMinuteQuotes(string dataFolder, Symbol symbol, DateTime dateUtc)
    {
        var dir = Path.Combine(dataFolder, symbol.SecurityType.ToString().ToLowerInvariant(),
            symbol.ID.Market.ToLowerInvariant(), "minute", symbol.Value.ToLowerInvariant());
        var zipPath = Path.Combine(dir, $"{dateUtc:yyyyMMdd}_quote.zip");
        if (!File.Exists(zipPath))
        {
            throw new FileNotFoundException($"minute quote file not found: {zipPath}");
        }

        var bars = new List<QuoteRow>();
        using var zip = ZipFile.OpenRead(zipPath);
        var entry = zip.Entries.Single();
        using var reader = new StreamReader(entry.Open());
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            var c = line.Split(',');
            // ms, bidO, bidH, bidL, bidC, bidSize, askO, askH, askL, askC, askSize
            double D(int i) => double.Parse(c[i], CultureInfo.InvariantCulture);
            bars.Add(new QuoteRow(int.Parse(c[0], CultureInfo.InvariantCulture),
                D(1), D(2), D(3), D(4), D(5), D(6), D(7), D(8), D(9), D(10)));
        }
        return bars;
    }

    /// <summary>
    /// Translates each bar's bid and ask by the gap between the synthetic and the original mid close, so the bar's
    /// mid close becomes <paramref name="synthMidClose"/>[i] while the bid/ask spread and the intrabar shape stay.
    /// </summary>
    public static List<QuoteRow> ApplyMidPath(List<QuoteRow> baseBars, double[] synthMidClose)
    {
        if (synthMidClose.Length != baseBars.Count)
        {
            throw new ArgumentException("synthetic mid path length must equal the number of bars.");
        }
        var result = new List<QuoteRow>(baseBars.Count);
        for (var i = 0; i < baseBars.Count; i++)
        {
            var b = baseBars[i];
            var shift = synthMidClose[i] - b.MidClose;
            result.Add(b with
            {
                BidO = b.BidO + shift,
                BidH = b.BidH + shift,
                BidL = b.BidL + shift,
                BidC = b.BidC + shift,
                AskO = b.AskO + shift,
                AskH = b.AskH + shift,
                AskL = b.AskL + shift,
                AskC = b.AskC + shift
            });
        }
        return result;
    }

    /// <summary>
    /// Writes the bars as Lean minute quote data for <paramref name="symbol"/> on <paramref name="dateUtc"/> into
    /// <paramref name="dataFolder"/>, using Lean's own writer (overwrites the day's file).
    /// </summary>
    public static void WriteMinuteQuotes(string dataFolder, Symbol symbol, DateTime dateUtc, List<QuoteRow> bars)
    {
        var midnight = new DateTime(dateUtc.Year, dateUtc.Month, dateUtc.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var period = TimeSpan.FromMinutes(1);
        var quoteBars = new List<BaseData>(bars.Count);
        foreach (var b in bars)
        {
            var time = midnight.AddMilliseconds(b.MsOfDay);
            quoteBars.Add(new QuoteBar(time, symbol,
                new LeanBar((decimal)b.BidO, (decimal)b.BidH, (decimal)b.BidL, (decimal)b.BidC), (decimal)b.BidSize,
                new LeanBar((decimal)b.AskO, (decimal)b.AskH, (decimal)b.AskL, (decimal)b.AskC), (decimal)b.AskSize,
                period));
        }
        var writer = new LeanDataWriter(Resolution.Minute, symbol, dataFolder, TickType.Quote);
        writer.Write(quoteBars);
    }
}

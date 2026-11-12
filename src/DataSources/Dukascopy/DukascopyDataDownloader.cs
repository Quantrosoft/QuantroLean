/*
 * MIT License
 * Copyright (c) 2025 Quantrosoft Pte. Ltd.
 *
 * Permission is hereby granted, free of charge, to any person obtaining a copy
 * of this software and associated documentation files (the "Software"), to deal
 * in the Software without restriction, including without limitation the rights
 * to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
 * copies of the Software, and to permit persons to whom the Software is
 * furnished to do so, subject to the following conditions:
 *
 * The above copyright notice and this permission notice shall be included in all
 * copies or substantial portions of the Software.
 *
 * THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
 * IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
 * FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
 * AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
 * LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
 * OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
 * SOFTWARE.
 *
 * Independent implementation: written from Dukascopy's public .bi5 feed format; no code is taken from any
 * third party. The files under Lzma/ are the public-domain LZMA SDK (see Lzma/LZMA-SDK-NOTICE.txt).
*/

using System;
using System.Collections.Generic;
using System.Buffers.Binary;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect;
using QuantConnect.Data;
using QuantConnect.Data.Market;
using QuantConnect.Logging;
using QuantConnect.Securities;

namespace QuantConnect.Lean.DataSource.Dukascopy
{
    /// <summary>
    /// Downloads free historical tick data from Dukascopy's public data feed and returns it as Lean quote ticks.
    /// Lean resolves this class by its type name (for example the downloader's <c>--data-downloader DukascopyDataDownloader</c>).
    /// </summary>
    /// <remarks>
    /// Dukascopy serves one LZMA-compressed file per instrument and hour at
    /// <c>https://datafeed.dukascopy.com/datafeed/{INSTRUMENT}/{YYYY}/{MM}/{DD}/{HH}h_ticks.bi5</c>, where the month is
    /// zero based (January = 00) and the hour is in GMT (= UTC). Each file decompresses to a sequence of 20-byte, big
    /// endian records: int32 milliseconds since the hour, int32 ask, int32 bid (both in the instrument's points),
    /// float32 ask volume, float32 bid volume. The point size and the Dukascopy instrument code are read from Lean's
    /// symbol-properties database (minimum price variation and market ticker), so no hard-coded instrument table is
    /// needed. Times are UTC throughout.
    /// </remarks>
    public sealed class DukascopyDataDownloader : IDataDownloader
    {
        private const string FeedUrlFormat =
            "https://datafeed.dukascopy.com/datafeed/{0}/{1:D4}/{2:D2}/{3:D2}/{4:D2}h_ticks.bi5";

        private const int RecordLength = 20;

        // Dukascopy throttles aggressive access: it answers bursts with HTTP 503 (and drip-fed, slow responses).
        // So requests are paced and transient failures are retried patiently with a long, capped backoff; only
        // after all of that do we give up (a real gap must fail loudly, never be written as missing data).
        private const int MaxAttempts = 8;
        private const int BaseBackoffMs = 3000;
        private const int MaxBackoffMs = 60000;
        private const int PaceDelayMs = 1500;

        private static readonly HttpClient HttpClient = CreateHttpClient();

        private readonly SymbolPropertiesDatabase _symbolPropertiesDatabase;

        /// <summary>
        /// Initializes a new instance of the <see cref="DukascopyDataDownloader"/> class.
        /// </summary>
        public DukascopyDataDownloader()
        {
            _symbolPropertiesDatabase = SymbolPropertiesDatabase.FromDataFolder();
        }

        /// <summary>
        /// Gets the historical quote ticks for a single symbol over the requested UTC time range.
        /// </summary>
        /// <param name="parameters">symbol, resolution, UTC start and end, tick type</param>
        /// <returns>the quote ticks in ascending time order, or <c>null</c> if the request is not supported</returns>
        public IEnumerable<BaseData> Get(DataDownloaderGetParameters parameters)
        {
            if (parameters == null)
            {
                throw new ArgumentNullException(nameof(parameters));
            }

            var symbol = parameters.Symbol;

            // Dukascopy delivers tick-level bid/ask quotes. Higher resolutions are built from these ticks by the
            // caller (QuantroLeanDownloader aggregates to minute, hour and daily), so only tick/quote requests are served here.
            if (parameters.Resolution != Resolution.Tick || parameters.TickType != TickType.Quote)
            {
                Log.Trace($"DukascopyDataDownloader.Get(): only Tick/Quote is supported; " +
                    $"got {parameters.Resolution}/{parameters.TickType} for {symbol.Value}. Returning no data.");
                return null;
            }

            if (symbol.SecurityType != SecurityType.Cfd && symbol.SecurityType != SecurityType.Forex)
            {
                Log.Trace($"DukascopyDataDownloader.Get(): security type {symbol.SecurityType} is not supported " +
                    $"for {symbol.Value}. Dukascopy serves forex and CFDs. Returning no data.");
                return null;
            }

            var symbolProperties = _symbolPropertiesDatabase.GetSymbolProperties(
                symbol.ID.Market, symbol, symbol.SecurityType, Currencies.USD);

            var instrument = !string.IsNullOrEmpty(symbolProperties.MarketTicker)
                ? symbolProperties.MarketTicker
                : symbol.Value.Replace("_", "").Replace(".", "");

            var pointSize = symbolProperties.MinimumPriceVariation;
            if (pointSize <= 0m)
            {
                throw new ArgumentException(
                    $"DukascopyDataDownloader.Get(): symbol-properties has no usable minimum price variation for " +
                    $"{symbol.Value} ({symbol.ID.Market}). Cannot scale Dukascopy prices.");
            }

            var startUtc = parameters.StartUtc;
            var endUtc = parameters.EndUtc;

            Log.Trace($"DukascopyDataDownloader.Get(): {symbol.Value} -> Dukascopy '{instrument}', point {pointSize}, " +
                $"{startUtc:yyyy-MM-dd HH:mm} .. {endUtc:yyyy-MM-dd HH:mm} UTC");

            return DownloadTicks(symbol, instrument, pointSize, startUtc, endUtc);
        }

        /// <summary>
        /// Downloads and decodes every hourly file in the range and returns the ticks as a materialized list
        /// (the caller enumerates the result several times).
        /// </summary>
        private List<BaseData> DownloadTicks(Symbol symbol, string instrument, decimal pointSize, DateTime startUtc, DateTime endUtc)
        {
            var ticks = new List<BaseData>();

            var firstHour = new DateTime(startUtc.Year, startUtc.Month, startUtc.Day, startUtc.Hour, 0, 0, DateTimeKind.Utc);
            var emptyHours = 0;
            var fileHours = 0;

            for (var hour = firstHour; hour < endUtc; hour = hour.AddHours(1))
            {
                // Pace the requests (except the first) so the feed is less likely to throttle us.
                if (hour != firstHour && PaceDelayMs > 0)
                {
                    Thread.Sleep(PaceDelayMs);
                }

                // Dukascopy's month is zero based in the path.
                var url = string.Format(FeedUrlFormat, instrument, hour.Year, hour.Month - 1, hour.Day, hour.Hour);

                var raw = Download(url);
                if (raw == null || raw.Length == 0)
                {
                    // 404 or empty hour: no ticks in this hour (weekend, holiday, before the instrument existed).
                    emptyHours++;
                    continue;
                }

                fileHours++;
                var decoded = LzmaDecode(raw, url);
                if (decoded.Length % RecordLength != 0)
                {
                    throw new IOException(
                        $"DukascopyDataDownloader: {url} decoded to {decoded.Length} bytes, not a multiple of {RecordLength}. " +
                        $"The delivery is corrupt.");
                }

                var count = decoded.Length / RecordLength;
                for (var i = 0; i < count; i++)
                {
                    var offset = i * RecordLength;
                    var span = decoded.AsSpan(offset, RecordLength);

                    var milliseconds = BinaryPrimitives.ReadInt32BigEndian(span);
                    var askPoints = BinaryPrimitives.ReadInt32BigEndian(span.Slice(4, 4));
                    var bidPoints = BinaryPrimitives.ReadInt32BigEndian(span.Slice(8, 4));
                    var askVolume = BinaryPrimitives.ReadSingleBigEndian(span.Slice(12, 4));
                    var bidVolume = BinaryPrimitives.ReadSingleBigEndian(span.Slice(16, 4));

                    var time = hour.AddMilliseconds(milliseconds);
                    if (time < startUtc || time >= endUtc)
                    {
                        continue;
                    }

                    var ask = askPoints * pointSize;
                    var bid = bidPoints * pointSize;

                    ticks.Add(new Tick
                    {
                        Symbol = symbol,
                        Time = time,
                        TickType = TickType.Quote,
                        AskPrice = ask,
                        BidPrice = bid,
                        AskSize = (decimal)askVolume,
                        BidSize = (decimal)bidVolume,
                        Value = (bid + ask) / 2m
                    });
                }
            }

            Log.Trace($"DukascopyDataDownloader.Get(): {symbol.Value} done: {ticks.Count} ticks from {fileHours} hourly files " +
                $"({emptyHours} empty/absent hours).");
            return ticks;
        }

        /// <summary>
        /// Downloads one hourly file. Returns the bytes on success, <c>null</c> on 404 (no data for this hour), and
        /// throws after <see cref="MaxAttempts"/> attempts on a transient failure (throttling, timeout). A transient
        /// failure is retried with a long, capped backoff and honours a <c>Retry-After</c> header when present.
        /// </summary>
        private static byte[] Download(string url)
        {
            var backoffMs = BaseBackoffMs;
            for (var attempt = 1; ; attempt++)
            {
                var retryAfterMs = 0;
                try
                {
                    using var response = HttpClient.GetAsync(url).GetAwaiter().GetResult();
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        return null;
                    }
                    if (response.IsSuccessStatusCode)
                    {
                        return response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    }

                    // 503/429 are Dukascopy's throttle; other non-success codes are real errors and fail at once.
                    if (response.StatusCode != HttpStatusCode.ServiceUnavailable &&
                        response.StatusCode != HttpStatusCode.TooManyRequests)
                    {
                        throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
                    }
                    retryAfterMs = GetRetryAfterMs(response);
                    throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} (throttled)");
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    if (attempt >= MaxAttempts)
                    {
                        throw new IOException(
                            $"DukascopyDataDownloader: {url} failed after {MaxAttempts} attempts: {ex.Message}", ex);
                    }
                    var waitMs = Math.Max(retryAfterMs, backoffMs);
                    Log.Trace($"DukascopyDataDownloader: {url} attempt {attempt} failed ({ex.Message}); retrying in {waitMs} ms.");
                    Thread.Sleep(waitMs);
                    backoffMs = Math.Min(backoffMs * 2, MaxBackoffMs);
                }
            }
        }

        /// <summary>
        /// Reads a <c>Retry-After</c> header (delta-seconds or an HTTP date) as milliseconds, or 0 if absent.
        /// </summary>
        private static int GetRetryAfterMs(HttpResponseMessage response)
        {
            var retryAfter = response.Headers.RetryAfter;
            if (retryAfter == null)
            {
                return 0;
            }
            if (retryAfter.Delta.HasValue)
            {
                return (int)Math.Min(retryAfter.Delta.Value.TotalMilliseconds, MaxBackoffMs);
            }
            if (retryAfter.Date.HasValue)
            {
                var ms = (retryAfter.Date.Value - DateTimeOffset.UtcNow).TotalMilliseconds;
                return ms > 0 ? (int)Math.Min(ms, MaxBackoffMs) : 0;
            }
            return 0;
        }

        /// <summary>
        /// Decodes a Dukascopy .bi5 file. The file is an LZMA "alone" stream: 5 property bytes, an 8-byte little
        /// endian uncompressed length, then the LZMA data.
        /// </summary>
        private static byte[] LzmaDecode(byte[] raw, string url)
        {
            if (raw.Length < 13)
            {
                throw new IOException($"DukascopyDataDownloader: {url} is only {raw.Length} bytes, too short to be an LZMA file.");
            }

            var properties = new byte[5];
            Array.Copy(raw, 0, properties, 0, 5);
            var uncompressedSize = BinaryPrimitives.ReadInt64LittleEndian(raw.AsSpan(5, 8));
            if (uncompressedSize < 0 || uncompressedSize > int.MaxValue)
            {
                throw new IOException($"DukascopyDataDownloader: {url} declares an implausible size of {uncompressedSize} bytes.");
            }

            var decoder = new SevenZip.Compression.LZMA.Decoder();
            decoder.SetDecoderProperties(properties);

            using var input = new MemoryStream(raw, 13, raw.Length - 13, writable: false);
            using var output = new MemoryStream((int)uncompressedSize);
            decoder.Code(input, output, raw.Length - 13, uncompressedSize, null);

            var decoded = output.ToArray();
            if (decoded.Length != uncompressedSize)
            {
                throw new IOException(
                    $"DukascopyDataDownloader: {url} decoded to {decoded.Length} bytes but the header declared {uncompressedSize}. " +
                    $"The delivery is corrupt.");
            }
            return decoded;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Lean.DataSource.Dukascopy/1.0)");
            return client;
        }
    }
}

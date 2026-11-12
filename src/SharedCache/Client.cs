/* MIT License
Copyright (c) 2026 Quantrosoft Pte. Ltd. - see Protocol.cs for the full notice. */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace QuantroLean.SharedCache
{
    /// <summary>The payload of one acquired key: either a read-only view of a shared section (every reader maps the
    /// same bytes) or, when the holder answered <c>uncached</c>/<c>dropped</c>, the consumer's own decoded bytes in
    /// this process. <see cref="OpenStream"/> gives a read-only stream of the payload either way.</summary>
    public sealed class MappedPayload : IDisposable
    {
        private readonly MemoryMappedFile? _mmf;
        private readonly byte[]? _bytes;
        public long Length { get; }
        public bool Shared => _mmf != null;

        private MappedPayload(MemoryMappedFile? mmf, byte[]? bytes, long length) { _mmf = mmf; _bytes = bytes; Length = length; }

        public static MappedPayload FromShared(MemoryMappedFile mmf, long length) => new(mmf, null, length);
        public static MappedPayload FromBytes(byte[] data) => new(null, data, data.LongLength);

        public Stream OpenStream() => _mmf != null
            ? _mmf.CreateViewStream(Protocol.HeaderSize, Length, MemoryMappedFileAccess.Read)
            : new MemoryStream(_bytes!, writable: false);

        public void Dispose() => _mmf?.Dispose();
    }

    /// <summary>The consumer side of the BTCACHE protocol: one call per data unit (BTCACHE_PROTOCOL.md section 5/7).
    /// <see cref="GetOrFill"/> returns the shared section if present, otherwise makes this process the filler (calls
    /// <c>fill</c> once, publishes the bytes) or, when the holder has no room, returns the freshly decoded bytes
    /// unshared. It never returns null: a protocol error is a loud exception, never a silent "no cache".</summary>
    public static class Client
    {
        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(600) };

        public static MappedPayload GetOrFill(string key, IReadOnlyList<string> source, Func<byte[]> fill,
                                              string consumerName, int port = Protocol.DefaultPort, Action<string>? log = null)
        {
            if (!Protocol.IsValidKey(key)) throw new ArgumentException("invalid BTCACHE key: " + key, nameof(key));
            var consumer = new { pid = Environment.ProcessId, name = consumerName };
            while (true)
            {
                var r = Post(port, "/btc/acquire", new { key, source, consumer });
                string state = Str(r, "state");
                switch (state)
                {
                    case "ready":
                        return OpenRead(Str(r, "name"), r.GetProperty("length").GetInt64(), key);
                    case "wait":
                        Thread.Sleep(r.TryGetProperty("retry_ms", out var ms) ? ms.GetInt32() : 200);
                        continue;
                    case "uncached":
                        log?.Invoke("[BTCACHE] uncached " + key + ": " + Why(r) + " - decoding in this process");
                        return MappedPayload.FromBytes(fill());
                    case "fill":
                        break;
                    default:
                        throw new InvalidDataException("[BTCACHE] " + key + ": unexpected acquire answer " + r.GetRawText());
                }

                string lease = Str(r, "lease");
                byte[] data = fill();
                var c = Post(port, "/btc/create", new { lease, length = data.LongLength });
                if (Str(c, "state") == "uncached")
                {
                    log?.Invoke("[BTCACHE] uncached " + key + " at create: " + Why(c) + " - keeping bytes in this process");
                    return MappedPayload.FromBytes(data);
                }
                string name = Str(c, "name");
                using (var w = Section.OpenReadWrite(name))
                using (var vs = w.CreateViewStream(Protocol.HeaderSize, data.LongLength, MemoryMappedFileAccess.Write))
                {
                    vs.Write(data, 0, data.Length);
                    vs.Flush();
                }
                var d = Post(port, "/btc/ready", new { lease, sha256 = Protocol.Sha256Hex(data) });
                if (Str(d, "state") != "ready")
                {
                    log?.Invoke("[BTCACHE] " + key + " not ready after fill: " + Why(d) + " - keeping bytes in this process");
                    return MappedPayload.FromBytes(data);
                }
                return OpenRead(Str(d, "name"), d.GetProperty("length").GetInt64(), key);
            }
        }

        private static MappedPayload OpenRead(string name, long length, string key)
        {
            var mmf = Section.OpenRead(name);
            try
            {
                using (var acc = mmf.CreateViewAccessor(0, Protocol.HeaderSize, MemoryMappedFileAccess.Read))
                {
                    var magic = new byte[8];
                    acc.ReadArray(0, magic, 0, 8);
                    for (int i = 0; i < 8; i++)
                        if (magic[i] != Protocol.MagicBytes[i]) throw new InvalidDataException("[BTCACHE] " + key + ": section " + name + " has no QSBTC001 magic");
                    if (acc.ReadInt32(Protocol.OffState) != 1) throw new InvalidDataException("[BTCACHE] " + key + ": section not ready");
                    long plen = acc.ReadInt64(Protocol.OffPayloadLength);
                    if (plen != length) throw new InvalidDataException("[BTCACHE] " + key + ": header length " + plen + " != announced " + length);
                }
                return MappedPayload.FromShared(mmf, length);
            }
            catch
            {
                mmf.Dispose();
                throw;
            }
        }

        /// <summary>Make sure a holder answers on the port; start one if not. One starter wins via a named mutex,
        /// the others wait for the port. Returns when a holder of a known protocol version answers.</summary>
        public static void EnsureHolder(int port, string dataRoot, string? holderCommand = null, Action<string>? log = null, int startTimeoutMs = 20000)
        {
            if (StatusOk(port)) return;
            using var mutex = new Mutex(false, "QuantroLeanCacheHolder_" + port);
            bool owned = false;
            try
            {
                try { owned = mutex.WaitOne(startTimeoutMs); } catch (AbandonedMutexException) { owned = true; }
                if (StatusOk(port)) return;
                var psi = BuildHolderStart(port, dataRoot, holderCommand);
                log?.Invoke("[BTCACHE] starting holder: " + psi.FileName + " " + psi.Arguments);
                Process.Start(psi);
                var sw = Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < startTimeoutMs)
                {
                    if (StatusOk(port)) return;
                    Thread.Sleep(200);
                }
                throw new TimeoutException("[BTCACHE] holder did not come up on port " + port + " within " + startTimeoutMs + " ms");
            }
            finally { if (owned) mutex.ReleaseMutex(); }
        }

        private static ProcessStartInfo BuildHolderStart(int port, string dataRoot, string? holderCommand)
        {
            var psi = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true };
            string args = "serve --port " + port + " --data-root \"" + dataRoot + "\"";
            if (!string.IsNullOrEmpty(holderCommand))
            {
                psi.FileName = holderCommand;
                psi.Arguments = args;
            }
            else
            {
                string dll = typeof(Client).Assembly.Location;
                psi.FileName = "dotnet";
                psi.Arguments = "\"" + dll + "\" " + args;
            }
            return psi;
        }

        private static bool StatusOk(int port)
        {
            try
            {
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(2));
                var resp = Http.GetAsync("http://127.0.0.1:" + port + "/btc/status", cts.Token).GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) return false;
                var text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using var doc = JsonDocument.Parse(text);
                return doc.RootElement.TryGetProperty("protocol", out var pv) && pv.GetInt32() == Protocol.Version;
            }
            catch (Exception) { return false; }
        }

        private static JsonElement Post(int port, string path, object body)
        {
            var content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
            using var resp = Http.PostAsync("http://127.0.0.1:" + port + path, content).GetAwaiter().GetResult();
            string text = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException("[BTCACHE] " + path + " -> HTTP " + (int)resp.StatusCode + ": " + text);
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }

        private static string Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
        private static string Why(JsonElement e) => e.TryGetProperty("why", out var w) ? w.GetString() ?? "" : "(no reason)";
    }
}

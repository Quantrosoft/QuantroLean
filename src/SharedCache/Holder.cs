/* MIT License
Copyright (c) 2026 Quantrosoft Pte. Ltd. - see Protocol.cs for the full notice. */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace QuantroLean.SharedCache
{
    /// <summary>The resident BTCACHE holder: owns named shared-memory sections and answers acquire/create/ready/
    /// drop/drop_kind/status over HTTP on 127.0.0.1 (BTCACHE_PROTOCOL.md). It decodes nothing - the first consumer
    /// that misses a key fills the section with its own decoder, every later consumer maps it read-only. A small HTTP
    /// server over a raw TcpListener keeps it dependency-free and needs no URL ACL / admin on any OS.</summary>
    public sealed class Holder : IDisposable
    {
        private sealed class Sec
        {
            public string Key = "";
            public SourceFile[] Source = Array.Empty<SourceFile>();
            public string Name = "";
            public long PayloadLength;
            public MemoryMappedFile Mmf = null!;
            public string? PayloadSha;
            public DateTime CreatedUtc;
            public long Hits;
            public long LastAccess;   // Stopwatch ticks
            public long TotalBytes => Protocol.HeaderSize + PayloadLength;
        }

        private sealed class Lease
        {
            public string Id = "";
            public string Key = "";
            public SourceFile[] Source = Array.Empty<SourceFile>();
            public int Pid;
            public string ConsumerName = "";
            public DateTime CreatedUtc;
            public Sec? Sec;   // set at /btc/create
        }

        private readonly int _port;
        private readonly string _dataRoot;
        private readonly double _ramLinePct;
        private readonly double _yieldPct;
        private readonly object _gate = new();
        private readonly Dictionary<string, Sec> _ready = new();
        private readonly Dictionary<string, Lease> _leaseByKey = new();
        private readonly Dictionary<string, Lease> _leaseById = new();
        private readonly Stopwatch _clock = Stopwatch.StartNew();

        private long _hits, _fills, _uncached, _evicted, _stale, _droppedLeases;
        private TcpListener? _listener;
        private Thread? _acceptThread;
        private Timer? _yieldTimer;
        private volatile bool _running;

        public Holder(int port, string dataRoot, double ramLinePct = 85.0, double yieldPct = 80.0)
        {
            _port = port;
            _dataRoot = Path.GetFullPath(dataRoot);
            _ramLinePct = ramLinePct;
            _yieldPct = yieldPct;
        }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Loopback, _port);
            _listener.Start();
            _running = true;
            _acceptThread = new Thread(AcceptLoop) { IsBackground = true, Name = "btc-accept" };
            _acceptThread.Start();
            _yieldTimer = new Timer(_ => YieldTick(), null, 5000, 5000);
        }

        public void Stop()
        {
            _running = false;
            try { _listener?.Stop(); } catch (SocketException) { }
            _yieldTimer?.Dispose();
            lock (_gate)
            {
                foreach (var s in _ready.Values) Section.Destroy(s.Mmf, s.Name);
                _ready.Clear();
                foreach (var l in _leaseById.Values) if (l.Sec != null) Section.Destroy(l.Sec.Mmf, l.Sec.Name);
                _leaseById.Clear();
                _leaseByKey.Clear();
            }
        }

        public void Dispose() => Stop();

        private void AcceptLoop()
        {
            while (_running)
            {
                TcpClient client;
                try { client = _listener!.AcceptTcpClient(); }
                catch (SocketException) { break; }
                catch (InvalidOperationException) { break; }
                var t = new Thread(() => HandleConnection(client)) { IsBackground = true };
                t.Start();
            }
        }

        // ---- tiny HTTP/1.1 over TcpListener (one request per connection, Connection: close) ----

        private void HandleConnection(TcpClient client)
        {
            using (client)
            using (var ns = client.GetStream())
            {
                try
                {
                    var (method, path, body) = ReadRequest(ns);
                    var (code, json) = Dispatch(method, path, body);
                    WriteResponse(ns, code, json);
                }
                catch (Exception ex)
                {
                    try { WriteResponse(ns, 400, Json(new { error = ex.Message })); } catch (IOException) { }
                }
            }
        }

        private static (string method, string path, string body) ReadRequest(NetworkStream ns)
        {
            var headerBytes = new List<byte>(1024);
            int prev = -1, prev2 = -1, prev3 = -1;
            int b;
            while ((b = ns.ReadByte()) >= 0)
            {
                headerBytes.Add((byte)b);
                if (prev3 == '\r' && prev2 == '\n' && prev == '\r' && b == '\n') break; // CRLF CRLF
                prev3 = prev2; prev2 = prev; prev = b;
            }
            string head = Encoding.ASCII.GetString(headerBytes.ToArray());
            var lines = head.Split("\r\n");
            var first = lines.Length > 0 ? lines[0].Split(' ') : Array.Empty<string>();
            string method = first.Length > 0 ? first[0] : "";
            string pathReq = first.Length > 1 ? first[1] : "";
            int contentLength = 0;
            foreach (var line in lines)
                if (line.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                    int.TryParse(line.Substring(15).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out contentLength);
            string body = "";
            if (contentLength > 0)
            {
                var buf = new byte[contentLength];
                int off = 0;
                while (off < contentLength)
                {
                    int got = ns.Read(buf, off, contentLength - off);
                    if (got <= 0) break;
                    off += got;
                }
                body = Encoding.UTF8.GetString(buf, 0, off);
            }
            return (method, pathReq, body);
        }

        private static void WriteResponse(NetworkStream ns, int code, string json)
        {
            string reason = code == 200 ? "OK" : code == 400 ? "Bad Request" : "Error";
            var payload = Encoding.UTF8.GetBytes(json);
            var head = Encoding.ASCII.GetBytes(
                "HTTP/1.1 " + code + " " + reason + "\r\n" +
                "Content-Type: application/json\r\n" +
                "Content-Length: " + payload.Length + "\r\n" +
                "Connection: close\r\n\r\n");
            ns.Write(head, 0, head.Length);
            ns.Write(payload, 0, payload.Length);
            ns.Flush();
        }

        private static string Json(object o) => JsonSerializer.Serialize(o);

        private (int, string) Dispatch(string method, string path, string body)
        {
            if (method == "GET" && path == "/btc/status") return (200, Status());
            if (method != "POST") return (400, Json(new { error = "only POST and GET /btc/status" }));
            using var doc = body.Length > 0 ? JsonDocument.Parse(body) : null;
            var root = doc?.RootElement ?? default;
            return path switch
            {
                "/btc/acquire" => Acquire(root),
                "/btc/create" => Create(root),
                "/btc/ready" => Ready(root),
                "/btc/drop" => Drop(root),
                "/btc/drop_kind" => DropKind(root),
                _ => (400, Json(new { error = "unknown path " + path })),
            };
        }

        // ---- requests (BTCACHE_PROTOCOL.md section 5) ----

        private (int, string) Acquire(JsonElement r)
        {
            string key = r.TryGetProperty("key", out var k) ? k.GetString() ?? "" : "";
            if (!Protocol.IsValidKey(key)) return (400, Json(new { error = "invalid key" }));
            var source = ReadSources(r); // throws (-> 400) if a source file is missing
            int pid = r.TryGetProperty("consumer", out var c) && c.TryGetProperty("pid", out var p) ? p.GetInt32() : 0;
            string cname = r.TryGetProperty("consumer", out var c2) && c2.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";

            lock (_gate)
            {
                if (_ready.TryGetValue(key, out var sec))
                {
                    if (SourceChanged(sec.Source, source)) { EvictSection(sec); _stale++; }
                    else { sec.Hits++; _hits++; sec.LastAccess = _clock.ElapsedTicks; return (200, Json(new { state = "ready", name = sec.Name, length = sec.PayloadLength })); }
                }
                if (_leaseByKey.TryGetValue(key, out var lease))
                {
                    if (IsAlive(lease.Pid)) return (200, Json(new { state = "wait", retry_ms = 200 }));
                    DropLease(lease); _droppedLeases++;
                }
                var newLease = new Lease { Id = Guid.NewGuid().ToString("N"), Key = key, Source = source, Pid = pid, ConsumerName = cname, CreatedUtc = DateTime.UtcNow };
                _leaseByKey[key] = newLease;
                _leaseById[newLease.Id] = newLease;
                return (200, Json(new { state = "fill", lease = newLease.Id }));
            }
        }

        private (int, string) Create(JsonElement r)
        {
            string leaseId = r.TryGetProperty("lease", out var l) ? l.GetString() ?? "" : "";
            long length = r.TryGetProperty("length", out var le) ? le.GetInt64() : 0;
            if (length <= 0) return (400, Json(new { error = "length must be > 0" }));
            lock (_gate)
            {
                if (!_leaseById.TryGetValue(leaseId, out var lease)) return (400, Json(new { error = "unknown lease" }));
                if (!EvictToFit(Protocol.HeaderSize + length))
                {
                    DropLease(lease); _uncached++;
                    return (200, Json(new { state = "uncached", why = "not enough RAM for " + length + " bytes" }));
                }
                string name = Section.NewName();
                var mmf = Section.Create(name, Protocol.HeaderSize + length);
                var sec = new Sec { Key = lease.Key, Source = lease.Source, Name = name, PayloadLength = length, Mmf = mmf, CreatedUtc = DateTime.UtcNow, LastAccess = _clock.ElapsedTicks };
                WriteHeader(sec, state: 0);
                lease.Sec = sec;
                return (200, Json(new { state = "created", name = name }));
            }
        }

        private (int, string) Ready(JsonElement r)
        {
            string leaseId = r.TryGetProperty("lease", out var l) ? l.GetString() ?? "" : "";
            string sha = r.TryGetProperty("sha256", out var s) ? s.GetString() ?? "" : "";
            lock (_gate)
            {
                if (!_leaseById.TryGetValue(leaseId, out var lease) || lease.Sec == null) return (400, Json(new { error = "unknown lease or create not called" }));
                var sec = lease.Sec;
                string actual = Protocol.Sha256HexOfFile(sec.Mmf, sec.PayloadLength);
                var current = StatSources(sec.Source.Select(x => x.Path));
                if (!string.Equals(actual, sha, StringComparison.OrdinalIgnoreCase) || SourceChanged(sec.Source, current))
                {
                    Section.Destroy(sec.Mmf, sec.Name);
                    DropLease(lease);
                    return (200, Json(new { state = "dropped", why = "payload sha mismatch or source changed during fill" }));
                }
                sec.PayloadSha = actual;
                WriteHeader(sec, state: 1);
                _ready[sec.Key] = sec;
                _leaseByKey.Remove(sec.Key);
                _leaseById.Remove(lease.Id);
                _fills++;
                sec.LastAccess = _clock.ElapsedTicks;
                return (200, Json(new { state = "ready", name = sec.Name, length = sec.PayloadLength }));
            }
        }

        private (int, string) Drop(JsonElement r)
        {
            string path = r.TryGetProperty("path", out var p) ? (p.GetString() ?? "") : "";
            path = path.Replace('\\', '/');
            lock (_gate)
            {
                var hit = _ready.Values.Where(s => s.Source.Any(x => string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var s in hit) EvictSection(s);
                return (200, Json(new { dropped = hit.Count }));
            }
        }

        private (int, string) DropKind(JsonElement r)
        {
            string kind = r.TryGetProperty("kind", out var k) ? (k.GetString() ?? "") : "";
            string prefix = kind + "/";
            lock (_gate)
            {
                var hit = _ready.Values.Where(s => s.Key.StartsWith(prefix, StringComparison.Ordinal)).ToList();
                foreach (var s in hit) EvictSection(s);
                return (200, Json(new { dropped = hit.Count }));
            }
        }

        private string Status()
        {
            lock (_gate)
            {
                double phys = 0; try { phys = MemoryProbe.Read().physPct; } catch { }
                long total = _ready.Values.Sum(s => s.TotalBytes);
                var entries = _ready.Values.Select(s => new { key = s.Key, state = s.PayloadSha != null ? 1 : 0, length = s.PayloadLength, hits = s.Hits }).ToArray();
                return Json(new
                {
                    protocol = Protocol.Version,
                    total_bytes = total,
                    sections_ready = _ready.Count,
                    sections_filling = _leaseByKey.Count,
                    ram_line_pct = _ramLinePct,
                    yield_pct = _yieldPct,
                    machine_ram_pct = phys,
                    counts = new { hits = _hits, fills = _fills, uncached = _uncached, evicted = _evicted, stale = _stale, dropped_leases = _droppedLeases },
                    entries,
                });
            }
        }

        // ---- helpers ----

        private void WriteHeader(Sec sec, int state)
        {
            var hdr = new byte[Protocol.HeaderSize];
            Array.Copy(Protocol.MagicBytes, 0, hdr, 0, 8);
            BitConverter.GetBytes(state).CopyTo(hdr, Protocol.OffState);
            BitConverter.GetBytes(1).CopyTo(hdr, Protocol.OffHeaderVersion);
            BitConverter.GetBytes((long)Protocol.HeaderSize).CopyTo(hdr, Protocol.OffPayloadOffset);
            BitConverter.GetBytes(sec.PayloadLength).CopyTo(hdr, Protocol.OffPayloadLength);
            var meta = new Dictionary<string, object?>
            {
                ["key"] = sec.Key,
                ["source"] = sec.Source.Select(x => new { path = x.Path, size = x.Size, write_time_utc_ticks = x.WriteTimeUtcTicks }).ToArray(),
                ["created_utc"] = sec.CreatedUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            };
            if (sec.PayloadSha != null) meta["payload_sha256"] = sec.PayloadSha;
            var jsonBytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(meta));
            if (Protocol.OffJson + jsonBytes.Length + 1 > Protocol.HeaderSize)
                throw new InvalidOperationException("BTCACHE header JSON exceeds " + (Protocol.HeaderSize - Protocol.OffJson) + " bytes");
            Array.Copy(jsonBytes, 0, hdr, Protocol.OffJson, jsonBytes.Length); // rest stays zero -> zero-terminated
            using var acc = sec.Mmf.CreateViewAccessor(0, Protocol.HeaderSize, MemoryMappedFileAccess.ReadWrite);
            acc.WriteArray(0, hdr, 0, hdr.Length);
            acc.Flush();
        }

        private SourceFile[] ReadSources(JsonElement r)
        {
            var list = new List<string>();
            if (r.TryGetProperty("source", out var s) && s.ValueKind == JsonValueKind.Array)
                foreach (var e in s.EnumerateArray()) list.Add(e.GetString() ?? "");
            return StatSources(list);
        }

        private SourceFile[] StatSources(IEnumerable<string> relPaths)
        {
            var res = new List<SourceFile>();
            foreach (var rel in relPaths)
            {
                if (rel.Contains("..") || rel.IndexOf('\\') >= 0) throw new InvalidDataException("bad source path " + rel);
                string full = Path.GetFullPath(Path.Combine(_dataRoot, rel));
                if (!full.StartsWith(_dataRoot, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("source escapes data root: " + rel);
                var fi = new FileInfo(full);
                if (!fi.Exists) throw new FileNotFoundException("source file does not exist: " + rel);
                res.Add(new SourceFile { Path = rel, Size = fi.Length, WriteTimeUtcTicks = fi.LastWriteTimeUtc.Ticks });
            }
            return res.ToArray();
        }

        private static bool SourceChanged(SourceFile[] stored, SourceFile[] current)
        {
            if (stored.Length != current.Length) return true;
            for (int i = 0; i < stored.Length; i++)
                if (stored[i].Path != current[i].Path || stored[i].Size != current[i].Size || stored[i].WriteTimeUtcTicks != current[i].WriteTimeUtcTicks)
                    return true;
            return false;
        }

        private void EvictSection(Sec sec)
        {
            Section.Destroy(sec.Mmf, sec.Name);
            _ready.Remove(sec.Key);
            _evicted++;
        }

        private void DropLease(Lease lease)
        {
            if (lease.Sec != null) Section.Destroy(lease.Sec.Mmf, lease.Sec.Name);
            _leaseByKey.Remove(lease.Key);
            _leaseById.Remove(lease.Id);
        }

        private bool EvictToFit(long neededBytes)
        {
            try
            {
                var (phys, commit, totalPhys) = MemoryProbe.Read();
                double addPct = totalPhys > 0 ? 100.0 * neededBytes / totalPhys : 100.0;
                while (Math.Max(phys, commit) + addPct >= _ramLinePct)
                {
                    var victim = LruReady();
                    if (victim == null) return false;
                    EvictSection(victim);
                    (phys, commit, totalPhys) = MemoryProbe.Read();
                }
                return true;
            }
            catch (PlatformNotSupportedException) { return true; } // no probe -> do not block (self-tests, unknown OS)
        }

        private void YieldTick()
        {
            if (!_running) return;
            try
            {
                lock (_gate)
                {
                    var (phys, commit, _) = MemoryProbe.Read();
                    while (Math.Max(phys, commit) >= _yieldPct)
                    {
                        var victim = LruReady();
                        if (victim == null) break;
                        EvictSection(victim);
                        (phys, commit, _) = MemoryProbe.Read();
                    }
                }
            }
            catch (PlatformNotSupportedException) { }
            catch (IOException) { }
        }

        private Sec? LruReady()
        {
            Sec? best = null;
            foreach (var s in _ready.Values)
                if (best == null || s.LastAccess < best.LastAccess) best = s;
            return best;
        }

        private static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try { using var p = Process.GetProcessById(pid); return !p.HasExited; }
            catch (ArgumentException) { return false; }
            catch (InvalidOperationException) { return false; }
        }
    }
}

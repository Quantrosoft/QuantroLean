/* MIT License
Copyright (c) 2026 Quantrosoft Pte. Ltd. - see Protocol.cs for the full notice. */

using System;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;

namespace QuantroLean.SharedCache
{
    /// <summary>Entry point of the bundled BTCACHE holder. <c>serve</c> runs the resident holder (the client starts it
    /// automatically when none answers); <c>selftest</c> proves one holder + two consumers share one section.</summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            if (args.Length > 0 && args[0] == "serve") return Serve(args);
            if (args.Length > 0 && args[0] == "selftest") return SelfTest();
            Console.Error.WriteLine("usage: QuantroLeanCacheHolder serve --port <p> --data-root <dir> [--ram-line <pct>] [--yield <pct>]");
            Console.Error.WriteLine("       QuantroLeanCacheHolder selftest");
            return 2;
        }

        private static int Serve(string[] args)
        {
            int port = Protocol.DefaultPort;
            string? dataRoot = null;
            double ram = 85.0, yield = 80.0;
            for (int i = 1; i < args.Length - 1; i++)
            {
                switch (args[i])
                {
                    case "--port": port = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--data-root": dataRoot = args[++i]; break;
                    case "--ram-line": ram = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                    case "--yield": yield = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                }
            }
            if (dataRoot == null) { Console.Error.WriteLine("ERROR --data-root is required"); return 2; }
            using var holder = new Holder(port, dataRoot, ram, yield);
            holder.Start();
            Console.WriteLine("[BTCACHE] holder listening on 127.0.0.1:" + port + " data-root=" + Path.GetFullPath(dataRoot));
            using var stop = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Set(); };
            stop.Wait();
            holder.Stop();
            return 0;
        }

        private static int SelfTest()
        {
            string root = Path.Combine(Path.GetTempPath(), "qlsc_selftest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "sub"));
            string rel = "sub/data.bin";
            File.WriteAllBytes(Path.Combine(root, "sub", "data.bin"), new byte[] { 1, 2, 3, 4 });

            int port = FreePort();
            using var holder = new Holder(port, root);
            holder.Start();
            if (!WaitStatus(port, 5000)) { Console.Error.WriteLine("FAIL: holder did not answer /btc/status"); return 1; }

            var payload = new byte[2 * 1024 * 1024];
            for (int i = 0; i < payload.Length; i++) payload[i] = (byte)(i % 251);

            int fillCalls = 0;
            Func<byte[]> fill = () => { Interlocked.Increment(ref fillCalls); return (byte[])payload.Clone(); };
            const string key = "example.selftest.v1/sub/data.bin";
            var src = new[] { rel };

            using var a = Client.GetOrFill(key, src, fill, "A", port, Console.WriteLine);
            byte[] da = ReadAll(a.OpenStream());
            using var b = Client.GetOrFill(key, src, fill, "B", port, Console.WriteLine);
            byte[] db = ReadAll(b.OpenStream());

            bool ok = true;
            ok &= Check("filler ran exactly once (B was a hit)", fillCalls == 1);
            ok &= Check("A payload length", da.Length == payload.Length);
            ok &= Check("B payload length", db.Length == payload.Length);
            ok &= Check("A bytes equal", SpanEq(da, payload));
            ok &= Check("B bytes equal (same shared copy)", SpanEq(db, payload));
            ok &= Check("A is a shared section", a.Shared);
            ok &= Check("B is a shared section", b.Shared);

            holder.Stop();
            try { Directory.Delete(root, true); } catch (IOException) { }
            Console.WriteLine(ok ? "SELFTEST PASS" : "SELFTEST FAIL");
            return ok ? 0 : 1;
        }

        private static bool Check(string what, bool cond) { Console.WriteLine((cond ? "  ok   " : "  FAIL ") + what); return cond; }

        private static bool SpanEq(byte[] x, byte[] y)
        {
            if (x.Length != y.Length) return false;
            for (int i = 0; i < x.Length; i++) if (x[i] != y[i]) return false;
            return true;
        }

        private static byte[] ReadAll(Stream s) { using var ms = new MemoryStream(); s.CopyTo(ms); return ms.ToArray(); }

        private static int FreePort()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            int p = ((IPEndPoint)l.LocalEndpoint).Port;
            l.Stop();
            return p;
        }

        private static bool WaitStatus(int port, int timeoutMs)
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var sw = System.Diagnostics.Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                try
                {
                    var resp = http.GetAsync("http://127.0.0.1:" + port + "/btc/status").GetAwaiter().GetResult();
                    if (resp.IsSuccessStatusCode) return true;
                }
                catch (Exception) { }
                Thread.Sleep(150);
            }
            return false;
        }
    }
}

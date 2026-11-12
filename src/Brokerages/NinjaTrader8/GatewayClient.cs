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
*/

using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QuantConnect.Logging;

namespace QuantroLean.Brokerages.NinjaTrader8
{
    /// <summary>
    /// The NinjaTrader 8 gateway process (`NinjaTrader8.Gateway.exe serve`) and the TCP session to it. The protocol:
    /// lines of TAB-separated fields, replies "R id ok|err ...", everything else pushed by the gateway on its own.
    /// </summary>
    internal sealed class GatewayClient : IDisposable
    {
        private const string ReadyPrefix = "[GATEWAY] ready ";
        private const string SelectedConnectionPrefix = "[GATEWAY] selected-connection\t";
        private const string SelectedAccountPrefix = "[GATEWAY] selected-account\t";
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

        private readonly ConcurrentDictionary<long, TaskCompletionSource<string[]>> _pending = new();
        private readonly object _writeLock = new();
        private Process _process;
        private TcpClient _tcp;
        private StreamWriter _writer;
        private long _nextId;
        private volatile bool _closed;

        /// <summary>Raised on the reader thread for every line the gateway pushes on its own (fields split at TAB).</summary>
        public event Action<string[]> Pushed;

        /// <summary>Raised once when the session to the gateway ends.</summary>
        public event Action<string> Closed;

        /// <summary>The connection the gateway resolved, from its "selected-connection" line; null if it reported none.</summary>
        public string SelectedConnection { get; private set; }

        /// <summary>The account the gateway resolved, from its "selected-account" line; null if it reported none.</summary>
        public string SelectedAccount { get; private set; }

        public bool IsConnected => _tcp != null && _tcp.Connected && !_closed;

        /// <summary>Starts the gateway, waits for its "ready" line and connects to the port it names. A null or empty
        /// connection or account is not passed on; the gateway then asks the user and reports the choice in
        /// <see cref="SelectedConnection"/> and <see cref="SelectedAccount"/>.</summary>
        public void Start(string gatewayPath, string connection, string account, string userDataDir, TimeSpan startTimeout)
        {
            if (!File.Exists(gatewayPath))
                throw new FileNotFoundException("NinjaTrader 8 gateway not found (ninjatrader8-gateway): " + Path.GetFullPath(gatewayPath));
            var info = new ProcessStartInfo(Path.GetFullPath(gatewayPath))
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(gatewayPath)),
            };
            info.ArgumentList.Add("serve");
            if (!string.IsNullOrEmpty(connection)) info.ArgumentList.Add("--connection=" + connection);
            if (!string.IsNullOrEmpty(account)) info.ArgumentList.Add("--account=" + account);
            if (!string.IsNullOrEmpty(userDataDir)) info.ArgumentList.Add("--user-data-dir=" + userDataDir);

            var ready = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            _process = new Process { StartInfo = info, EnableRaisingEvents = true };
            _process.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null) return;
                Log.Trace("NinjaTrader8 gateway: " + e.Data);
                if (e.Data.StartsWith(SelectedConnectionPrefix, StringComparison.Ordinal))
                    SelectedConnection = e.Data.Substring(SelectedConnectionPrefix.Length);
                else if (e.Data.StartsWith(SelectedAccountPrefix, StringComparison.Ordinal))
                    SelectedAccount = e.Data.Substring(SelectedAccountPrefix.Length);
                else if (e.Data.StartsWith(ReadyPrefix, StringComparison.Ordinal))
                {
                    string port = e.Data.Substring(ReadyPrefix.Length).Split(' ')[0];
                    ready.TrySetResult(int.Parse(port, CultureInfo.InvariantCulture));
                }
            };
            _process.ErrorDataReceived += (s, e) => { if (e.Data != null) Log.Error("NinjaTrader8 gateway: " + e.Data); };
            _process.Exited += (s, e) =>
            {
                ready.TrySetException(new InvalidOperationException("the NinjaTrader 8 gateway ended with exit code " + _process.ExitCode
                    + " before it was ready; its output is in the log above"));
                Close("the gateway process ended with exit code " + _process.ExitCode);
            };
            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            Log.Trace("NinjaTrader8 gateway: started process " + _process.Id + " (" + info.FileName + ")");

            if (!ready.Task.Wait(startTimeout))
                throw new TimeoutException("the NinjaTrader 8 gateway was not ready within " + (int)startTimeout.TotalSeconds + " s (ninjatrader8-start-timeout)");
            int portNumber = ready.Task.Result;

            _tcp = new TcpClient { NoDelay = true };
            _tcp.Connect("127.0.0.1", portNumber);
            NetworkStream stream = _tcp.GetStream();
            _writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var reader = new Thread(() => ReadLoop(stream)) { IsBackground = true, Name = "NinjaTrader8 gateway reader" };
            reader.Start();
        }

        /// <summary>Sends a request and waits for its reply; returns the fields after "ok", throws on "err".</summary>
        public string[] Request(string verb, params string[] args)
        {
            if (!IsConnected) throw new InvalidOperationException("not connected to the NinjaTrader 8 gateway");
            long id = Interlocked.Increment(ref _nextId);
            var tcs = new TaskCompletionSource<string[]>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[id] = tcs;
            var line = new StringBuilder();
            line.Append(id.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(verb);
            foreach (string a in args) line.Append('\t').Append(Clean(a));
            lock (_writeLock) _writer.WriteLine(line.ToString());
            if (!tcs.Task.Wait(RequestTimeout))
            {
                _pending.TryRemove(id, out _);
                throw new TimeoutException("the NinjaTrader 8 gateway did not answer '" + verb + "' within " + (int)RequestTimeout.TotalSeconds + " s");
            }
            string[] reply = tcs.Task.Result;
            if (reply.Length < 3) throw new InvalidOperationException("malformed reply to '" + verb + "'");
            if (reply[2] != "ok")
                throw new InvalidOperationException("NinjaTrader 8 gateway, '" + verb + "': " + (reply.Length > 3 ? reply[3] : reply[2]));
            return reply.AsSpan(3).ToArray();
        }

        private static string Clean(string s) => (s ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');

        private void ReadLoop(NetworkStream stream)
        {
            string reason = "the gateway closed the connection";
            try
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string[] f = line.Split('\t');
                    if (f[0] == "R" && f.Length >= 3 && long.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
                    {
                        if (_pending.TryRemove(id, out var tcs)) tcs.TrySetResult(f);
                        continue;
                    }
                    try
                    {
                        Pushed?.Invoke(f);
                    }
                    catch (Exception ex)
                    {
                        Log.Error(ex, "NinjaTrader8 gateway: handling '" + line + "'");
                    }
                }
            }
            catch (IOException ex)
            {
                reason = "reading from the gateway failed: " + ex.Message;
            }
            catch (ObjectDisposedException)
            {
                reason = "the session was closed";
            }
            Close(reason);
        }

        private void Close(string reason)
        {
            if (_closed) return;
            _closed = true;
            foreach (var p in _pending.Values) p.TrySetException(new InvalidOperationException("NinjaTrader 8 gateway: " + reason));
            Log.Trace("NinjaTrader8 gateway: session ended - " + reason);
            Closed?.Invoke(reason);
        }

        /// <summary>Asks the gateway host to shut down and waits for the process to end; kills it after 60 s
        /// as a fallback.</summary>
        public void Dispose()
        {
            if (IsConnected)
            {
                // "shutdown" ends the gateway host (clean exit); "quit" would only close this client session and
                // leave the host running for its client-timeout, which this Dispose then force-killed after 60 s.
                try { Request("shutdown"); }
                catch (Exception ex) when (ex is InvalidOperationException || ex is TimeoutException || ex is AggregateException)
                {
                    Log.Error("NinjaTrader8 gateway: shutdown - " + ex.Message);
                }
            }
            _tcp?.Dispose();
            if (_process != null)
            {
                if (!_process.WaitForExit(60000))
                {
                    Log.Error("NinjaTrader8 gateway: the process did not end within 60 s - killed");
                    _process.Kill();
                }
                _process.Dispose();
                _process = null;
            }
            _closed = true;
        }
    }
}

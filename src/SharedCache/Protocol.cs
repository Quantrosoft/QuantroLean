/* MIT License
Copyright (c) 2026 Quantrosoft Pte. Ltd.

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
using System.Globalization;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace QuantroLean.SharedCache
{
    /// <summary>Constants and helpers of the BTCACHE protocol, version 1. The wire contract itself is the public
    /// master text <c>BTCACHE_PROTOCOL.md</c> in this folder (a byte copy of the CacheGuard master); this class only
    /// implements it. The holder (<see cref="Holder"/>) and client (<see cref="Client"/>) both build on it.</summary>
    public static class Protocol
    {
        public const int Version = 1;
        public const int DefaultPort = 9878;
        public const int HeaderSize = 4096;     // the section's fixed header; the payload starts here
        public const int MaxKeyLength = 512;

        // Header field byte offsets (little-endian), BTCACHE_PROTOCOL.md section 4.
        public const int OffState = 8;          // int32: 0 = being filled, 1 = ready
        public const int OffHeaderVersion = 12; // int32: 1
        public const int OffPayloadOffset = 16; // int64: 4096
        public const int OffPayloadLength = 24; // int64
        public const int OffJson = 64;          // UTF-8 JSON, zero-terminated, up to byte 4095

        public static readonly byte[] MagicBytes = Encoding.ASCII.GetBytes("QSBTC001"); // 8 bytes

        /// <summary>A key is <c>&lt;kind&gt;/&lt;rest&gt;</c>, '/'-separated, &lt;= 512 chars, no '..' segment, no
        /// backslash (BTCACHE_PROTOCOL.md section 3).</summary>
        public static bool IsValidKey(string? key)
        {
            if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return false;
            if (key.IndexOf('\\') >= 0) return false;
            int slash = key.IndexOf('/');
            if (slash <= 0 || slash == key.Length - 1) return false; // need a non-empty kind and rest
            foreach (var seg in key.Split('/'))
                if (seg.Length == 0 || seg == "..") return false;
            return true;
        }

        public static string Sha256Hex(ReadOnlySpan<byte> data)
        {
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(data, hash);
            var sb = new StringBuilder(64);
            foreach (var b in hash) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        public static string Sha256HexOfFile(MemoryMappedFile mmf, long payloadLength)
        {
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            using var acc = mmf.CreateViewStream(HeaderSize, payloadLength, MemoryMappedFileAccess.Read);
            var buf = new byte[1 << 20];
            long left = payloadLength;
            while (left > 0)
            {
                int want = (int)Math.Min(buf.Length, left);
                int got = acc.Read(buf, 0, want);
                if (got <= 0) throw new IOException("short read while hashing section payload");
                sha.AppendData(buf, 0, got);
                left -= got;
            }
            var h = sha.GetHashAndReset();
            var sb = new StringBuilder(64);
            foreach (var b in h) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }
    }

    /// <summary>One source file of a section: its data-root-relative path and the size/write time at lease time
    /// (a change drops the section, BTCACHE_PROTOCOL.md section 3).</summary>
    public sealed class SourceFile
    {
        public string Path { get; set; } = "";
        public long Size { get; set; }
        public long WriteTimeUtcTicks { get; set; }
    }

    /// <summary>Reads physical-memory and commit load of the machine, cross-platform. Windows: GlobalMemoryStatusEx;
    /// Linux: /proc/meminfo. The holder's RAM line and yield line are checked against these (protocol section 6).</summary>
    public static class MemoryProbe
    {
        public static bool Available => OperatingSystem.IsWindows() || OperatingSystem.IsLinux();

        /// <summary>(usedPhysicalPercent, usedCommitPercent, totalPhysicalBytes). On Linux commit == physical (only
        /// MemAvailable is read), which is a conservative single gate.</summary>
        public static (double physPct, double commitPct, long totalPhys) Read()
        {
            if (OperatingSystem.IsWindows()) return ReadWindows();
            if (OperatingSystem.IsLinux()) return ReadLinux();
            throw new PlatformNotSupportedException("BTCACHE holder: memory probe supports Windows and Linux only");
        }

        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatusEx
        {
            public uint dwLength;
            public uint dwMemoryLoad;
            public ulong ullTotalPhys;
            public ulong ullAvailPhys;
            public ulong ullTotalPageFile;
            public ulong ullAvailPageFile;
            public ulong ullTotalVirtual;
            public ulong ullAvailVirtual;
            public ulong ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

        private static (double, double, long) ReadWindows()
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            if (!GlobalMemoryStatusEx(m))
                throw new IOException("GlobalMemoryStatusEx failed, error " + Marshal.GetLastWin32Error());
            double phys = m.dwMemoryLoad;
            double commit = m.ullTotalPageFile == 0 ? 0.0
                : 100.0 * (m.ullTotalPageFile - m.ullAvailPageFile) / m.ullTotalPageFile;
            return (phys, commit, (long)m.ullTotalPhys);
        }

        private static (double, double, long) ReadLinux()
        {
            long total = 0, avail = 0;
            foreach (var line in File.ReadAllLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) total = MemKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) avail = MemKb(line);
            }
            if (total <= 0) throw new IOException("/proc/meminfo: no MemTotal");
            double used = 100.0 * (total - avail) / total;
            return (used, used, total * 1024L);
        }

        private static long MemKb(string line)
        {
            var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return parts.Length >= 2 && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var kb) ? kb : 0;
        }
    }
}

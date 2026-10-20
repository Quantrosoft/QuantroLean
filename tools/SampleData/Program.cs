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
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;

namespace MyLean.Tools.SampleData
{
   /// <summary>Puts the Lean sample data the examples of this repository run on into a Lean data folder. The data is
   /// not part of the repository; this tool takes it from Lean, bit-exact: every file is checked against its size and
   /// SHA-256 at Lean commit <see cref="LeanCommit"/>, the commit the Lean NuGet packages of Directory.Packages.props
   /// were built from. CSV files are compared with LF line ends (git may check them out with CRLF).
   ///
   /// Usage:
   ///   SampleData --to &lt;data folder&gt; --from &lt;Data folder of a Lean clone&gt;   copy from a local Lean clone
   ///   SampleData --to &lt;data folder&gt; --download                           download from github.com/QuantConnect/Lean
   /// A file already in the target with the right content is kept; one with other content stops the tool (exit 2)
   /// without touching it.</summary>
   internal static class Program
   {
      private const string LeanCommit = "ea3d5159152530d58f3afe2feced990a0bf64b96";

      private static readonly (string Path, long Size, string Sha256)[] Files =
      {
         // SmaCrossoverAlgorithm (C#, Python): SPY minute bars 2013-10-04 to 2013-10-11
         ("equity/usa/minute/spy/20131004_trade.zip", 7694, "d9a7fb035d3455b5d47e4e4c18f19384ca9b43d03925739cb3f0393ababa0954"),
         ("equity/usa/minute/spy/20131004_quote.zip", 11970, "3ca617ce36c17dc1581748d69e7afd697df51d164bbb1fc8681f097dde8c5f05"),
         ("equity/usa/minute/spy/20131007_trade.zip", 8791, "42a51db3d3b07cb5421189b92bf831d996b9369e2211b5bea54e6af2dcfcd173"),
         ("equity/usa/minute/spy/20131007_quote.zip", 12919, "79bedca5cdc5fd6f48b66f8f8cfb1832dfb9ac98f69a246824b3c64a311eb3b3"),
         ("equity/usa/minute/spy/20131008_trade.zip", 9549, "3632ac059d585b702a6c01851dbcb8c21c7d599d84ab1b8bcae8e3e3578c8bd0"),
         ("equity/usa/minute/spy/20131008_quote.zip", 14020, "8c9b18c67a3cb3109186898603f52781b4c3c2bacdaa6a439d27765fb1233c89"),
         ("equity/usa/minute/spy/20131009_trade.zip", 9224, "72d7d50c7eb24084a205c0c25a3f56b372675527addfe8d05f90c64aeb7943e8"),
         ("equity/usa/minute/spy/20131009_quote.zip", 13649, "74c5f5aa899c1ff2b37e736585595bc30244d7a8262c3fbdcb989ec054d61494"),
         ("equity/usa/minute/spy/20131010_trade.zip", 10054, "deb8c6873ebfe5071e13c255c47bde76b7b086a70ce29a8832a8fc0cd089b1de"),
         ("equity/usa/minute/spy/20131010_quote.zip", 14224, "7a07dbd4f6372a66dfe27b5ab1f270594e464ca7a8ceef1d0a53c45813f6289a"),
         ("equity/usa/minute/spy/20131011_trade.zip", 8356, "28dbc224a97d8273f9aac4316a8cae2e8dda01ec7fcd3c3a38f9ed746d315e83"),
         ("equity/usa/minute/spy/20131011_quote.zip", 12613, "d9c42e80df2ab1acd503bf20e950b6f2f8a67461619ab78d0bdee74be97c6445"),
         ("equity/usa/factor_files/spy.csv", 2649, "ad53e292de2e7076ff36721e1dcffd1db0d04fe6813b59aeece8620fb0d4cec1"),
         // NinjaScript example (SmaCrossoverStrategy): SPY ticks 2013-10-07, trades and quotes
         ("equity/usa/tick/spy/20131007_trade.zip", 905882, "1d966532ad3e9d4fcf835d4dec182f06cff970937ca97b4fcae8bac65e85688e"),
         ("equity/usa/tick/spy/20131007_quote.zip", 6671973, "e3186f734f51b27fbe75632c54bc6d30e00ff742d4dd15949a86d40e0bab62fc"),
      };

      private static int Main(string[] args)
      {
         string to = null, from = null;
         bool download = false;
         for (int i = 0; i < args.Length; i++)
         {
            switch (args[i])
            {
               case "--to" when i + 1 < args.Length: to = args[++i]; break;
               case "--from" when i + 1 < args.Length: from = args[++i]; break;
               case "--download": download = true; break;
               default:
                  return Usage($"unknown argument '{args[i]}'");
            }
         }
         if (to == null) return Usage("--to is missing");
         if ((from == null) == !download) return Usage("give exactly one of --from <folder> and --download");
         if (from != null && !Directory.Exists(from)) return Usage($"--from folder '{from}' does not exist");

         using var http = download ? new HttpClient { Timeout = TimeSpan.FromMinutes(5) } : null;
         int fetched = 0, kept = 0;
         foreach (var (path, size, sha256) in Files)
         {
            var target = Path.Combine(to, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(target))
            {
               var have = Hash(Content(File.ReadAllBytes(target), path));
               if (have != sha256)
               {
                  Console.Error.WriteLine($"ERROR {target} exists with other content (SHA-256 {have}, Lean has {sha256}). "
                                          + "Not touched - move it away and run again.");
                  return 2;
               }
               kept++;
               Console.WriteLine($"ok    {path}");
               continue;
            }

            byte[] data;
            string source;
            if (download)
            {
               source = $"https://raw.githubusercontent.com/QuantConnect/Lean/{LeanCommit}/Data/{path}";
               data = http.GetByteArrayAsync(source).GetAwaiter().GetResult();
            }
            else
            {
               source = Path.Combine(from, path.Replace('/', Path.DirectorySeparatorChar));
               if (!File.Exists(source))
               {
                  Console.Error.WriteLine($"ERROR {source} does not exist in the Lean data folder.");
                  return 3;
               }
               data = File.ReadAllBytes(source);
            }
            data = Content(data, path);
            var got = Hash(data);
            if (data.LongLength != size || got != sha256)
            {
               Console.Error.WriteLine($"ERROR {source}: {data.LongLength} bytes, SHA-256 {got}; Lean commit {LeanCommit} has "
                                       + $"{size} bytes, SHA-256 {sha256}. Nothing written.");
               return 3;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target));
            var tmp = target + ".tmp";
            File.WriteAllBytes(tmp, data);
            File.Move(tmp, target, overwrite: false);
            fetched++;
            Console.WriteLine($"got   {path} ({size} bytes)");
         }
         Console.WriteLine($"{fetched} file(s) fetched, {kept} already present, all {Files.Length} match Lean commit {LeanCommit}.");
         return 0;
      }

      /// <summary>The bytes to compare: CSV files with LF line ends, everything else unchanged.</summary>
      private static byte[] Content(byte[] data, string path)
         => path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(data).Replace("\r\n", "\n"))
            : data;

      private static string Hash(byte[] data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

      private static int Usage(string error)
      {
         Console.Error.WriteLine("ERROR " + error);
         Console.Error.WriteLine("usage: SampleData --to <data folder> (--from <Data folder of a Lean clone> | --download)");
         return 1;
      }
   }
}

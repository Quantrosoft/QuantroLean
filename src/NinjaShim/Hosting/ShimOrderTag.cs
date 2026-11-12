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

namespace QuantroLean.NinjaShim.Hosting
{
   /// <summary>Parsed order tag: entry vs exit + the associated signals.</summary>
   public sealed class ShimOrderTag
   {
      public enum TagKind { Entry, Exit }

      public TagKind Kind;
      /// <summary>The entry signal this order is tied to (both kinds).</summary>
      public string FromSignal;
      /// <summary>(Exit only) the reason: "SL"/"TP"/"EmergencyClose"/"exit"/…</summary>
      public string ExitSignal;

      /// <summary>Encode an entry market order tag: "ENT;&lt;signal&gt;".</summary>
      public static string EncodeEntry(string signal)
         => "ENT;" + (signal ?? "");

      /// <summary>Encode an exit market order tag:
      /// "EXT;&lt;fromSignal&gt;;&lt;exitSignal&gt;".</summary>
      public static string EncodeExit(string fromSignal, string exitSignal)
         => "EXT;" + (fromSignal ?? "") + ";" + (exitSignal ?? "");

      /// <summary>Parse a tag produced by EncodeEntry/EncodeExit. Returns
      /// false for null/foreign tags (e.g. broker-generated orders that do
      /// not belong to the shim) — callers treat those as orphans, they are
      /// not an error condition.</summary>
      public static bool TryParse(string tag, out ShimOrderTag t)
      {
         t = null;
         if (string.IsNullOrEmpty(tag) || tag.Length < 4 || tag[3] != ';') return false;
         var prefix = tag.Substring(0, 3);
         var rest = tag.Substring(4);
         switch (prefix)
         {
            case "ENT":
               t = new ShimOrderTag { Kind = TagKind.Entry, FromSignal = rest };
               return true;
            case "EXT":
               var sep = rest.IndexOf(';');
               t = sep < 0
                  ? new ShimOrderTag { Kind = TagKind.Exit, FromSignal = rest }
                  : new ShimOrderTag
                    {
                       Kind = TagKind.Exit,
                       FromSignal = rest.Substring(0, sep),
                       ExitSignal = rest.Substring(sep + 1),
                    };
               return true;
            default:
               return false;
         }
      }
   }
}

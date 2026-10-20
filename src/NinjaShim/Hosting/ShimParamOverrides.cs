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
using System.Collections.Generic;
using NinjaTrader.NinjaScript;

namespace MyLean.NinjaShim.Hosting
{
   public static class ShimParamOverrides
   {
      /// <summary>Apply p_-prefixed key/value overrides onto the strategy.
      /// Call AFTER DriveLifecycle(State.SetDefaults) so the overrides land
      /// on top of the strategy author's defaults. `log` (optional) receives
      /// one line per applied override for the host's diagnostics.</summary>
      public static void Apply(StrategyBase s,
         IEnumerable<KeyValuePair<string, string>> pParams, Action<string> log = null)
      {
         if (s == null)
            throw new ArgumentNullException(nameof(s),
               "[SHIM-HOSTING] ShimParamOverrides.Apply: strategy is null.");
         if (pParams == null)
            throw new ArgumentNullException(nameof(pParams),
               "[SHIM-HOSTING] ShimParamOverrides.Apply: pParams is null.");

         var ic = System.Globalization.CultureInfo.InvariantCulture;
         foreach (var kv in pParams)
         {
            if (kv.Key == null || !kv.Key.StartsWith("p_", StringComparison.Ordinal))
               continue;
            var name = kv.Key.Substring(2);
            var prop = s.GetType().GetProperty(name);
            if (prop == null || !prop.CanWrite)
               throw new InvalidOperationException(
                  $"[SHIM-HOSTING] Unknown/readonly strategy property '{name}' " +
                  $"from parameter '{kv.Key}' (strategy {s.GetType().Name}). " +
                  "Fix the config — overrides never fall back to defaults.");
            var pt = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;
            object val;
            if      (pt.IsEnum)              val = Enum.Parse(pt, kv.Value, ignoreCase: true);
            else if (pt == typeof(bool))     val = bool.Parse(kv.Value);
            else if (pt == typeof(int))      val = int.Parse(kv.Value, ic);
            else if (pt == typeof(double))   val = double.Parse(kv.Value, ic);
            else if (pt == typeof(DateTime)) val = DateTime.Parse(kv.Value, ic);
            else                             val = kv.Value;
            prop.SetValue(s, val);
            log?.Invoke($"[SHIM-HOSTING] Param override: {name} = {kv.Value}");
         }
      }
   }
}

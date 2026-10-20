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

// Minimal stand-in for QuantConnect.Logging.Log so the compiled-in GatewayClient.cs builds into this
// test assembly without the full Lean dependency graph. Program sets Sink to its own logger.
namespace QuantConnect.Logging
{
    public static class Log
    {
        public static Action<string> Sink = s => Console.WriteLine(s);
        public static void Trace(string s) { Sink("[gw] " + s); }
        public static void Error(string s) { Sink("[gw-ERR] " + s); }
        public static void Error(Exception e, string s) { Sink("[gw-ERR] " + s + " :: " + (e == null ? "" : e.Message)); }
        public static void Debug(string s) { Sink("[gw-dbg] " + s); }
    }
}

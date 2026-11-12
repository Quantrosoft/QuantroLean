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

Independent implementation.
*/

using System.Diagnostics;
using System.Text.Json;

namespace QuantroLean.MonteCarlo.Spp;

/// <summary>Runs a QuantroLean backtest and reads back the closed-trade profits.</summary>
public sealed class BacktestRunner
{
    private readonly string _launcherExe;
    private readonly string _launcherDir;
    private readonly string _algorithmTypeName;

    public BacktestRunner(string launcherExe, string algorithmTypeName)
    {
        _launcherExe = launcherExe;
        _launcherDir = Path.GetDirectoryName(Path.GetFullPath(launcherExe))!;
        _algorithmTypeName = algorithmTypeName;
    }

    /// <summary>
    /// Runs the backtest on <paramref name="dataFolder"/> and returns the closed-trade profits (account currency),
    /// in trade order. Throws if the backtest fails.
    /// </summary>
    public double[] RunAndGetProfits(string dataFolder, string resultsDir)
    {
        Directory.CreateDirectory(resultsDir);
        var psi = new ProcessStartInfo(_launcherExe)
        {
            WorkingDirectory = _launcherDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        psi.ArgumentList.Add("--algorithm-type-name");
        psi.ArgumentList.Add(_algorithmTypeName);
        psi.ArgumentList.Add("--data-folder");
        psi.ArgumentList.Add(Path.GetFullPath(dataFolder));
        psi.ArgumentList.Add("--results-destination-folder");
        psi.ArgumentList.Add(Path.GetFullPath(resultsDir));

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("could not start the launcher.");
        // Drain the pipes so the process does not block on a full buffer.
        p.OutputDataReceived += (_, _) => { };
        p.ErrorDataReceived += (_, _) => { };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(10 * 60 * 1000))
        {
            p.Kill(entireProcessTree: true);
            throw new TimeoutException("the backtest did not finish within 10 minutes.");
        }
        p.WaitForExit();
        if (p.ExitCode != 0)
        {
            throw new InvalidOperationException($"the backtest exited with code {p.ExitCode} (results in {resultsDir}).");
        }

        return ReadClosedTradeProfits(Path.Combine(resultsDir, _algorithmTypeName + ".json"));
    }

    /// <summary>Reads totalPerformance.closedTrades[].profitLoss from a Lean backtest result file.</summary>
    public static double[] ReadClosedTradeProfits(string resultJsonPath)
    {
        if (!File.Exists(resultJsonPath))
        {
            throw new FileNotFoundException($"backtest result not found: {resultJsonPath}");
        }
        using var doc = JsonDocument.Parse(File.ReadAllText(resultJsonPath));
        if (!doc.RootElement.TryGetProperty("totalPerformance", out var tp) ||
            !tp.TryGetProperty("closedTrades", out var trades) ||
            trades.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        var profits = new List<double>(trades.GetArrayLength());
        foreach (var t in trades.EnumerateArray())
        {
            if (t.TryGetProperty("profitLoss", out var pl))
            {
                profits.Add(pl.GetDouble());
            }
        }
        return profits.ToArray();
    }
}

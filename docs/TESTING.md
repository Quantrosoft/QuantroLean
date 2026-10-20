# Testing MyLean

Commands and expected results to verify every feature on a clean checkout. The numeric results are deterministic:
Lean backtests, the immutable Dukascopy history and the seeded Monte Carlo all reproduce the same values. Run from
the repository root unless noted. `.NET 10 SDK` is required; the NinjaShim tests also need a local NinjaTrader 8.

## 1. Build

    dotnet build MyLean.sln

Expected: build succeeds, 0 errors. With NinjaTrader 8 installed, also `dotnet build MyLean.NinjaTrader.sln`.

## 2. Sample data

    dotnet run --project tools/SampleData -- --to data --download

Expected: the SPY sample data and the data-folder files are written into `data/` and pass their SHA-256 check.

## 3. Backtest (C# example, no network)

    cd src/Lean/MyLauncher/bin/Debug
    MyLauncher --results-destination-folder out

Expected (SmaCrossoverAlgorithm on the SPY minute sample):
- `STATISTICS:: Total Orders 85`
- `STATISTICS:: End Equity 99889.20`
- `STATISTICS:: OrderListHash a25e0d233a7cd30406944c0de2d1fd69`

## 4. Dukascopy download and content check (network)

    cd src/Lean/MyDownloader/bin/Debug
    MyDownloader --tickers EUR_USD --data-downloader DukascopyDataDownloader --data-type Quote \
                 --security-type Cfd --market dukascopy --resolution Tick --start-date 20240102 --end-date 20240103

Expected:
- log line `EUR_USD done: 101427 ticks from 24 hourly files (0 empty/absent hours).`
- the written tick file `data/cfd/dukascopy/tick/eur_usd/20240102_quote.zip` decompresses to 101427 rows of
  `ms,bid,ask`; the first row at or after 36000000 ms (hour 10 UTC) is `36000224,1.10149,1.1015`.

The feed throttles heavy use; a day can take a few minutes with retries. This is the only step that downloads data.

## 5. Dukascopy backtest (needs step 4's data, no network)

    cd src/Lean/MyLauncher/bin/Debug
    MyLauncher --algorithm-type-name DukascopyExampleAlgorithm --results-destination-folder out_duka

Expected (DukascopyExampleAlgorithm on the EUR_USD minute data of 2024-01-02):
- `STATISTICS:: Total Orders 30`
- `STATISTICS:: End Equity 99643.87`
- `STATISTICS:: OrderListHash ac53bdb0d460c031f9cf264b75e3a570`

## 6. Monte Carlo, trade resampling (no network)

Make a trades CSV from a backtest result (the closed trades' profit and loss), for example from step 3's
`out/SmaCrossoverAlgorithm.json` (`totalPerformance.closedTrades[].profitLoss`), as `trades.csv` with a `profit`
column. Then:

    cd tools/MonteCarlo/bin/Debug
    MonteCarlo --trades trades.csv --method bootstrap --iterations 2000 --seed 42 --out-dir a
    MonteCarlo --trades trades.csv --method bootstrap --iterations 2000 --seed 42 --out-dir b

Expected:
- determinism: `a/montecarlo_bootstrap_summary.json` and `b/...` are byte-identical (same for the `_simulations.csv`).
- reshuffle invariant: with `--method reshuffle`, the NetProfit distribution's min, max and real value are equal
  (reshuffle keeps the total), while MaxDrawdown's min and max differ.
- all four methods (`reshuffle`, `bootstrap`, `block`, `stationary`) finish with exit code 0.

## 7. Monte Carlo, synthetic price path (needs step 4's data, no extra network)

    cd tools/MonteCarloSpp/bin/Debug
    MonteCarloSpp --data-folder <repo>/data --launcher <repo>/src/Lean/MyLauncher/bin/Debug/MyLauncher.exe \
                  --start-date 20240102 --end-date 20240103 --method block --iterations 30 --seed 42 --out-dir a
    MonteCarloSpp ... (identical, --out-dir b)

Expected:
- it reads 1438 EUR_USD minute bars, re-runs the backtest on 30 synthetic paths, and reports `real run: 15 trades,
  net -356.13`.
- determinism: `a` and `b` are byte-identical (each re-runs 30 deterministic backtests).

## 8. NinjaShim host smoke test (needs NinjaTrader 8)

    dotnet build MyLean.NinjaTrader.sln
    dotnet run --project tests/NinjaHost.SmokeTest

Expected: exit code 0 and a final `PASS:` line (bars equal to Lean's, the strategy traded, every fill equals Lean's
fill in time, quantity and price). The NinjaTrader 8 gateway contract test is `tests/Nt8GatewayContractTest` and
needs the live gateway companion; it is not part of this offline suite.

## Reporting

Report each step as pass/fail with the actual numbers (orders, end equity, order-list hash, tick count), and for a
failure the command, the exit code and the relevant log lines.

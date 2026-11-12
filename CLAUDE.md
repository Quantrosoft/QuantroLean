# QuantroLean — notes for an AI assistant

You are helping someone use this repository. `README.md` is the full guide (layout, build, run, every feature). This
file says what the repository can do and how to drive it without breaking anything. Read both before the first build.

QuantroLean is a set of projects on top of [QuantConnect Lean](https://github.com/QuantConnect/Lean), the open-source
algorithmic trading engine. Lean is consumed as the official `QuantConnect.*` NuGet packages; this repository holds the
launchers, example algorithms, a data folder and extensions.

## Language

Talk to the user in the user's language: answers, questions, plans and reports. Keep in ENGLISH: this documentation,
the code, the comments and everything the programs print — quote program output and log lines verbatim in English and
explain them in the user's language.

## What this repository can do

See the feature table in `README.md`. In short:

- **Lean as the official NuGet packages**, one version for all of them (`Directory.Packages.props`), with the launcher,
  optimizer and downloader as projects of this solution.
- **Example algorithms** in C# (`src/Algorithms/QuantroLeanAlgo.CSharp`) and Python (`src/Algorithms/QuantroLeanAlgo.Python`).
- **Run NinjaTrader 8 strategy source unchanged on Lean** (the NinjaShim): backtest a NinjaScript strategy on Lean's
  tick data, with tick-exact equity-drawdown measurement. Needs a local NinjaTrader 8 for its stock indicators and
  bars types.
- **NinjaTrader 8 as a Lean brokerage** (futures): live connection, accounts and market data of a local NinjaTrader 8
  through a companion gateway process. The gateway companion is bundled (binaries only, no source) and copied next to
  the launcher, so a clone live-connects out of the box; `NinjaTrader8LiveTicksAlgorithm` is a read-only live-ticks
  example.
- **Dukascopy** as a free source of historical tick data (forex and CFDs): an in-repo data-source project
  (`src/DataSources/Dukascopy`), referenced by the launcher and downloader, so a clone downloads out of the box.
- **Monte Carlo** analysis of a strategy: resampling the closed trades (`tools/MonteCarlo`), and re-running the
  backtest on synthetic price paths (`tools/MonteCarloSpp`).

## Build

Requires the .NET 10 SDK.

    dotnet build QuantroLean.sln

With NinjaTrader 8 installed (Windows only):

    dotnet build QuantroLean.NinjaTrader.sln

## Sample data

The examples run on Lean's sample data, which is not part of this repository. `tools/SampleData` puts it into `data/`
and checks every file against its size and SHA-256 at the Lean commit of the package version; a file with other content
stops it. Market data (ticks, bars, factor files) is never committed.

    dotnet run --project tools/SampleData -- --to data --download

## Run a backtest

Each tool reads `config.json` from the current directory, so run the tool from its output folder.

    cd src/Lean/QuantroLeanLauncher/bin/Debug
    QuantroLeanLauncher

The default configuration backtests the example `SmaCrossoverAlgorithm` on the SPY minute sample data. Command-line
options override the configuration, for example `--algorithm-type-name`, `--algorithm-location` and `--data-folder`.
Python algorithms need Lean's Python runtime (see `README.md`).

The launcher exits automatically when the run finishes (`close-automatically` is `true` in `config.json`; set it to
`false` to keep the console open until a key press). On the sample data two `ERROR` lines are expected and harmless:
`map_files` and `interest-rate.csv` are missing — the examples use neither equity symbol mapping nor an interest-rate
curve, so the results are unaffected.

## Optimize

    cd src/Lean/QuantroLeanOptimizer/bin/Debug
    QuantConnect.Optimizer.Launcher

The optimizer's `config.json` searches the example's parameters; each backtest runs as a separate `QuantroLeanLauncher` process.

## Download historical data

    cd src/Lean/QuantroLeanDownloader/bin/Debug
    QuantroLeanDownloader --tickers <SYMBOLS> --data-downloader <NAME> --data-type Quote --security-type <TYPE> \
                 --market <MARKET> --resolution Tick --start-date YYYYMMDD --end-date YYYYMMDD

Downloaded ticks are written to the data folder and also aggregated to minute, hour and daily quote bars, with UTC as
the data and exchange time zone. For the free Dukascopy source see the next section.

## NinjaTrader 8 strategies on Lean (NinjaShim)

A NinjaScript strategy (a `NinjaTrader.NinjaScript.Strategies.Strategy` subclass, written for NinjaTrader 8) is compiled
unchanged together with the shim and the Lean host into one algorithm assembly and run by Lean as `NinjaScriptAlgorithm`:

    dotnet build QuantroLean.NinjaTrader.sln
    cd src/Lean/QuantroLeanLauncher.NinjaTrader/bin/Debug
    QuantroLeanLauncher.NinjaTrader

What it does and does not do:

- The strategy's bars are built from Lean's ticks; with `verify-bars` each bar is compared with Lean's own
  consolidation and a difference stops the run. Fills follow NinjaTrader's rules (buy at ask, sell at bid, stops on the
  opposite side, stop loss and target checked tick by tick, open positions closed at the regular session end). Every
  order goes to Lean as a market order; the host compares the positions after every tick and stops on a difference, and
  measures the equity drawdown itself with the open position at its exit price.
- Equities, forex, CFDs and futures. Primary series: time (`Minute,N` / `Second,N`), tick count (`Tick,N`), and the
  hosted NinjaTrader bars types `Range,N` and `Renko,N` (these need the shim built with a local NinjaTrader 8 install).
- NinjaTrader's chart drawing and GUI are stubs. The ATM API (`AtmStrategyCreate` and the rest) does not exist in the
  shim; a strategy that calls it does not compile.

`tests/NinjaHost.SmokeTest` runs the example end to end and checks bars, trades and every fill against Lean (exit code
0 = pass); `--config <file>` runs another configuration.

## NinjaTrader 8 brokerage (live)

`src/Brokerages/NinjaTrader8` is a Lean brokerage and live data source for NinjaTrader 8 futures, in the same shape as
Lean's other brokerages. It trades one account of one connection configured in a local NinjaTrader 8, through a
companion gateway process it talks to over a local TCP socket. Configure it in `config.json` (keys in `README.md`).
Market, limit, stop market and stop limit orders; Day or GoodTilCanceled; whole futures contracts. History is not
served. On connect it checks the gateway's wire-protocol version (the `hello` reply's first field) and stops loudly if
it does not match the version the brokerage was built for. The gateway companion is bundled as binaries only (no
source) in `src/Brokerages/NinjaTrader8/gateway/`, copied next to the launcher so a clone live-connects out of the box
and `ninjatrader8-gateway` defaults to it; it is a separate, licensed build and keeps its native core current itself.
Run it live
with the `live-ninjatrader8` environment; `NinjaTrader8LiveTicksAlgorithm` is a read-only example that streams one
futures contract's live ticks to the console (parameter `nt8-contract`, e.g. `MNQ 12-26`) and places no orders.

## Dukascopy historical data (free)

The data source `QuantConnect.DataSource.Dukascopy` (`src/DataSources/Dukascopy`) downloads free historical tick
data for forex and CFDs from Dukascopy's public feed. It is part of this repository and is referenced by
`QuantroLeanLauncher` and `QuantroLeanDownloader`, so a clone can download Dukascopy data out of the box. Run a download through
`QuantroLeanDownloader`:

    QuantroLeanDownloader --tickers EUR_USD --data-downloader DukascopyDataDownloader --data-type Quote \
                 --security-type Cfd --market dukascopy --resolution Tick --start-date 20240102 --end-date 20240103

Notes:

- Symbols use Lean's Dukascopy names (`EUR_USD`, `XAU_USD`, `DEU.IDX_EUR`, ...); the full list is the `dukascopy`
  market in `data/symbol-properties/symbol-properties-database.csv`. The Dukascopy instrument code and the price scale
  are read from that file (its market ticker and minimum price variation), so there is no hard-coded table.
- Times are UTC. The feed serves one LZMA-compressed file per instrument and hour; hours without data (weekends,
  holidays, before the instrument existed) are simply absent.
- The feed throttles heavy use, so large ranges are slow. The downloader paces its requests and retries transient
  throttling (HTTP 503/429) patiently; a truly corrupt file stops the run rather than writing a gap.

## Monte Carlo

`tools/MonteCarlo` resamples a strategy's closed-trade profits (reshuffle, bootstrap, block bootstrap, stationary
bootstrap) and reports the distribution of net profit, equity drawdown, profit factor, Sharpe ratio and final
equity (mean/median/min/max/P5/P95, histograms, the real run's percentile) with the risk measures P(profitable),
risk of ruin, value at risk, conditional VaR and equity confidence bands. Input is a CSV of closed-trade profits,
for example the closed trades of a QuantroLean backtest result (`totalPerformance.closedTrades[].profitLoss`). Output is
CSV / JSON / optional HTML; the same seed gives an identical result. `tools/MonteCarloSpp` is the synthetic price
path method: it resamples the backtest's bar returns, writes synthetic Lean data and re-runs the whole backtest on
each path (so it is much slower). Drawdown always means the equity drawdown, built from the trades, not from daily
returns. Both tools are an independent implementation from the methods' textbook definitions.

    cd tools/MonteCarlo/bin/Debug
    MonteCarlo --trades trades.csv --method bootstrap --iterations 2000 --seed 42 --html

## Reporting results

- Report numbers only from the result files and name the file. "Drawdown" means the equity drawdown, which the
  NinjaShim host measures per tick and writes next to Lean's results as `<strategy>-<symbol>-trades.csv`.
- A non-zero exit code is a result, not something to retry blindly; read the log and quote the relevant lines.

## Rules for an assistant

1. Never commit market data; it is fetched and git-ignores it.
2. Keep the code, the comments, `README.md` and this file in sync — after any change, no stale names or paths remain.
3. Build before you say it is done. For the NinjaShim, the smoke test's exit code 0 is the pass.
4. The NinjaTrader bars types and the live brokerage need a local NinjaTrader 8; without it, build `QuantroLean.sln` only.

## License

Source files written for this repository carry an MIT header (Quantrosoft Pte. Ltd.); files copied from Lean keep
their Apache License 2.0 header; the umbrella `LICENSE.txt` is Apache License 2.0. The bundled NinjaTrader 8 gateway
binaries in `src/Brokerages/NinjaTrader8/gateway/` (`NinjaTrader8.Gateway.exe`, `NinjaTrader8.Gateway.exe.config`,
`nt8native.dll`) are **not** open source: they are proprietary (Copyright &copy; Quantrosoft Pte. Ltd., all rights
reserved) and licensed only under Quantrosoft's own terms &mdash; see `src/Brokerages/NinjaTrader8/gateway/LICENSE.txt`.

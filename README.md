# QuantroLean

Projects on top of [QuantConnect Lean](https://github.com/QuantConnect/Lean), the open-source algorithmic trading
engine. Lean is consumed as the official `QuantConnect.*` NuGet packages; this repository holds the launchers,
example algorithms, a data folder and extensions.

## Features

| Feature | Status |
|---|---|
| Lean as the official NuGet packages, one version for all of them, weekly Dependabot updates | available |
| Launcher, optimizer and downloader as projects of this solution | available |
| Downloader: downloaded ticks are also aggregated to minute, hour and daily quote bars | available |
| Example algorithm in C# and Python, with a tool that fetches the sample data from Lean bit-exact | available |
| Market hours and symbol properties for Dukascopy forex and CFD symbols | available |
| Run NinjaTrader 8 strategy source code unchanged on Lean (NinjaShim), backtest on Lean's tick data with tick-exact equity-drawdown measurement | available: equities, forex, CFDs, futures; primary series time / tick / Range / Renko bars |
| NinjaTrader 8 strategies live through Lean's brokerages | built in, not yet run live |
| NinjaTrader 8 as a Lean brokerage: live connection, accounts and market data of a local NinjaTrader 8 (futures) | available; orders built, not yet run live |
| Gateway companion bundled (binaries only, no source) and copied next to the launcher, so a clone live-connects out of the box; the host keeps its native core current itself | available |
| Read-only live example: stream a NinjaTrader 8 contract's ticks to the console (NinjaTrader8LiveTicksAlgorithm) | available; needs a local NinjaTrader 8 and the gateway companion |
| Dukascopy as free source of historical data, mainly forex and CFDs | available |
| Monte Carlo analysis of a strategy: trade resampling (reshuffle, bootstrap, block, stationary) and synthetic price paths | available |

## Layout

| Path | Content |
|---|---|
| `Directory.Packages.props` | package versions; all `QuantConnect.*` packages share one Lean version |
| `Directory.Build.props` | repository-wide MSBuild properties |
| `QuantroLean.sln` | everything that builds without NinjaTrader |
| `QuantroLean.NinjaTrader.sln` | the NinjaScript projects; needs NinjaTrader 8 on the PC |
| `src/Lean/QuantroLeanLauncher` | Lean's `Launcher`: backtesting and live trading (`Program.cs` unchanged) |
| `src/Lean/QuantroLeanLauncher.NinjaTrader` | the same launcher on the Windows desktop runtime, for NinjaScript algorithms |
| `src/Lean/QuantroLeanOptimizer` | Lean's `Optimizer.Launcher` (unchanged); runs every backtest as a QuantroLeanLauncher process |
| `src/Lean/QuantroLeanDownloader` | Lean's `DownloaderDataProvider`; downloaded ticks are also aggregated to minute, hour and daily quote bars |
| `src/Algorithms/QuantroLeanAlgo.CSharp` | C# algorithms (assembly `QuantroLeanAlgo.CSharp.dll`) |
| `src/Algorithms/QuantroLeanAlgo.Python` | Python algorithms |
| `src/Algorithms/QuantroLeanAlgo.NinjaScript` | NinjaScript strategies, compiled with the NinjaShim and the Lean host |
| `src/NinjaShim` | NinjaScript compatibility layer: NinjaTrader 8's strategy API, run by a host on any engine |
| `src/NinjaHost` | the Lean host of the NinjaShim (`NinjaScriptAlgorithm`) |
| `src/Brokerages/NinjaTrader8` | Lean brokerage and live data source for NinjaTrader 8 (futures), through a companion gateway process |
| `src/DataSources/Dukascopy` | Dukascopy data source: free historical ticks for forex and CFDs (an `IDataDownloader`) |
| `tools/SampleData` | fetches Lean's sample data for the examples into `data/` |
| `tools/MonteCarlo` | Monte Carlo analysis of a strategy's closed-trade profits (resampling) |
| `tools/MonteCarloSpp` | Monte Carlo on synthetic price paths (re-runs the backtest on resampled price series) |
| `tests/NinjaHost.SmokeTest` | end-to-end test of the NinjaScript host |
| `tests/BarStreamFormingTest` | unit test of the NinjaShim forming-bar logic |
| `tests/Nt8GatewayContractTest` | test of the NinjaTrader 8 gateway wire protocol (needs the gateway companion) |
| `data` | Lean data folder: market hours, symbol properties, map files; market data is fetched, never committed |

## Build

Requires the .NET 10 SDK.

    dotnet build QuantroLean.sln

With NinjaTrader 8 installed (Windows):

    dotnet build QuantroLean.NinjaTrader.sln

`docs/TESTING.md` lists commands and the expected results to verify every feature on a clean checkout.

## Sample data

The examples run on Lean's sample data, which is not part of this repository. `tools/SampleData` puts it into
`data/` and checks every file against its size and SHA-256 at the Lean commit of the package version; a file with
other content stops it. Take the data from a local clone of Lean, or download it from Lean's GitHub repository:

    dotnet run --project tools/SampleData -- --to data --from <Lean clone>/Data
    dotnet run --project tools/SampleData -- --to data --download

## Run

Each tool reads `config.json` from the current directory. The build copies it into the tool's output folder, and
the relative paths in it are relative to that folder, so run the tools from there.

Backtest:

    cd src/Lean/QuantroLeanLauncher/bin/Debug
    QuantroLeanLauncher

The default configuration backtests the example `SmaCrossoverAlgorithm` on the SPY minute sample data
(2013-10-07 to 2013-10-11). Command line options override the configuration, for example
`--algorithm-type-name`, `--algorithm-location` and `--data-folder`.

Python algorithms: set `"algorithm-language"` to `"Python"` and `"algorithm-location"` to the `.py` file; both
lines are prepared in `config.json`. Lean needs Python 3.11 and the environment variable `PYTHONNET_PYDLL`, see
Lean's [Algorithm.Python/readme.md](https://github.com/QuantConnect/Lean/blob/master/Algorithm.Python/readme.md).

Optimization:

    cd src/Lean/QuantroLeanOptimizer/bin/Debug
    QuantConnect.Optimizer.Launcher

The optimizer's `config.json` searches the example's parameters `fast-period` and `slow-period`. The optimizer
assembly keeps Lean's name `QuantConnect.Optimizer.Launcher` because Lean's Composer collects exported types only
from `QuantConnect.*.dll` files.

## NinjaTrader 8 strategies on Lean

A NinjaScript strategy (a `NinjaTrader.NinjaScript.Strategies.Strategy` subclass, as written for NinjaTrader 8) is
compiled unchanged together with the NinjaShim and the Lean host into one algorithm assembly
(`src/Algorithms/QuantroLeanAlgo.NinjaScript`). Lean runs it as the algorithm `NinjaScriptAlgorithm`:

    dotnet run --project tools/SampleData -- --to data --from <Lean clone>/Data
    dotnet build QuantroLean.NinjaTrader.sln
    cd src/Lean/QuantroLeanLauncher.NinjaTrader/bin/Debug
    QuantroLeanLauncher.NinjaTrader

The configuration runs the example `SmaCrossoverStrategy` on SPY's ticks of 2013-10-07. Its parameters
(`config.json`, `"parameters"`) are described in `src/NinjaHost/NinjaScriptAlgorithm.cs`: the strategy class, the
security, the period, the primary bar series (`Minute,N`, `Second,N`, `Tick,N`) and `p_<Property>` values for the
strategy's own properties.

How it runs:

- The strategy's bars are built from Lean's ticks. With `verify-bars` every bar is compared with Lean's own
  consolidation of the same ticks (open, high, low, close, volume, end time); a difference stops the run.
- Fills follow NinjaTrader's rules in the shim: a buy fills at the ask, a sell at the bid, a long stop triggers on
  the bid, a short stop on the ask; stop loss and profit target are checked tick by tick, open positions are closed
  at the end of the regular session. Within one time step the quotes are handed over before the trades (trades and
  quotes are separate recordings, their order inside the step is unknown).
- Every order of the strategy is sent to Lean as a market order. In a backtest the host compares Lean's position
  with the strategy's after every tick and stops on a difference. Live, the orders go to the brokerage the launcher
  is configured with; stop loss and profit target then exist only in the shim, not as resting orders at the broker.
- The host measures the equity drawdown itself on every tick, with the open position at its exit price, and writes
  the strategy's trade list (`<strategy>-<symbol>-trades.csv`) next to Lean's results.

`tests/NinjaHost.SmokeTest` runs the example and checks bars, trades and every fill against Lean (exit code 0 =
pass); with `--config <file>` it runs another configuration, for example one that must fail.

What it needs and what it does not do:

- NinjaTrader 8 on the PC: NinjaTrader's stock indicators and bars types (`@SMA.cs`, `@RenkoBarsType.cs`, ...) are
  linked from its `bin\Custom` folder at build time (`src/NinjaShim/NinjaTrader8.props`) and never copied here.
- Equities, forex, CFDs and futures. A future is one dated contract (`security-type: Future`, `symbol`, `market`
  e.g. `cme`, `contract-expiry: yyyy-MM-dd`); a window that crosses the contract's roll is run per contract.
- Primary series: time (`Minute,N` / `Second,N`), tick count (`Tick,N`), and the hosted NT8 bars types `Range,N`
  and `Renko,N` (these need the NinjaShim built with NINJASHIM_NT8_BARSTYPES, i.e. a local NinjaTrader 8 install;
  `verify-bars` is not available for them). `reset-on-new-day` restarts a hosted series each trading day.
- Time bars align to UTC multiples of the period, so daily bars are UTC days. The session end is the regular close
  of the first trading day; early closes of single days are not taken over.
- Chart drawing and NinjaTrader's GUI are stubs without effect. ATM strategies (`AtmStrategyCreate` and the rest of
  NinjaTrader's ATM API) do not exist in the shim; a strategy that calls them does not compile.

## NinjaTrader 8 brokerage (live)

`src/Brokerages/NinjaTrader8` is a Lean brokerage and live data source (`IDataQueueHandler`) for NinjaTrader 8
futures, in the same shape as Lean's other brokerages. It trades one account of one connection configured in a local
NinjaTrader 8, through a companion gateway process that the brokerage starts and talks to over a local TCP socket.

Configure it in `config.json` (or the live job's brokerage data):

| Key | Value |
|---|---|
| `ninjatrader8-gateway` | path of the gateway host; defaults to the bundled `gateway/NinjaTrader8.Gateway.exe` next to the launcher |
| `ninjatrader8-connection` | name of the connection as configured in NinjaTrader; may be left empty (see below) |
| `ninjatrader8-account` | name of the account on that connection; may be left empty (see below) |
| `ninjatrader8-user-data-dir` | optional NinjaTrader data folder for the gateway (default: NinjaTrader's own; one process per data folder) |
| `ninjatrader8-start-timeout` | optional seconds until the gateway must be ready (default 300) |

If `ninjatrader8-connection` or `ninjatrader8-account` is empty or does not match a configured one, the gateway shows
a dropdown on the desktop, and the brokerage writes the chosen name back into this `config.json` (only that one value
changes, comments and the rest stay), so the next start passes it and no dropdown appears. Clear the stored value to
be asked again, or set the right name by hand. On a machine without an interactive desktop (a service or Session 0)
the dropdown cannot show, so a valid connection and account must be set there.

It needs a local NinjaTrader 8. The gateway companion is bundled with this repository as binaries only (no source) in
`src/Brokerages/NinjaTrader8/gateway/` and copied next to the launcher, so a clone live-connects out of the box; it is
a separate, licensed build and keeps its native core up to date itself (see that folder's `README.md`). Market and
limit, stop market and stop limit orders, Day or GoodTilCanceled, whole futures contracts; history is not served.

On connect the brokerage checks the gateway's wire-protocol version (the first field of its `hello` reply) against the
version it was built for and stops loudly if they differ: a higher version means the gateway is newer and this
brokerage must be updated to the matching release, a lower one means the gateway must be updated. A plain gateway
update keeps the protocol version, so the brokerage keeps working across gateway releases unless the wire format
actually changed.

Run it live with the `live-ninjatrader8` environment (the bundled gateway is the default; set the Demo/Sim
`ninjatrader8-connection` / `ninjatrader8-account` in `config.json`, or leave them empty to pick from a dropdown):

    QuantroLeanLauncher --environment live-ninjatrader8 --algorithm-type-name NinjaTrader8LiveTicksAlgorithm

`NinjaTrader8LiveTicksAlgorithm` (`src/Algorithms/QuantroLeanAlgo.CSharp`) is a read-only example: it streams the live ticks of
one futures contract (parameter `nt8-contract`, a NinjaTrader name like `MNQ 12-26`) to the console and places no
orders.

## Data

`data/` has Lean's data folder layout. `market-hours/market-hours-database.json` and
`symbol-properties/symbol-properties-database.csv` are Lean's files at the package version, extended by entries for
the market `dukascopy`: the CFD market hours `Cfd-dukascopy-[*]` and 1,148 CFD symbol definitions. Market data (bars,
ticks, factor files) comes from `tools/SampleData` or the downloader and is ignored by git.

## Dukascopy

Free historical tick data for forex and CFDs from Dukascopy's public feed. The data source is a project of this
repository, `src/DataSources/Dukascopy` (assembly `QuantConnect.DataSource.Dukascopy`), referenced by `QuantroLeanLauncher`
and `QuantroLeanDownloader`, so a clone can download Dukascopy data out of the box. It has no runtime NuGet dependency of its
own: the LZMA decoder it needs is the public-domain LZMA SDK, compiled in (`src/DataSources/Dukascopy/Lzma`).

Download (see also `src/Lean/QuantroLeanDownloader/Load.bat`), from the downloader's output folder:

    QuantroLeanDownloader --tickers EUR_USD --data-downloader DukascopyDataDownloader --data-type Quote \
                 --security-type Cfd --market dukascopy --resolution Tick --start-date 20240102 --end-date 20240103

The ticks are written and also aggregated to minute, hour and daily quote bars, with UTC as the data and exchange
time zone. Symbols use Lean's Dukascopy names (`EUR_USD`, `XAU_USD`, `DEU.IDX_EUR`, ...); the full list is the
`dukascopy` market in `data/symbol-properties/symbol-properties-database.csv`. The Dukascopy instrument code and the
price scale come from that file, so there is no hard-coded table. The feed throttles heavy use, so large ranges are
slow; the downloader paces its requests and retries transient throttling patiently. `DukascopyExampleAlgorithm`
(`src/Algorithms/QuantroLeanAlgo.CSharp`) backtests a moving-average crossover on the EUR_USD CFD using this data.

## Monte Carlo

`tools/MonteCarlo` resamples a strategy's closed-trade profits to estimate how much of a backtest's result is luck
and what the downside looks like. Methods: reshuffle, i.i.d. bootstrap, circular block bootstrap and the stationary
bootstrap. Per-metric distributions (net profit, equity drawdown, profit factor, Sharpe ratio, final equity) with
mean/median/min/max/P5/P95 and a histogram, plus the backtesting percentile of the real run; risk measures
P(profitable), risk of ruin, value at risk, conditional VaR and equity confidence bands. Input is a CSV of
closed-trade profits (for example the closed trades of a QuantroLean backtest result). Output is CSV, JSON and an optional
HTML page; it is deterministic for a given seed. See `tools/MonteCarlo/README.md`.

`tools/MonteCarloSpp` is the synthetic price path method: it resamples the backtest's bar returns into synthetic
price series, writes each as Lean data and re-runs the whole backtest on it, then analyses the distribution of the
result metrics with the same statistics. It drives `QuantroLeanLauncher`, so it is much slower than the trade resampling.
See `tools/MonteCarloSpp/README.md`.

Drawdown always means the equity drawdown, built from the trade profits (or the host's tick-exact equity), not from
daily returns. The Monte Carlo is an independent implementation written from the methods' textbook definitions.
`docs/montecarlo.html` is a short overview.

## Updating Lean

1. Set the new version on all `QuantConnect.*` entries in `Directory.Packages.props`. Dependabot
   (`.github/dependabot.yml`) opens a pull request for new versions once a week.
2. Look up the Lean commit the packages were built from: the `<repository ... commit="...">` element in the
   `.nuspec` of `QuantConnect.Lean.Engine`.
3. Merge Lean's changes between the old and the new commit into the copied files in `src/Lean` and the data files
   in `data/` (three-way: this repository / Lean at the old commit / Lean at the new commit).
4. Set the new commit and the files' sizes and SHA-256 in `tools/SampleData/Program.cs`.

Current: `2.5.18248` = Lean commit `a99fba023df877bd2301c14b4db37f40101d273c`.

## Changes to Lean files

Files copied from Lean and changed here say so in their header (Apache License 2.0, section 4(b)):

| File | Change |
|---|---|
| `src/Lean/QuantroLeanDownloader/Program.cs` | ticks are also aggregated to minute, hour and daily quote bars; data is written with UTC as data and exchange time zone |
| `src/Lean/QuantroLeanDownloader/Models/BrokerageDataDownloader.cs` | the live node packet carries only the brokerage name and the deployment target |
| `src/Lean/QuantroLeanLauncher/config.json`, `src/Lean/QuantroLeanLauncher.NinjaTrader/config.json` | algorithm selection, parameters and data folder |
| `src/Algorithms/QuantroLeanAlgo.Python/AlgorithmImports.py` | the imports of `QuantConnect.Research`, `QuantConnect.Securities.IndexOption` and `QuantConnect.Indicators.CandlestickPatterns` are removed |
| `data/market-hours/market-hours-database.json`, `data/symbol-properties/symbol-properties-database.csv` | Dukascopy entries added (the formats have no comments, so the notice is here) |

`src/Lean/QuantroLeanOptimizer/config.json` and `src/Lean/QuantroLeanDownloader/config.json` are this repository's settings, written
after Lean's `config.example.json` of the same tools.

## License

Files written for this repository carry the MIT License header of Quantrosoft Pte. Ltd. Files copied from Lean keep
their Apache License 2.0 header (Lean is Copyright QuantConnect Corporation). NinjaTrader's own NinjaScript sources
are not part of this repository; the build links them from the local NinjaTrader 8 installation. All other source
files: Apache License 2.0, see [LICENSE.txt](LICENSE.txt).

**The bundled NinjaTrader 8 gateway binaries are not open source.** The files in
`src/Brokerages/NinjaTrader8/gateway/` &mdash; `NinjaTrader8.Gateway.exe`, `NinjaTrader8.Gateway.exe.config` and
`nt8native.dll` &mdash; are proprietary software, Copyright &copy; Quantrosoft Pte. Ltd., all rights reserved. They
are **not** covered by the MIT or Apache License above; they are a separate, licensed build bundled for convenience,
and their use is governed by Quantrosoft's own licence terms (see
[`src/Brokerages/NinjaTrader8/gateway/LICENSE.txt`](src/Brokerages/NinjaTrader8/gateway/LICENSE.txt)). Redistribution,
modification and reverse engineering are not permitted.

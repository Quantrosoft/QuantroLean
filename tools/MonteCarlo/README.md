# MonteCarlo

Monte Carlo analysis of a strategy's **closed-trade profits**: it resamples the trades many times to estimate how
much of a backtest's result is luck, and what the downside looks like. Self-contained (no NuGet or Lean dependency),
and **deterministic** &mdash; the same seed gives a byte-for-byte identical result.

This is an independent implementation: the resampling methods and risk measures are written from their textbook
definitions, no code is taken from any other backtester or Monte Carlo tool.

## Input

A CSV with one closed trade per row and a profit column, in account currency:

    profit
    125.0
    -60.0
    ...

The column is taken by `--profit-column`, or auto-detected as the first of `profit`, `pnl`, `netprofit`,
`profit_loss`, `profitloss`. A file without a header is read as one profit per line. The profits are the real run's,
in trade order (the order matters for drawdown).

To get such a file from a MyLean backtest, export the closed trades' profit-and-loss from Lean's result, or, for a
NinjaShim run, take the host's `<strategy>-<symbol>-trades.csv` and convert its points to money with the instrument's
point value.

## Methods

| Method | What it resamples |
|---|---|
| `reshuffle` | a random permutation of the trades (no replacement): same trades, new order |
| `bootstrap` | the i.i.d. bootstrap: n trades drawn with replacement |
| `block` | the circular block bootstrap: contiguous blocks of `--block-size`, drawn with replacement, wrapping around |
| `stationary` | the stationary bootstrap (Politis & Romano 1994): blocks of random length, mean `--block-size` |

Reshuffle leaves order-independent metrics unchanged (net profit, profit factor, Sharpe) and varies only what depends
on the order (drawdown, equity path). The bootstraps vary all of them.

## Metrics (per simulation)

NetProfit, MaxDrawdown (equity drawdown in money), MaxDrawdownPercent, ProfitFactor, SharpeRatio (of the per-trade
profits, not annualized) and FinalEquity. For each: Mean, Median, Min, Max, P5, P95, a histogram, and the real run's
**backtesting percentile** (the fraction of simulations at or below the real value).

Drawdown is the **equity drawdown** (peak to trough of the running equity), per the house rule &mdash; it is built
from the trade profits, not from daily returns.

## Risk measures

- **P(profitable)**: fraction of simulations with a positive net profit.
- **Risk of ruin**: fraction of simulations whose equity ever falls to or below the ruin threshold
  (`initial-capital * (1 - ruin-level)`).
- **Value at risk / Conditional VaR** at `--var-confidence` (default 95%), on the net-profit distribution, reported
  as positive losses.
- **Equity confidence bands**: the P5 / P50 / P95 of the simulated equity after each trade.

## Usage

    cd tools/MonteCarlo/bin/Debug
    MonteCarlo --trades trades.csv --method bootstrap --iterations 2000 --seed 42 --initial-capital 100000 --html

Options: `--trades` (required), `--profit-column`, `--initial-capital` (100000), `--method`
(reshuffle/bootstrap/block/stationary, default bootstrap), `--iterations` (2000), `--seed` (42), `--block-size`
(default round(sqrt(trades))), `--ruin-level` (0.5), `--var-confidence` (0.95), `--bins` (20), `--out-dir`,
`--prefix`, `--html`. `--help` prints the full list.

Outputs: `<prefix>_simulations.csv` (one row per simulation), `<prefix>_summary.json` (distributions, risk measures,
equity bands), and with `--html` a self-contained `<prefix>.html`. The same seed and input produce identical files;
no wall-clock time is written.

## Determinism

The generator is a fixed xoshiro256** seeded through SplitMix64, not the platform `Random`, so a given seed yields
the same sequence on any machine and .NET version. Numbers are written with the invariant culture and the round-trip
format.

## Synthetic price paths

This tool resamples the *trades*. The sibling tool `../MonteCarloSpp` is the synthetic price path method: it
resamples the backtest's *bar returns* into synthetic price series and re-runs the whole backtest on each one. It
shares this tool's statistics and reporting. See `../MonteCarloSpp/README.md`.

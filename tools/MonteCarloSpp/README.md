# MonteCarloSpp

The **synthetic price path** Monte Carlo: it resamples the bar returns of a backtest's price series into synthetic
price series, writes each one as Lean data, and **re-runs the whole QuantroLean backtest on it**, then analyses the
distribution of the result metrics with the same statistics and risk measures as the trade-resampling
[`../MonteCarlo`](../MonteCarlo/README.md). Because every path is a full backtest, this is much slower than
resampling the trades &mdash; use a modest iteration count.

Independent implementation: the method (resample the bar returns, rebuild a price series, re-run the backtest) is
written from its textbook definition; no code is taken from any other tool.

## How it works

1. Read the base minute quote bars of the symbol over the day range from the Lean data folder.
2. Take the log returns of the mid close and resample them (reshuffle / bootstrap / block / stationary).
3. Rebuild a synthetic mid-close path from the resampled returns (same first price) and translate each original
   bar's bid and ask to the synthetic mid, keeping the spread and the intrabar shape.
4. Write the synthetic bars as Lean data (Lean's own `LeanDataWriter`) into a copy of the data folder, and run
   `QuantroLeanLauncher` on it.
5. Read each run's closed-trade profits, compute the metrics, and summarize the distribution and the risk measures.

The real run (the backtest on the original prices) is reported alongside, with its backtesting percentile.

## Usage

    cd tools/MonteCarloSpp/bin/Debug
    MonteCarloSpp --data-folder <QuantroLean>/data \
                  --launcher <QuantroLean>/src/Lean/QuantroLeanLauncher/bin/Debug/QuantroLeanLauncher.exe \
                  --start-date 20240102 --end-date 20240103 \
                  --algorithm-type-name DukascopyExampleAlgorithm --symbol EUR_USD --market dukascopy \
                  --method block --iterations 200 --seed 42 --html

Required: `--data-folder`, `--launcher`, `--start-date`, `--end-date` (the minute quote data must exist for the
range; end is exclusive). Options: `--algorithm-type-name` (DukascopyExampleAlgorithm), `--symbol` (EUR_USD),
`--market` (dukascopy), `--security-type` (Cfd), `--method` (reshuffle/bootstrap/block/stationary, default block),
`--iterations` (200), `--seed` (42), `--block-size` (default round(sqrt(returns))), `--initial-capital` (100000),
`--ruin-level` (0.5), `--var-confidence` (0.95), `--bins` (20), `--out-dir`, `--prefix`, `--work-dir`,
`--keep-work`, `--html`. `--help` prints the full list.

Outputs: `<prefix>_simulations.csv`, `<prefix>_summary.json` and with `--html` `<prefix>.html`. Deterministic for a
given seed (the synthetic data and the backtests are both deterministic), so the same seed and inputs produce
identical files.

## Notes

- Works on minute quote data (forex / CFDs), for example the Dukascopy EUR_USD data downloaded with `QuantroLeanDownloader`.
- Equity confidence bands over the trade index are not produced here (synthetic runs have different trade counts);
  the metric distributions and the risk measures are.
- Drawdown is the equity drawdown, built from the trades of each re-run, per the house rule.

## License

MIT, see the repository `LICENSE.txt`.

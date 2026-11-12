# QuantConnect.DataSource.Dukascopy

A free historical data source for Lean: `DukascopyDataDownloader` implements Lean's `IDataDownloader` and fetches
tick data for forex and CFDs from [Dukascopy](https://www.dukascopy.com)'s public data feed. It is part of this
repository; `QuantroLeanLauncher` and `QuantroLeanDownloader` reference it, so a clone can download Dukascopy data out of the box.

Independent implementation: the downloader is written from Dukascopy's public `.bi5` feed format; no code is taken
from any third party. The files under `Lzma/` are the public-domain LZMA SDK by Igor Pavlov (see
`Lzma/LZMA-SDK-NOTICE.txt`), so the assembly has no runtime NuGet dependency of its own.

## How it works

Dukascopy serves one LZMA-compressed file per instrument and hour at
`https://datafeed.dukascopy.com/datafeed/{INSTRUMENT}/{YYYY}/{MM}/{DD}/{HH}h_ticks.bi5` (month is zero based, hour is
GMT = UTC). Each file decompresses to 20-byte big-endian records: milliseconds since the hour, ask and bid in the
instrument's points, and ask and bid volume. The Dukascopy instrument code and the price scale are read from Lean's
symbol-properties database (the `dukascopy` market's market ticker and minimum price variation), so there is no
hard-coded instrument table. Times are UTC throughout. Hours without data (weekends, holidays, before the instrument
existed) are absent on the feed and skipped; a corrupt file stops the run rather than writing a gap.

## Use

Lean resolves the downloader by type name, so `QuantroLeanDownloader` drives it with `--data-downloader
DukascopyDataDownloader` (see the repository README, "Dukascopy"):

    QuantroLeanDownloader --tickers EUR_USD --data-downloader DukascopyDataDownloader --data-type Quote \
                 --security-type Cfd --market dukascopy --resolution Tick --start-date 20240102 --end-date 20240103

The feed throttles heavy use, so large ranges are slow; the downloader paces its requests and retries transient
throttling (HTTP 503/429) patiently.

## License

MIT (repository `LICENSE.txt`). The files under `Lzma/` are public domain; see `Lzma/LZMA-SDK-NOTICE.txt`.

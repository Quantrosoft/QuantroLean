# BTCACHE protocol - version 1

MIT License - Copyright (c) 2026 Quantrosoft

A protocol that lets many concurrent backtest processes on one machine read ONE shared, in-memory copy of the same
input data instead of each loading and decoding its own. A resident **holder** process owns named shared-memory
sections; **consumers** (backtest processes) ask the holder for a section by key. The first consumer that misses a key
fills the section with its own decoder; every later consumer maps the same section read-only. The holder never decodes
data, so the content is exactly what the consumer's own decoder produces.

This file is the master text of the protocol. A holder or client that implements it names the version it speaks.

## 1. Terms

| term | meaning |
|---|---|
| holder | the resident process that creates, keeps and evicts sections and answers the requests below |
| consumer | a process that reads data through the holder (a backtest, an optimizer pass, ...) |
| filler | the consumer that got the lease for a missing key and writes its payload |
| data root | the folder the holder resolves source paths against |
| key | `<kind>/<rest>` - identifies what a section holds (section 3) |
| source | the data files a section was decoded from, relative to the data root |
| section | a named, memory-backed shared-memory object: a 4096-byte header + the payload |

## 2. Transport

- HTTP/1.1 on the loopback interface only (`127.0.0.1:<port>`; default port 9878). Request and answer bodies are
  UTF-8 JSON (`Content-Type: application/json`).
- `POST` for every request in section 5, `GET /btc/status` for the status.
- A request the holder refuses (malformed key, unknown lease, a source file that does not exist, ...) is answered with
  HTTP 400 and `{"error": "<text>"}`. A consumer treats it as an error of its own run (fail loud), never as "no cache".
- One holder per port and machine: the holder binds its port exclusively.

## 3. Keys and sources

- `key` = `<kind>/<rest>`, `/`-separated, at most 512 characters, no `..` segment, no backslash.
  - `kind` names the payload layout AND its version and is chosen by the consumer that defines the layout, e.g.
    `example.ticks.v1`. A layout change gets a new kind (`...v2`); the holder never converts between kinds.
  - `rest` is free: `<symbol>/<YYYYMMDD>` for day data, or a data path for one section per data file.
- `source` = a list of paths relative to the data root (`/`-separated). The holder records each file's size and last
  write time when it leases the key. A section belongs to exactly that source state: when a source file changes (size or
  write time) or disappears, the section is dropped and the next consumer fills it anew.
- Every consumer that reads the same data must send the same key and the same source paths (resolve links and junctions
  to the real path below the data root first), otherwise it gets a section of its own.

## 4. Section layout

Little-endian. The consumer never builds a section name; it takes the `name` the holder answers.

| bytes | field |
|---|---|
| 0..7 | magic, ASCII `QSBTC001` |
| 8..11 | state, int32: 0 = being filled, 1 = ready |
| 12..15 | header version, int32: 1 |
| 16..23 | payload offset, int64: 4096 |
| 24..31 | payload length in bytes, int64 |
| 32..63 | reserved, zero |
| 64..4095 | header JSON, UTF-8, zero-terminated: `key`, `source` (path, size, write time per file), `filler` (pid, process name), `created_utc`, and once ready `payload_sha256` |
| 4096.. | payload: the consumer's own layout, `length` bytes (the offset keeps it 8-byte aligned) |

The payload is opaque to the holder. A consumer that defines a kind documents its layout itself (record size, field
order, units) next to its kind name.

Section names: on Windows a pagefile-backed file mapping (`CreateFileMapping` on `INVALID_HANDLE_VALUE`) whose name the
holder answers (e.g. `Global\QsBtc_<32 hex>`; a holder in one user session may use the session namespace); on
Linux / POSIX a shared-memory object (`shm_open`, e.g. `/QsBtc_<32 hex>`). Access: the consumer's user only (Windows: a
DACL for SYSTEM and the user; POSIX: mode 0600).

## 5. Requests

### 5.1 `POST /btc/acquire`
Request: `{"key": "...", "source": ["<path>", ...], "consumer": {"pid": <int>, "name": "<text>"}}`

| answer | meaning, what the consumer does |
|---|---|
| `{"state": "ready", "name": "...", "length": N}` | open `name` read-only, check magic and state 1, read the payload |
| `{"state": "fill", "lease": "<id>"}` | you are the filler: decode as you would without the cache, then 5.2 and 5.3 |
| `{"state": "wait", "retry_ms": M}` | another consumer is filling it: ask again after M ms |
| `{"state": "uncached", "why": "..."}` | no section for it now (memory): read your data as without the cache and LOG the line with `why` |

The `pid` must be a running process: the holder drops the lease when that process ends before 5.3.

### 5.2 `POST /btc/create`
Request: `{"lease": "<id>", "length": N}` (N > 0, the payload's exact size)

Answer `{"state": "created", "name": "..."}` - open `name` read-write and write the payload at offset 4096; or
`{"state": "uncached", "why": "..."}` (no room, see section 6) - proceed without the cache and log it.

### 5.3 `POST /btc/ready`
Request: `{"lease": "<id>", "sha256": "<hex of the payload's SHA-256>"}`

The holder computes the payload's SHA-256 and compares; it checks that the source files are unchanged; it writes
`payload_sha256` into the header JSON and state 1. Answer `{"state": "ready", "name": "...", "length": N}` - the filler
may now re-open the section read-only like any reader; or `{"state": "dropped", "why": "..."}` (the SHA-256 differs or a
source changed during the fill) - the filler uses its own decoded data for this run and logs it.

### 5.4 `POST /btc/drop` and `POST /btc/drop_kind`
`{"path": "<source path>"}` drops every section built from that file (the data owner calls it after replacing a file);
`{"kind": "<kind>"}` drops every section of one kind (a layout no consumer reads any more). Answer `{"dropped": n}`.

### 5.5 `GET /btc/status`
At least: `protocol` (the version of this text the holder speaks: 1), `total_bytes` (header + payload of every section
the holder keeps), `sections_ready`, `sections_filling`,
`ram_line_pct`, `yield_pct`, `machine_ram_pct`, `counts` (hits, fills, uncached, evicted, stale, dropped_leases) and
`entries` (key, state, length, hits, last access). A holder may also write it to a status file for other tools.

## 6. Memory rules of the holder

- Before it creates a section the holder evicts least recently used ready sections until the machine's physical memory
  AND its commit charge, plus the new section, stay below the RAM line (default 85 %). A section that would not fit even
  with every section evicted is answered `uncached` at once, without evicting anything.
- Yield: periodically (default every 5 s) the holder evicts least recently used ready sections while physical memory or
  commit charge is at or above the yield line (default 80 %), so new processes find their memory.
- No fixed size limit is required; the lines above decide.
- Evicting means the holder closes its own handle. The operating system keeps the memory as long as any reader still has
  the section mapped, so eviction never invalidates a reader's view; a later acquire of the same key fills a new
  section.

## 7. Consumer rules

- Map ready sections read-only; never write into a section you did not create through your own lease.
- Check magic `QSBTC001` and state 1 before reading; a reader may verify `payload_sha256` from the header.
- One call per data unit (e.g. per symbol day), not per record.
- `uncached` and `dropped` are not errors of the data: proceed as without the cache, and log each one.
- A filler writes exactly what its decoder would hand to its run without the cache (bit-identical by construction); a
  layout that converts types documents the conversion and checks it per record.

## 8. Versioning

This text is version 1. A change that an existing consumer could misread (a header field, a request or answer field it
must understand) is a new protocol version; the holder announces the version it speaks in `GET /btc/status`
(`protocol`), and a client refuses a holder whose version it does not know.

# NinjaTrader 8 gateway companion (bundled binaries)

These files are the NinjaTrader 8 gateway companion, shipped with MyLean as **binaries only** so a fresh clone builds
and can live-connect to NinjaTrader 8 out of the box — the `ninjatrader8-gateway` configuration defaults to the host
here. There is no source for them in this repository; the gateway is a separate, licensed build.

| File | What it is | Source | SHA-256 |
|---|---|---|---|
| `NinjaTrader8.Gateway.exe` | the host process the brokerage starts (`serve`) | gateway signed release, tag `v10.0.0` | `094e02755442830ce320bf50c078f784a67d0091071db8e7073e4bf9fab62420` |
| `NinjaTrader8.Gateway.exe.config` | the host's .NET configuration | same release | `5eedafa6a1d0d5fae9a8f2ad7291e4dc7df4f3a570cae43de0089131778862f1` |
| `nt8native.dll` | the native core the host loads (P/Invoke, from this folder) | update channel `https://lic.quantrosoft.com/update/nt8native/`, version 10.0.0 | `c9a57c2df7cbf0a996f3c31007f524f5440abc3dc46ef382f519d2e947cd44dc` |

The build copies this folder next to the launcher (`gateway/`), and the brokerage starts
`gateway/NinjaTrader8.Gateway.exe`. Keep the three files together.

## Updates

The host keeps `nt8native.dll` current itself: on start it checks the channel and, only for a strictly higher version,
replaces the DLL in place after a SHA-256 and signature check (with a `.bak` rollback). The check is fail-safe — if the
channel cannot be reached it keeps the shipped DLL and starts normally, so the package also runs offline. Nothing else
fetches or hand-copies the DLL at runtime.

## Re-fetching (maintainers)

`tools/fetch-gateway.ps1` re-populates this folder from the authoritative sources and verifies every SHA-256. It needs
network access to the channel and an authenticated `gh` with access to the gateway's release repository (passed via
`-Repo <owner>/<name>`). Bump its version, tag and SHA-256 defaults when the gateway ships a new build.

## License

The binaries in this folder &mdash; `NinjaTrader8.Gateway.exe`, `NinjaTrader8.Gateway.exe.config` and `nt8native.dll`
&mdash; are **proprietary software, Copyright &copy; Quantrosoft Pte. Ltd., all rights reserved.** They are **not**
open source and are **not** covered by the MIT or Apache License 2.0 that applies to the source code of this
repository (the top-level [`LICENSE.txt`](../../../../LICENSE.txt)). They are a separate, licensed build, bundled only
as a convenience so a clone can connect out of the box; their use is governed by Quantrosoft's own licence terms.
Redistribution outside this repository, modification, decompilation and reverse engineering are not permitted. See
[`LICENSE.txt`](LICENSE.txt) in this folder.

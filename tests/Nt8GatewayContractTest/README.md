# Nt8GatewayContractTest

A manual integration test for the NinjaTrader 8 brokerage client
(`src/Brokerages/NinjaTrader8/GatewayClient.cs`). It drives the real, shipped
`GatewayClient` through the whole brokerage↔gateway contract against a running
gateway companion and reports `PASS`/`FAIL` per step plus a `SUMMARY` line.

It is **not** a CI unit test: it needs a local gateway executable and a
NinjaTrader 8 install. Use NinjaTrader's built-in **Simulated Data Feed** with
account **Sim101** for a login-free run (no broker credentials).

The client source is compiled in via a `Link` include (it is `internal`); its
only external dependency, `QuantConnect.Logging.Log`, is stubbed by `LogShim.cs`,
so no Lean assemblies are referenced.

## Run

```
dotnet build tests/Nt8GatewayContractTest -c Release
tests/Nt8GatewayContractTest/bin/Release/net10.0/Nt8GatewayContractTest.exe \
    "<path>/NinjaTrader8.Gateway.exe" "Simulated Data Feed" "Sim101" "MNQ 12-26" "<logfile>"
```

Arguments: `<gatewayExe> <connection> <account> <symbol> <logFile>`.

## What it checks

- **Phase A** (one client): `start+connect`, `hello`, `account`, `positions`,
  `orders`, `sub`, a working limit order with `change` + `cancel`, a market order
  with the resulting position and a `flatten`, `unsub`, and `shutdown` on dispose.
- **Phase B**: restart the gateway and reconnect (`hello`).
- **Phase C**: a second client attaching alongside the first (multi-instance).

Orders go only to the chosen account; keep it a demo or simulation account.

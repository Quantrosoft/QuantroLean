<#
.SYNOPSIS
  Re-fetch and verify the bundled NinjaTrader 8 gateway companion for MyLean.

.DESCRIPTION
  Maintainer / release tool. It re-populates src/Brokerages/NinjaTrader8/gateway/ with the three files a clone ships:
    nt8native.dll                     - the native core, from the update channel (version-pinned by SHA-256)
    NinjaTrader8.Gateway.exe          - the host process, from the gateway's signed GitHub release
    NinjaTrader8.Gateway.exe.config   - its .NET config, same release
  Every file is checked against its expected SHA-256; a mismatch stops the script and writes nothing.

  Not needed at runtime: the shipped host updates nt8native.dll itself from the channel. The gateway is a separate,
  licensed build whose exe/config live in a private release, so this needs an authenticated 'gh' and network access.

.NOTES
  The native DLL file name comes from the manifest (field p[5], e.g. nt8native-10.0.0.dll) and is saved next to the
  host as nt8native.dll (the P/Invoke name the host loads from its own folder). Bump the version/tag/SHA defaults below
  when the gateway ships a new build.
#>
param(
  [string] $DllVersion  = '10.0.0',  # channel version to expect: manifest full version p[6] if present, else major p[2]
  [string] $ReleaseTag  = 'v10.0.0',
  [string] $Repo        = '',   # the gateway's signed-release repository (owner/name) - pass it; not hard-coded here
  [string] $ChannelBase = 'https://lic.quantrosoft.com/update/nt8native',
  [string] $DllSha      = 'c9a57c2df7cbf0a996f3c31007f524f5440abc3dc46ef382f519d2e947cd44dc',
  [string] $ExeSha      = '094e02755442830ce320bf50c078f784a67d0091071db8e7073e4bf9fab62420',
  [string] $ConfigSha   = '5eedafa6a1d0d5fae9a8f2ad7291e4dc7df4f3a570cae43de0089131778862f1'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
if (-not $Repo) { throw "pass -Repo <owner>/<name> - the gateway's signed-release repository (exe + config assets)" }
$gateway = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\Brokerages\NinjaTrader8\gateway'))
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('gwfetch_' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null

function Confirm-Sha([string]$path, [string]$want) {
  $have = (Get-FileHash -Algorithm SHA256 $path).Hash.ToLower()
  if ($have -ne $want.ToLower()) { throw "SHA-256 mismatch for $([IO.Path]::GetFileName($path)): got $have, expected $want" }
}

try {
  # 1) native DLL from the channel; cross-check the signed manifest names the same version + SHA.
  $raw = (Invoke-WebRequest -Uri "$ChannelBase/manifest" -UseBasicParsing -TimeoutSec 30).Content
  if ($raw -is [byte[]]) { $raw = [Text.Encoding]::ASCII.GetString($raw) }
  # qs-upd-1 | nt8native | <major p2> | <sha256 p3> | <size p4> | <filename p5> | [<full version p6>]
  $fields = (($raw -split "`n")[0]) -split '\|'
  $fileName    = $fields[5]                                                                    # never hard-code the file name
  $manifestVer = if ($fields.Count -gt 6 -and $fields[6]) { $fields[6] } else { $fields[2] }   # full version p[6], else major p[2]
  if ($manifestVer -ne $DllVersion) { throw "channel manifest version is $manifestVer, expected $DllVersion" }
  if ($fields[3].ToLower() -ne $DllSha.ToLower()) { throw "channel manifest SHA $($fields[3]) does not match expected $DllSha" }
  $dllTmp = Join-Path $tmp 'nt8native.dll'
  Invoke-WebRequest -Uri "$ChannelBase/$fileName" -OutFile $dllTmp -UseBasicParsing -TimeoutSec 60
  Confirm-Sha $dllTmp $DllSha

  # 2) host exe + config from the gateway's signed GitHub release (needs an authenticated gh with access to the repo).
  gh release download $ReleaseTag --repo $Repo --dir $tmp --pattern 'NinjaTrader8.Gateway.exe' --pattern 'NinjaTrader8.Gateway.exe.config' --clobber
  if ($LASTEXITCODE -ne 0) { throw "gh release download $ReleaseTag from $Repo failed (authenticated gh + access required)" }
  Confirm-Sha (Join-Path $tmp 'NinjaTrader8.Gateway.exe') $ExeSha
  Confirm-Sha (Join-Path $tmp 'NinjaTrader8.Gateway.exe.config') $ConfigSha

  # 3) all three verified: place them into the gateway folder (the DLL under its P/Invoke name).
  New-Item -ItemType Directory -Force $gateway | Out-Null
  Copy-Item $dllTmp (Join-Path $gateway 'nt8native.dll') -Force
  Copy-Item (Join-Path $tmp 'NinjaTrader8.Gateway.exe') (Join-Path $gateway 'NinjaTrader8.Gateway.exe') -Force
  Copy-Item (Join-Path $tmp 'NinjaTrader8.Gateway.exe.config') (Join-Path $gateway 'NinjaTrader8.Gateway.exe.config') -Force
  Write-Host "OK  gateway populated at $gateway (channel v$DllVersion, release $ReleaseTag); all SHA-256 verified."
}
finally { Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue }

<#
.SYNOPSIS
    Screenshot state of the settings-diagnostics package: two fake
    receivers, one connected as the output.

.DESCRIPTION
    Run by tools/web-screenshots.ps1 -Setup tools/screenshot-setups/settings-diagnostics.ps1 once the bridge answers.
    Connects "Fake Receiver 1" (02:FA:CE:00:00:01) as the single output, so the Diagnostics page shows an output name,
    bound timing/control ports, moving packet counters and live log lines; "Fake Receiver 2" stays online and idle.
    Only the in-process fakes are contacted. The Settings page needs no state (it reads the defaults).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseUrl
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$one = '02:FA:CE:00:00:01'
$two = '02:FA:CE:00:00:02'

# The fakes are announced right after start-up; wait until both are listed.
$ids = @()
$deadline = (Get-Date).AddSeconds(30)
do {
    $receivers = @(Invoke-RestMethod -Uri "$BaseUrl/api/receivers")
    $ids = @($receivers | ForEach-Object { $_.id })
    if ($ids -contains $one -and $ids -contains $two) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

if (-not ($ids -contains $one -and $ids -contains $two)) {
    throw "The two fake receivers did not show up at $BaseUrl/api/receivers."
}

Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/receivers/$one/connect" | Out-Null

# Wait until the session is up, so the statistics belong to a running output.
$state = ''
$deadline = (Get-Date).AddSeconds(20)
do {
    $state = (Invoke-RestMethod -Uri "$BaseUrl/api/receivers/$one").state
    if ($state -in @('connected', 'streaming', 'Connected', 'Streaming')) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

$diagnostics = Invoke-RestMethod -Uri "$BaseUrl/api/diagnostics"
Write-Host "settings-diagnostics setup: $one is $state, $two idle; packets sent $($diagnostics.packetsSent), timing port $($diagnostics.timingPort)."

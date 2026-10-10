<#
.SYNOPSIS
    Screenshot state of the receivers-page package: two fake receivers, one
    connected.

.DESCRIPTION
    Run by tools/web-screenshots.ps1 -Setup tools/screenshot-setups/receivers-page.ps1 once the bridge answers. Connects
    "Fake Receiver 1" (02:FA:CE:00:00:01) as the single output and sets its level to 65, so the first card shows the
    "connected" pill, the Output chip, Disconnect and a part-filled hero slider; "Fake Receiver 2" stays online and
    idle (Connect with its title). Only the in-process fakes are contacted.
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
Invoke-RestMethod -Method Put -Uri "$BaseUrl/api/receivers/$one/volume" -ContentType 'application/json' -Body '{"level":65}' | Out-Null

# Wait until the session is up, so the screenshots show "connected" rather than "connecting…".
$deadline = (Get-Date).AddSeconds(20)
do {
    $state = (Invoke-RestMethod -Uri "$BaseUrl/api/receivers/$one").state
    if ($state -in @('connected', 'streaming', 'Connected', 'Streaming')) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

Write-Host "receivers-page setup: $one is $state, $two idle."

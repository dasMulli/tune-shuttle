<#
.SYNOPSIS
    Screenshot state of the forget-receivers change: one fake receiver online, one offline since three days.

.DESCRIPTION
    Run by tools/web-screenshots.ps1 -Setup tools/screenshot-setups/forget-receivers.ps1 once the bridge answers. Takes
    "Fake Receiver 2" (02:FA:CE:00:00:02) off the fakes' browser through the development-only route
    POST /api/dev/fake-receivers/{id}/offline?daysAgo=3 (Debug builds with Bridge:FakeReceivers), so /receivers shows
    its card offline with "Last seen 3 days ago" and the Forget action, the page head shows "Forget all offline
    receivers (1)", and the Player's Output list shows the offline row with its last-seen time. "Fake Receiver 1" stays
    online and idle. Only the in-process fakes are contacted.
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

Invoke-RestMethod -Method Post -Uri "$BaseUrl/api/dev/fake-receivers/$two/offline?daysAgo=3" | Out-Null

# Wait until the bridge lists it offline with the earlier last-seen time.
$deadline = (Get-Date).AddSeconds(20)
do {
    $receiver = Invoke-RestMethod -Uri "$BaseUrl/api/receivers/$two"
    if (-not $receiver.online -and $receiver.lastSeen -and ([DateTimeOffset]$receiver.lastSeen) -lt (Get-Date).AddDays(-2)) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

Write-Host "forget-receivers setup: $one online, $two offline (last seen $($receiver.lastSeen))."

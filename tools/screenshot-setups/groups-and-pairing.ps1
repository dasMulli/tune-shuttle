<#
.SYNOPSIS
    Screenshot state of the groups-and-pairing package (docs/web/WORK-PLAN.md, groups-and-pairing).

.DESCRIPTION
    Run by tools/web-screenshots.ps1 once the bridge answers (it passes only -BaseUrl):
      pwsh tools/web-screenshots.ps1 -Package groups-and-pairing -Routes /groups -Setup tools/screenshot-setups/groups-and-pairing.ps1

    Creates the group "Downstairs" with master level G 32 and both fakes as members (r_i 40 and 24, inactive, so the
    Output card offers "Activate"), and a second, empty group "Upstairs" (the list then shows a ready and an empty
    pill). Writes the id of "Downstairs" to the console and returns it, so the editor route is /groups/<id>; for the
    editor screenshots run the bridge yourself, call this script, then web-screenshots.ps1 -NoStart with
    -Routes /groups,/groups/<id>.

    The pairing dialog cannot be opened from here: the fakes need no PIN, and only a pairingRequired event (a connect
    that needs a PIN) or the Pair button of a PIN receiver opens it in the browser. Its screenshots put the dialog's
    rendered markup over the live Player page instead (see the package report).

    Only the in-process fakes are contacted.
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

function Invoke-Json([string] $Method, [string] $Path, $Body) {
    $arguments = @{ Method = $Method; Uri = "$BaseUrl$Path" }
    if ($null -ne $Body) {
        $arguments.ContentType = 'application/json'
        $arguments.Body = ($Body | ConvertTo-Json -Compress)
    }

    Invoke-RestMethod @arguments
}

# The fakes are announced right after start-up; wait until both are listed.
$deadline = (Get-Date).AddSeconds(30)
do {
    $ids = @(@(Invoke-Json Get '/api/receivers' $null) | ForEach-Object { $_.id })
    if ($ids -contains $one -and $ids -contains $two) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

if (-not ($ids -contains $one -and $ids -contains $two)) {
    throw "The two fake receivers did not show up at $BaseUrl/api/receivers."
}

$group = Invoke-Json Post '/api/groups' @{ name = 'Downstairs' }
$groupId = [Uri]::EscapeDataString($group.id)
# G first: setting it later would scale the members' r_i (the members keep the levels they are added with).
Invoke-Json Put "/api/groups/$groupId/volume" @{ level = 32 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $one; level = 40 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $two; level = 24 } | Out-Null
Invoke-Json Post '/api/groups' @{ name = 'Upstairs' } | Out-Null

Write-Host "groups-and-pairing setup: group Downstairs is /groups/$groupId (both fakes, inactive); Upstairs is empty."
$group.id

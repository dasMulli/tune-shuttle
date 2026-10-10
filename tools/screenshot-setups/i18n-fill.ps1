<#
.SYNOPSIS
    Screenshot state of the i18n-fill package: the longest strings on every page.

.DESCRIPTION
    Run by tools/web-screenshots.ps1 once the bridge answers, for the language sets
        pwsh tools/web-screenshots.ps1 -Package i18n-fill -Routes /,/receivers,/settings -Sizes 390x844 -Themes light -Langs ru,de
            -Setup tools/screenshot-setups/i18n-fill.ps1
        pwsh tools/web-screenshots.ps1 -Package i18n-fill -Routes /,/receivers,/settings -Sizes 1280x900 -Themes light -Langs fr
            -Setup tools/screenshot-setups/i18n-fill.ps1

    Builds the state with the most text: a group with a long name ("Living room + kitchen + office") holding both fakes
    (G 32, r_i 40 and 24) is the active output, so the Player shows the master label with the group name, the members
    rows (wide) and the wrapping members chip (phone, "name level · name level"), the Output card a group row and two
    receiver rows with pills, the Status card "2 of 2 online"; the Receivers page shows both cards connected with their
    group and output chips. Settings shows every hint of the Bridge card (the longest catalog texts).

    Only the in-process fakes (02:FA:CE:00:00:01/02) are contacted.
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

function Wait-Connected([string] $Id) {
    $deadline = (Get-Date).AddSeconds(20)
    do {
        $state = (Invoke-Json Get "/api/receivers/$Id" $null).state
        if ($state -in @('connected', 'streaming', 'Connected', 'Streaming')) { return $state }
        Start-Sleep -Milliseconds 250
    } while ((Get-Date) -lt $deadline)

    return $state
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

# Idempotent for -NoStart runs against a service that already has the group: reuse it.
$name = 'Living room + kitchen + office'
$group = $null
# foreach enumerates the answer whether Invoke-RestMethod hands back the JSON array as one object or item by item.
foreach ($candidate in (Invoke-Json Get '/api/groups' $null)) {
    if ($null -ne $candidate -and $candidate.name -eq $name) { $group = $candidate; break }
}

if ($null -eq $group) {
    $group = Invoke-Json Post '/api/groups' @{ name = $name }
    $groupId = [Uri]::EscapeDataString($group.id)
    # G first: setting it later would scale the members' r_i (members keep the levels they are added with).
    Invoke-Json Put "/api/groups/$groupId/volume" @{ level = 32 } | Out-Null
    Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $one; level = 40 } | Out-Null
    Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $two; level = 24 } | Out-Null
}

$groupId = [Uri]::EscapeDataString($group.id)
Invoke-Json Post "/api/groups/$groupId/activate" $null | Out-Null
$first = Wait-Connected $one
$second = Wait-Connected $two
Write-Host "i18n-fill setup: group '$name' ($($group.id)) active, $one is $first, $two is $second."

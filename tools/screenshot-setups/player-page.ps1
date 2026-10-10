<#
.SYNOPSIS
    Screenshot state of the player-page package (docs/web/WORK-PLAN.md, player-page).

.DESCRIPTION
    Run by tools/web-screenshots.ps1 once the bridge answers. The screenshot script passes only -BaseUrl, so:
      receiver set: pwsh tools/web-screenshots.ps1 -Package player-page -Routes / -Setup tools/screenshot-setups/player-page.ps1
      group set:    pwsh tools/web-screenshots.ps1 -Package player-page -Routes / -OutDir artifacts/ui/player-page/group
                        -Setup tools/screenshot-setups/player-page-group.ps1   (the wrapper passes -Scenario group)

    -Scenario receiver: connects "Fake Receiver 1" (02:FA:CE:00:00:01) as the single output at level 65; "Fake
    Receiver 2" stays online and idle, so the Output card shows an active row and a quiet row.
    -Scenario group: creates the group "Downstairs" with master level G 32 and both fakes (r_i 40 and 24) and
    activates it, so the hero shows the member rows (wide) / the members chip (phone).

    Only the in-process fakes are contacted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseUrl,

    [ValidateSet('receiver', 'group')]
    [string] $Scenario = 'receiver'
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

if ($Scenario -eq 'receiver') {
    Invoke-Json Post "/api/receivers/$one/connect" $null | Out-Null
    Invoke-Json Put "/api/receivers/$one/volume" @{ level = 65 } | Out-Null
    $state = Wait-Connected $one
    Write-Host "player-page setup (receiver): $one is $state, $two idle."
    return
}

$group = Invoke-Json Post '/api/groups' @{ name = 'Downstairs' }
$groupId = [Uri]::EscapeDataString($group.id)
# G first: setting it later would scale the members' r_i (the members keep the levels they are added with).
Invoke-Json Put "/api/groups/$groupId/volume" @{ level = 32 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $one; level = 40 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $two; level = 24 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/activate" $null | Out-Null
$first = Wait-Connected $one
$second = Wait-Connected $two
Write-Host "player-page setup (group): group $($group.id) active, $one is $first, $two is $second."

<#
.SYNOPSIS
    Group screenshot state of the player-page package: runs player-page.ps1 with -Scenario group.

.DESCRIPTION
    tools/web-screenshots.ps1 passes only -BaseUrl to a setup script, so the group set (artifacts/ui/player-page/group)
    comes from this wrapper: tools/web-screenshots.ps1 -Setup tools/screenshot-setups/player-page-group.ps1
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseUrl
)

& "$PSScriptRoot/player-page.ps1" -BaseUrl $BaseUrl -Scenario group

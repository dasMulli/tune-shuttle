# Screenshot setup scripts

One `<package>.ps1` per work package (docs/web/WORK-PLAN.md 2.2), run by `tools/web-screenshots.ps1 -Setup <script>` as
`& <script> -BaseUrl http://localhost:<port>` once the bridge answers: a plain PowerShell file (`param([string] $BaseUrl)`)
that puts the two fake receivers (`02:FA:CE:00:00:01`, `02:FA:CE:00:00:02`) into the wanted state through the REST API,
e.g. `Invoke-RestMethod -Method Post "$BaseUrl/api/receivers/02:FA:CE:00:00:01/connect"`. Each run starts from an empty
data directory, so a script creates everything it needs.

`player-page-group.ps1` is a wrapper for the Player page's group scenario (`player-page.ps1 -Scenario group`), since
`web-screenshots.ps1` passes only `-BaseUrl` to a setup script.

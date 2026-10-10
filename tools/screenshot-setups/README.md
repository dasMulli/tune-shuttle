# Screenshot setup scripts

Each script puts the bridge's two fake receivers (`02:FA:CE:00:00:01`, `02:FA:CE:00:00:02`) into a state through the
REST API before `tools/web-screenshots.ps1 -Setup <script>` or `tools/web-audit.ps1 -Setup <script>` takes its pictures
or measurements. The tools call it as `& <script> -BaseUrl http://localhost:<port>` once the bridge answers: a plain
PowerShell file with a mandatory `-BaseUrl` parameter, for example
`Invoke-RestMethod -Method Post "$BaseUrl/api/receivers/02:FA:CE:00:00:01/connect"`. Each run starts from an empty data
directory, so a script creates everything it needs.

`player-page-group.ps1` is a wrapper for the Player page's group scenario (`player-page.ps1 -Scenario group`), since
the tools pass only `-BaseUrl` to a setup script. What each script sets up: `docs/DEVELOPMENT.md`, "Screenshots".

<#
.SYNOPSIS
    Screenshot and audit state of the mobile-polish package (docs/web/WORK-PLAN.md, mobile-polish).

.DESCRIPTION
    As a setup script (tools/web-screenshots.ps1 -Setup / tools/web-audit.ps1 -Setup) it waits for the two fake
    receivers, creates the group "Downstairs" (master G 32, Fake Receiver 1 at 40, Fake Receiver 2 at 24) and activates
    it, so every page has the busiest realistic state: the Player shows an active group row with its Edit button, Stop
    output and the members chip (phone) / member bars (wide); Receivers shows two connected cards with group and output
    chips; Groups lists one group. Only the in-process fakes are contacted.

    Spotify is off in every dev run, so the hero says "Nothing playing". -HeroShots <dir> additionally captures the
    Player with a track on screen, to compare the phone hero with the phone mockup: for each size and theme it starts a
    headless Edge over the DevTools protocol and installs, before any page script runs, a browser-side demo: a wrapper
    around window.fetch that answers GET /api/status with Spotify connected (user "martin") and GET /api/playback with
    a playing track, and a WebSocket wrapper that rewrites the statusChanged / playbackChanged events of the live
    snapshot the same way. Nothing is sent to the service. Files: <dir>/hero-<W>x<H>-<theme>-<lang>.png.

.EXAMPLE
    pwsh tools/web-screenshots.ps1 -Package mobile-polish -Routes /,/receivers -Sizes 390x844,844x390 -Setup tools/screenshot-setups/mobile-polish.ps1

.EXAMPLE
    pwsh tools/screenshot-setups/mobile-polish.ps1 -BaseUrl http://localhost:18080 -HeroShots artifacts/ui/mobile-polish
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseUrl,

    # When set: also capture the Player with the demo track into this folder (the group is set up first if needed).
    [string] $HeroShots,

    # Viewports (CSS px) of the hero screenshots.
    [string[]] $Sizes = @('390x844', '844x390', '768x1024', '1280x900'),

    # Themes of the hero screenshots (the one-shot ?theme= query).
    [string[]] $Themes = @('light', 'dark'),

    # Language of the hero screenshots (the one-shot ?lang= query).
    [string] $Lang = 'en',

    # Longest wait in ms for the WASM runtime to boot; then the extra settle time for the data.
    [int] $Budget = 20000,
    [int] $Settle = 2500,

    [string] $Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
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

# ---- the state: the active group "Downstairs" of both fakes ---------------------------------------------------

$deadline = (Get-Date).AddSeconds(30)
do {
    $ids = @(@(Invoke-Json Get '/api/receivers' $null) | ForEach-Object { $_.id })
    if ($ids -contains $one -and $ids -contains $two) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

if (-not ($ids -contains $one -and $ids -contains $two)) {
    throw "The two fake receivers did not show up at $BaseUrl/api/receivers."
}

# ForEach-Object unrolls the JSON array (an empty one arrives as a single empty array object).
$existing = Invoke-Json Get '/api/groups' $null | ForEach-Object { $_ } |
    Where-Object { $_ -and $_.PSObject.Properties['name'] -and $_.name -eq 'Downstairs' } | Select-Object -First 1
if ($existing) {
    $group = $existing
    $groupId = [Uri]::EscapeDataString($group.id)
}
else {
    $group = Invoke-Json Post '/api/groups' @{ name = 'Downstairs' }
    $groupId = [Uri]::EscapeDataString($group.id)
    # G first: setting it later would scale the members' r_i (the members keep the levels they are added with).
    Invoke-Json Put "/api/groups/$groupId/volume" @{ level = 32 } | Out-Null
    Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $one; level = 40 } | Out-Null
    Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $two; level = 24 } | Out-Null
}
Invoke-Json Post "/api/groups/$groupId/activate" $null | Out-Null
$first = Wait-Connected $one
$second = Wait-Connected $two
Write-Host "mobile-polish setup: group $($group.id) active, $one is $first, $two is $second."

if (-not $HeroShots) { return }

# ---- the hero screenshots (browser-side demo track) ---------------------------------------------------------------

# Runs before any page script: the REST answers and the live snapshot say "Spotify connected, a track is playing".
$demo = @'
(function () {
    var started = Date.now();
    function status(s) { s.spotifyConnected = true; s.spotifyUser = "martin"; return s; }
    function playback() {
        return { playing: true, trackId: "spotify:track:demo", title: "Strobeheart", artist: "Niki & The Dove",
                 album: "Instinct", positionMs: 72000 + (Date.now() - started), durationMs: 214000, artworkUrl: null, spotifyVolume: 32 };
    }
    var realFetch = window.fetch.bind(window);
    window.fetch = function (input, init) {
        var url = typeof input === "string" ? input : (input && input.url) || "";
        var method = ((init && init.method) || (input && input.method) || "GET").toUpperCase();
        if (method === "GET" && /\/api\/playback(\?|$)/.test(url)) {
            return Promise.resolve(new Response(JSON.stringify(playback()), { status: 200, headers: { "Content-Type": "application/json" } }));
        }
        if (method === "GET" && /\/api\/status(\?|$)/.test(url)) {
            return realFetch(input, init).then(function (r) { return r.json(); }).then(function (s) {
                return new Response(JSON.stringify(status(s)), { status: 200, headers: { "Content-Type": "application/json" } });
            });
        }
        return realFetch(input, init);
    };
    function rewrite(text) {
        try {
            var e = JSON.parse(text);
            if (e && e.type === "statusChanged" && e.status) { status(e.status); return JSON.stringify(e); }
            if (e && e.type === "playbackChanged") { e.playback = playback(); return JSON.stringify(e); }
        } catch (x) { }
        return text;
    }
    function convert(event) {
        var data = event.data;
        if (typeof data === "string") return new MessageEvent("message", { data: rewrite(data) });
        if (data instanceof ArrayBuffer) {
            var text = new TextDecoder().decode(data), changed = rewrite(text);
            return changed === text ? event : new MessageEvent("message", { data: new TextEncoder().encode(changed).buffer });
        }
        return event;
    }
    var proto = WebSocket.prototype, add = proto.addEventListener;
    proto.addEventListener = function (type, listener, options) {
        if (type !== "message" || typeof listener !== "function") return add.call(this, type, listener, options);
        return add.call(this, type, function (event) { return listener.call(this, convert(event)); }, options);
    };
    var onmessage = Object.getOwnPropertyDescriptor(proto, "onmessage");
    Object.defineProperty(proto, "onmessage", {
        configurable: true,
        get: function () { return onmessage.get.call(this); },
        set: function (handler) {
            onmessage.set.call(this, typeof handler === "function" ? function (event) { return handler.call(this, convert(event)); } : handler);
        }
    });
})();
'@

function ConvertTo-Argument([string] $value) {
    if ($value -match '[\s"]') { '"' + ($value -replace '"', '\"') + '"' } else { $value }
}

function Stop-Tree($process) {
    if ($process -and -not $process.HasExited) {
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null }
        finally { $ErrorActionPreference = $previous }
    }
}

function Receive-CdpMessage([System.Net.WebSockets.ClientWebSocket] $socket) {
    $buffer = [byte[]]::new(1MB)
    $stream = [IO.MemoryStream]::new()
    $cts = [Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(60))
    try {
        do {
            $result = $socket.ReceiveAsync([ArraySegment[byte]]::new($buffer), $cts.Token).GetAwaiter().GetResult()
            if ($result.MessageType -eq [System.Net.WebSockets.WebSocketMessageType]::Close) { throw 'The DevTools connection closed' }
            $stream.Write($buffer, 0, $result.Count)
        } while (-not $result.EndOfMessage)
        [Text.Encoding]::UTF8.GetString($stream.ToArray())
    }
    finally {
        $cts.Dispose()
        $stream.Dispose()
    }
}

$script:cdpId = 0
function Invoke-Cdp([System.Net.WebSockets.ClientWebSocket] $socket, [string] $method, [hashtable] $params = @{}) {
    $script:cdpId++
    $id = $script:cdpId
    $json = @{ id = $id; method = $method; params = $params } | ConvertTo-Json -Depth 10 -Compress
    $bytes = [Text.Encoding]::UTF8.GetBytes($json)
    $socket.SendAsync([ArraySegment[byte]]::new($bytes), [System.Net.WebSockets.WebSocketMessageType]::Text, $true, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    while ($true) {
        $message = Receive-CdpMessage $socket | ConvertFrom-Json
        if ($message.PSObject.Properties['id'] -and $message.id -eq $id) {
            if ($message.PSObject.Properties['error']) { throw "DevTools $method failed: $($message.error.message)" }
            return $message.result
        }
    }
}

function Invoke-CdpEval([System.Net.WebSockets.ClientWebSocket] $socket, [string] $expression) {
    $result = Invoke-Cdp $socket 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true }
    if ($result.PSObject.Properties['exceptionDetails']) { return $null }
    if ($result.result.PSObject.Properties['value']) { return $result.result.value }
    return $null
}

function Save-HeroShot([string] $url, [int] $width, [int] $height, [string] $file) {
    $profileDir = Join-Path ([IO.Path]::GetTempPath()) ("bridge-hero-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    $portFile = Join-Path $profileDir 'DevToolsActivePort'
    $edgeArgs = @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
        '--hide-scrollbars', '--remote-debugging-port=0', "--user-data-dir=$profileDir", 'about:blank'
    )
    $process = Start-Process -FilePath $Edge -ArgumentList ($edgeArgs | ForEach-Object { ConvertTo-Argument $_ }) -PassThru -WindowStyle Hidden
    $socket = $null
    try {
        $deadline = (Get-Date).AddSeconds(30)
        $debugPort = $null
        while (-not $debugPort) {
            if ((Get-Date) -gt $deadline) { throw 'Edge did not open its DevTools port within 30 s' }
            if (Test-Path $portFile) {
                $line = Get-Content $portFile -TotalCount 1 -ErrorAction SilentlyContinue
                if ($line -match '^\d+$') { $debugPort = [int]$line }
            }
            if (-not $debugPort) { Start-Sleep -Milliseconds 100 }
        }
        $page = $null
        while (-not $page) {
            if ((Get-Date) -gt $deadline) { throw 'Edge did not list a page target within 30 s' }
            try {
                $page = Invoke-RestMethod -Uri "http://127.0.0.1:$debugPort/json/list" -TimeoutSec 3 |
                    ForEach-Object { $_ } | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
            }
            catch { $page = $null }
            if (-not $page) { Start-Sleep -Milliseconds 100 }
        }
        $socket = [System.Net.WebSockets.ClientWebSocket]::new()
        $socket.Options.KeepAliveInterval = [TimeSpan]::Zero
        $socket.ConnectAsync([Uri] $page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null

        Invoke-Cdp $socket 'Page.enable' | Out-Null
        Invoke-Cdp $socket 'Page.addScriptToEvaluateOnNewDocument' @{ source = $demo } | Out-Null
        Invoke-Cdp $socket 'Emulation.setDeviceMetricsOverride' @{ width = $width; height = $height; deviceScaleFactor = 1; mobile = $false } | Out-Null
        Invoke-Cdp $socket 'Page.navigate' @{ url = $url } | Out-Null

        $ready = "document.readyState === 'complete' && !!document.querySelector('.hero__title') && document.querySelector('.hero__title').textContent.indexOf('Strobeheart') >= 0"
        $deadline = (Get-Date).AddMilliseconds($Budget)
        while (-not [bool](Invoke-CdpEval $socket $ready)) {
            if ((Get-Date) -gt $deadline) { Write-Warning "  the demo track did not show within $Budget ms: $url"; break }
            Start-Sleep -Milliseconds 250
        }
        Start-Sleep -Milliseconds $Settle
        $shot = Invoke-Cdp $socket 'Page.captureScreenshot' @{ format = 'png' }
        [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($shot.data))
    }
    finally {
        if ($socket) { try { $socket.Dispose() } catch { } }
        Stop-Tree $process
        Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
    }
}

New-Item -ItemType Directory -Force -Path $HeroShots | Out-Null
$HeroShots = (Resolve-Path $HeroShots).Path
foreach ($size in @($Sizes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
    $w, $h = $size.Trim() -split 'x'
    foreach ($theme in @($Themes | ForEach-Object { $_ -split ',' } | Where-Object { $_ })) {
        $file = Join-Path $HeroShots "hero-${w}x${h}-$($theme.Trim())-$Lang.png"
        Save-HeroShot "$BaseUrl/?theme=$($theme.Trim())&lang=$Lang" ([int]$w) ([int]$h) $file
        Write-Host "  $file"
    }
}

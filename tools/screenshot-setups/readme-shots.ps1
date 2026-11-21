<#
.SYNOPSIS
    Takes the README / docs screenshots (docs/images/*.png) from the bridge's two fake receivers.

.DESCRIPTION
    A setup script for tools/web-screenshots.ps1: it is called with -BaseUrl once the bridge answers, puts the two fakes
    into a group, and captures the pictures itself in headless Edge over the DevTools protocol:

      pwsh tools/web-screenshots.ps1 -Package readme -Routes / -Sizes 1280x900 -Themes light -OutDir artifacts/screenshots/readme `
          -Setup tools/screenshot-setups/readme-shots.ps1

    The real service is never touched and nothing about a real network, speaker or account is shown: a browser-side demo
    (installed before any page script runs) renames the fakes to "Kitchen" and "Living room", says that Spotify is
    connected for a made-up user and plays a made-up track. Nothing of this is sent to the service.

    Files: <Out>/<name>-<theme>.png with the names player, receivers, group (the group editor) and player-phone.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseUrl,

    [string] $Out = (Join-Path $PSScriptRoot '..\..\docs\images'),
    [string[]] $Themes = @('light', 'dark'),
    [int] $Budget = 20000,
    [int] $Settle = 2500,
    [string] $Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$one = '02:FA:CE:00:00:01'
$two = '02:FA:CE:00:00:02'
$groupName = 'Downstairs'

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

# ---- the state: the active group of both fakes ---------------------------------------------------------------------

$deadline = (Get-Date).AddSeconds(30)
do {
    $ids = @(@(Invoke-Json Get '/api/receivers' $null) | ForEach-Object { $_.id })
    if ($ids -contains $one -and $ids -contains $two) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)
if (-not ($ids -contains $one -and $ids -contains $two)) {
    throw "The two fake receivers did not show up at $BaseUrl/api/receivers."
}

$group = Invoke-Json Post '/api/groups' @{ name = $groupName }
$groupId = [Uri]::EscapeDataString($group.id)
# G first: setting it later would scale the members' r_i.
Invoke-Json Put "/api/groups/$groupId/volume" @{ level = 40 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $one; level = 48 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/members" @{ receiverId = $two; level = 36 } | Out-Null
Invoke-Json Post "/api/groups/$groupId/activate" $null | Out-Null
$a = Wait-Connected $one
$b = Wait-Connected $two
Write-Host "readme-shots setup: group $($group.id) active, $one is $a, $two is $b."

# ---- the browser-side demo -----------------------------------------------------------------------------------------

$demo = @'
(function () {
    var started = Date.now();
    var names = [["Fake Receiver 1", "Kitchen"], ["Fake Receiver 2", "Living room"]];
    // The fakes listen on loopback ports: show one documentation-range address (192.0.2.0/24) per speaker instead.
    function walk(o) {
        if (!o || typeof o !== "object") return;
        if (typeof o.id === "string" && /^02:FA:CE:00:00:0(\d)$/.test(o.id)) {
            var n = RegExp.$1;
            if ("address" in o) o.address = "192.0.2.2" + n;
            if ("port" in o) o.port = 7000;
        }
        for (var k in o) walk(o[k]);
    }
    function rename(text) {
        for (var i = 0; i < names.length; i++) text = text.split(names[i][0]).join(names[i][1]);
        text = text.split("Test Speaker ABCD").join("Smart speaker");
        text = text.split("Example Audio Maker GmbH").join("Example Audio");
        try { var o = JSON.parse(text); walk(o); text = JSON.stringify(o); } catch (x) { }
        return text;
    }
    function status(s) { s.spotifyConnected = true; s.spotifyUser = "listener"; return s; }
    function playback() {
        return { playing: true, trackId: "spotify:track:demo", title: "Golden Hour",
                 artist: "The Night Owls", album: "Slow Lights",
                 positionMs: 72000 + (Date.now() - started), durationMs: 214000, artworkUrl: null, spotifyVolume: 40 };
    }
    var realFetch = window.fetch.bind(window);
    window.fetch = function (input, init) {
        var url = typeof input === "string" ? input : (input && input.url) || "";
        var method = ((init && init.method) || (input && input.method) || "GET").toUpperCase();
        if (method === "GET" && /\/api\/playback(\?|$)/.test(url)) {
            return Promise.resolve(new Response(JSON.stringify(playback()), { status: 200, headers: { "Content-Type": "application/json" } }));
        }
        if (url.indexOf("/api/") < 0) return realFetch(input, init);
        return realFetch(input, init).then(function (r) {
            if (r.status === 204 || r.status === 205 || r.status === 304) return r;
            return r.text().then(function (t) {
                if (/\/api\/status(\?|$)/.test(url)) { try { t = JSON.stringify(status(JSON.parse(t))); } catch (x) { } }
                return new Response(rename(t), { status: r.status, headers: { "Content-Type": r.headers.get("Content-Type") || "application/json" } });
            });
        });
    };
    function rewrite(text) {
        try {
            var e = JSON.parse(text);
            if (e && e.type === "statusChanged" && e.status) { status(e.status); text = JSON.stringify(e); }
            else if (e && e.type === "playbackChanged") { e.playback = playback(); text = JSON.stringify(e); }
        } catch (x) { }
        return rename(text);
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

function Save-Shot([string] $url, [int] $width, [int] $height, [string] $file, [string] $waitFor) {
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

        $ready = "document.readyState === 'complete' && !document.querySelector('#app > .splash') && ($waitFor)"
        $deadline = (Get-Date).AddMilliseconds($Budget)
        while (-not [bool](Invoke-CdpEval $socket $ready)) {
            if ((Get-Date) -gt $deadline) { Write-Warning "  the page did not settle within $Budget ms: $url"; break }
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

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$track = "!!document.querySelector('.hero__title') && document.querySelector('.hero__title').textContent.indexOf('Golden Hour') >= 0"
$shots = @(
    @{ Name = 'player';       Route = '/';                Size = '1280x600'; Wait = $track },
    @{ Name = 'receivers';    Route = '/receivers';       Size = '1280x500'; Wait = "document.body.textContent.indexOf('Kitchen') >= 0" },
    @{ Name = 'group';        Route = "/groups/$groupId"; Size = '1280x900'; Wait = "document.body.textContent.indexOf('Downstairs') >= 0" },
    @{ Name = 'player-phone'; Route = '/';                Size = '390x844';  Wait = $track }
)
foreach ($shot in $shots) {
    $w, $h = $shot.Size -split 'x'
    foreach ($theme in $Themes) {
        $file = Join-Path $Out "$($shot.Name)-$theme.png"
        $joiner = if ($shot.Route.Contains('?')) { '&' } else { '?' }
        Save-Shot "$BaseUrl$($shot.Route)${joiner}theme=$theme&lang=en" ([int]$w) ([int]$h) $file $shot.Wait
        Write-Host "  $file"
    }
}

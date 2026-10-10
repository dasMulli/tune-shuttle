<#
.SYNOPSIS
    Screenshot state of the shell package: Fake Receiver 1 connected, so the status pill
    is "live" and the Player has an output; optionally the error-toast screenshots.

.DESCRIPTION
    As a setup script (tools/web-screenshots.ps1 -Setup tools/screenshot-setups/shell.ps1) it waits for the two fake
    receivers, connects "Fake Receiver 1" (02:FA:CE:00:00:01) as the single output and waits until the session is up.
    Only the in-process fakes are contacted.

    Toasts are client-side: a failing REST call made by this script (or by curl) never shows up in a browser. So
    -ToastShots <dir> additionally takes its own screenshots with a toast on screen: for each size and theme it starts a
    headless Edge over the DevTools protocol, installs (before any page script runs) a wrapper around window.fetch that
    answers the app's first GET /api/status with a 502 problem, and loads "/". The app's start-up refresh then fails the
    way any API error does (BridgeCommands -> notices.failed), so the real error toast shows: under the top bar on
    desktop, above the tab bar on phones. Files: <dir>/toast-<W>x<H>-<theme>-<lang>.png.

.EXAMPLE
    pwsh tools/web-screenshots.ps1 -Package shell -Routes /,/settings -Setup tools/screenshot-setups/shell.ps1

.EXAMPLE
    pwsh tools/screenshot-setups/shell.ps1 -BaseUrl http://localhost:18080 -ToastShots artifacts/ui/shell
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $BaseUrl,

    # When set: also capture the toast screenshots into this folder.
    [string] $ToastShots,

    # Viewports (CSS px) of the toast screenshots.
    [string[]] $Sizes = @('390x844', '1280x900'),

    # Themes of the toast screenshots (the one-shot ?theme= query).
    [string[]] $Themes = @('light', 'dark'),

    # Language of the toast screenshots (the one-shot ?lang= query).
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

# ---- the state: Fake Receiver 1 connected ---------------------------------------------------------------------

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

$deadline = (Get-Date).AddSeconds(20)
do {
    $state = (Invoke-RestMethod -Uri "$BaseUrl/api/receivers/$one").state
    if ($state -in @('connected', 'streaming', 'Connected', 'Streaming')) { break }
    Start-Sleep -Milliseconds 250
} while ((Get-Date) -lt $deadline)

Write-Host "shell setup: $one is $state, $two idle."

if (-not $ToastShots) { return }

# ---- optional: toast screenshots over the DevTools protocol ---------------------------------------------------

if (-not (Test-Path $Edge)) { throw "Edge not found at $Edge" }
New-Item -ItemType Directory -Force -Path $ToastShots | Out-Null
$ToastShots = (Resolve-Path $ToastShots).Path

# Runs before the app's own scripts on every document of the tab: the first GET /api/status answers 502.
$failFirstStatus = @'
(function () {
    var realFetch = window.fetch.bind(window);
    var failed = false;
    window.fetch = function (input, init) {
        var url = typeof input === "string" ? input : (input && input.url) || "";
        if (!failed && /\/api\/status(\?|$)/.test(url)) {
            failed = true;
            var body = JSON.stringify({ title: "Bad Gateway", status: 502, detail: "Injected by tools/screenshot-setups/shell.ps1 for the toast screenshots." });
            return Promise.resolve(new Response(body, { status: 502, headers: { "Content-Type": "application/problem+json" } }));
        }
        return realFetch(input, init);
    };
})();
'@

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

function Save-ToastShot([int] $width, [int] $height, [string] $theme, [string] $file) {
    $profileDir = Join-Path ([IO.Path]::GetTempPath()) ('bridge-toast-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    $portFile = Join-Path $profileDir 'DevToolsActivePort'
    $edgeArgs = '--headless=new --disable-gpu --no-first-run --no-default-browser-check --disable-extensions --hide-scrollbars ' +
        "--remote-debugging-port=0 `"--user-data-dir=$profileDir`" about:blank"
    $process = Start-Process -FilePath $Edge -ArgumentList $edgeArgs -PassThru -WindowStyle Hidden
    $socket = $null
    try {
        $deadline = (Get-Date).AddSeconds(30)
        $debugPort = $null
        while (-not $debugPort) {
            if ((Get-Date) -gt $deadline) { throw 'Edge did not open its DevTools port within 30 s' }
            if (Test-Path $portFile) {
                $first = Get-Content $portFile -TotalCount 1 -ErrorAction SilentlyContinue
                if ($first -match '^\d+$') { $debugPort = [int]$first }
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

        Invoke-Cdp $socket 'Emulation.setDeviceMetricsOverride' @{ width = $width; height = $height; deviceScaleFactor = 1; mobile = $false } | Out-Null
        Invoke-Cdp $socket 'Page.enable' | Out-Null
        Invoke-Cdp $socket 'Page.addScriptToEvaluateOnNewDocument' @{ source = $failFirstStatus } | Out-Null
        Invoke-Cdp $socket 'Page.navigate' @{ url = "$BaseUrl/?theme=$theme&lang=$Lang" } | Out-Null

        $deadline = (Get-Date).AddMilliseconds($Budget)
        while (-not [bool](Invoke-CdpEval $socket "!!document.querySelector('.toast')")) {
            if ((Get-Date) -gt $deadline) { Write-Warning "  no toast within $Budget ms at ${width}x${height} $theme"; break }
            Start-Sleep -Milliseconds 250
        }
        Start-Sleep -Milliseconds $Settle

        $overflow = Invoke-CdpEval $socket 'document.documentElement.scrollWidth - window.innerWidth'
        if ($overflow -and [int]$overflow -gt 0) { Write-Warning "  horizontal overflow of $overflow px at ${width}x${height}" }

        $shot = Invoke-Cdp $socket 'Page.captureScreenshot' @{ format = 'png' }
        [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($shot.data))
        Write-Host "  $file"
    }
    finally {
        if ($socket) { try { $socket.Dispose() } catch { } }
        Stop-Tree $process
        Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
    }
}

function Split-List([string[]] $values) {
    @($values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

$Sizes = Split-List $Sizes
$Themes = Split-List $Themes
foreach ($size in $Sizes) {
    if ($size -notmatch '^(\d+)x(\d+)$') { throw "Size '$size' is not WxH" }
    $width = [int]$Matches[1]
    $height = [int]$Matches[2]
    foreach ($theme in $Themes) {
        Save-ToastShot $width $height $theme (Join-Path $ToastShots "toast-${width}x${height}-$theme-$Lang.png")
    }
}

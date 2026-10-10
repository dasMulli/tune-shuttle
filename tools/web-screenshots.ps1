<#
.SYNOPSIS
    Screenshots of the web app with headless Edge (docs/web/WORK-PLAN.md 2.2).

.DESCRIPTION
    Starts the bridge with two in-process fake receivers (unless -NoStart), waits for /api/status, runs the optional
    -Setup script against it, then takes one PNG per route x size x theme x language and stops the service again.

    Files: <OutDir>/<name>-<W>x<H>-<theme>-<lang>.png (with -ScrollY N: ...-<lang>-scroll<N>.png), where <name> is
    "player" for "/" and otherwise the route without its leading slash with every character outside [A-Za-z0-9]
    replaced by "-" (/settings/diagnostics -> settings-diagnostics, /groups/new -> groups-new, /_styleguide ->
    -styleguide, /_styleguide?dialog=1 -> -styleguide-dialog-1). <OutDir> defaults to artifacts/screenshots/<Package>
    under the repository root.

    Themes: light and dark use the one-shot ?theme= query of index.html (never stored). system-dark loads ?theme=system
    in an Edge started with --force-dark-mode, which flips prefers-color-scheme (proves that "system" follows the OS);
    system / system-light load ?theme=system without it.

    How the picture is taken: headless Edge (155) never lays a page out narrower than 504 CSS px, whatever
    --window-size says, so "--window-size=390,844 --screenshot" would crop a 504px-wide layout. Each screenshot
    therefore starts its own headless Edge with --remote-debugging-port and drives it over the DevTools protocol:
    Emulation.setDeviceMetricsOverride (exactly W x H CSS px, scale 1, desktop pointer: headless Edge still reports
    (hover: hover) and no coarse pointer, UI-SPEC 5.6), Page.navigate, then it waits until the Blazor splash is gone
    (at most -Budget ms) plus -Settle ms for the REST and WebSocket data, and calls Page.captureScreenshot.
    After each capture the script prints the horizontal overflow (scrollWidth - innerWidth) when it is not 0.

    The service only ever runs with --Bridge:FakeReceivers=2, Spotify off and mDNS browsing off (the fakes replace
    it), file logging off and a fresh temporary data directory, so it never contacts real receivers and every run starts
    from the same state. Owned by the design-system package; other packages add tools/screenshot-setups/<package>.ps1
    instead of editing this file.

.EXAMPLE
    pwsh tools/web-screenshots.ps1 -Package shell -Routes /,/settings -Setup tools/screenshot-setups/shell.ps1

.EXAMPLE
    pwsh tools/web-screenshots.ps1 -Package design-system -Routes /_styleguide -Themes light,dark,system-dark

.EXAMPLE
    pwsh tools/web-screenshots.ps1 -Package mobile-polish -Routes / -Sizes 390x2200 -Themes light -NoStart -Port 18080
#>
[CmdletBinding()]
param(
    # Package name: the default output folder artifacts/screenshots/<Package>.
    [Parameter(Mandatory)]
    [string] $Package,

    # Routes to capture ("/", "/receivers", "/settings/diagnostics", ...). Comma-separated strings are split too.
    [string[]] $Routes = @('/'),

    # Viewport sizes as WxH (CSS px; a tall height such as 390x2200 shows the end of a page).
    [string[]] $Sizes = @('390x844', '768x1024', '1280x900'),

    # light, dark, system (= system-light) or system-dark.
    [string[]] $Themes = @('light', 'dark'),

    # Language codes for ?lang= (de, en, fr, es, ru, uk, pt).
    [string[]] $Langs = @('en'),

    # Port of the service (started here, or already running with -NoStart).
    [int] $Port = 8080,

    # Use a service that is already running on -Port instead of starting one. After a rebuild of src/Bridge.Web,
    # restart that service: it serves only the fingerprinted _framework files it found when it started.
    [switch] $NoStart,

    # A PowerShell script run once the service answers: & $Setup -BaseUrl http://localhost:<Port>.
    [string] $Setup,

    # Output folder; default artifacts/screenshots/<Package> under the repository root.
    [string] $OutDir,

    # Longest wait in ms for the WASM runtime to boot (the splash to disappear). Raise it if a screenshot still shows
    # the loading splash.
    [int] $Budget = 15000,

    # Extra wait in ms after the app rendered, for the REST answers and the WebSocket snapshot.
    [int] $Settle = 2500,

    # Scroll the page down by this many CSS px before the capture (0 = top). The file name gets "-scroll<N>" before
    # ".png". Shows the sticky/translucent top bar over content and the phone bar scrolling away.
    [int] $ScrollY = 0,

    # Keep the scrollbars visible (default: --hide-scrollbars, as WORK-PLAN 2.2 specifies).
    [switch] $ShowScrollbars,

    # Path of msedge.exe.
    [string] $Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$baseUrl = "http://localhost:$Port"

function Split-List([string[]] $values) {
    @($values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
}

function Get-RouteName([string] $route) {
    if ($route -eq '/' -or $route -eq '') { return 'player' }
    ($route -replace '^/', '') -replace '[^A-Za-z0-9]', '-'
}

function Test-Service {
    try {
        $response = Invoke-WebRequest -Uri "$baseUrl/api/status" -UseBasicParsing -TimeoutSec 3
        return $response.StatusCode -eq 200
    }
    catch {
        return $false
    }
}

function ConvertTo-Argument([string] $value) {
    if ($value -match '[\s"]') { '"' + ($value -replace '"', '\"') + '"' } else { $value }
}

function Stop-Tree($process) {
    if ($process -and -not $process.HasExited) {
        # taskkill reports children that exit on their own as errors; Windows PowerShell 5.1 would turn that stderr
        # line into a terminating error under $ErrorActionPreference = 'Stop'.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null }
        finally { $ErrorActionPreference = $previous }
    }
}

# ---- a minimal DevTools protocol client ------------------------------------------------------------------------

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
        # events and other answers are ignored
    }
}

function Invoke-CdpEval([System.Net.WebSockets.ClientWebSocket] $socket, [string] $expression) {
    $result = Invoke-Cdp $socket 'Runtime.evaluate' @{ expression = $expression; returnByValue = $true }
    if ($result.PSObject.Properties['exceptionDetails']) { return $null }
    if ($result.result.PSObject.Properties['value']) { return $result.result.value }
    return $null
}

# Starts a headless Edge with a DevTools port, returns @{ Process; Socket }.
function Start-Edge([string] $profileDir, [bool] $forceDark) {
    $portFile = Join-Path $profileDir 'DevToolsActivePort'
    Remove-Item $portFile -ErrorAction SilentlyContinue
    $edgeArgs = @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
        '--remote-debugging-port=0', "--user-data-dir=$profileDir"
    )
    if (-not $ShowScrollbars) { $edgeArgs += '--hide-scrollbars' }
    if ($forceDark) { $edgeArgs += '--force-dark-mode' }
    $edgeArgs += 'about:blank'
    $process = Start-Process -FilePath $Edge -ArgumentList ($edgeArgs | ForEach-Object { ConvertTo-Argument $_ }) -PassThru -WindowStyle Hidden

    $deadline = (Get-Date).AddSeconds(30)
    $debugPort = $null
    while (-not $debugPort) {
        if ((Get-Date) -gt $deadline) { Stop-Tree $process; throw 'Edge did not open its DevTools port within 30 s' }
        if (Test-Path $portFile) {
            $first = Get-Content $portFile -TotalCount 1 -ErrorAction SilentlyContinue
            if ($first -match '^\d+$') { $debugPort = [int]$first }
        }
        if (-not $debugPort) { Start-Sleep -Milliseconds 100 }
    }

    $page = $null
    while (-not $page) {
        if ((Get-Date) -gt $deadline) { Stop-Tree $process; throw 'Edge did not list a page target within 30 s' }
        try {
            # ForEach-Object unrolls the JSON array (pwsh 7 emits it as one object).
            $page = Invoke-RestMethod -Uri "http://127.0.0.1:$debugPort/json/list" -TimeoutSec 3 |
                ForEach-Object { $_ } | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
        }
        catch { $page = $null }
        if (-not $page) { Start-Sleep -Milliseconds 100 }
    }

    $socket = [System.Net.WebSockets.ClientWebSocket]::new()
    $socket.Options.KeepAliveInterval = [TimeSpan]::Zero
    $socket.ConnectAsync([Uri] $page.webSocketDebuggerUrl, [Threading.CancellationToken]::None).GetAwaiter().GetResult() | Out-Null
    @{ Process = $process; Socket = $socket }
}

function Save-Screenshot([string] $url, [int] $width, [int] $height, [bool] $forceDark, [string] $file, [string] $profileRoot) {
    # A fresh profile per screenshot: no cached boot manifest or assembly survives a rebuild of the web app.
    $profileDir = Join-Path $profileRoot ([Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    $browser = Start-Edge $profileDir $forceDark
    $socket = $browser.Socket
    try {
        Invoke-Cdp $socket 'Emulation.setDeviceMetricsOverride' @{ width = $width; height = $height; deviceScaleFactor = 1; mobile = $false } | Out-Null
        Invoke-Cdp $socket 'Page.navigate' @{ url = $url } | Out-Null

        # Rendered = the splash is gone and the error strip is not showing. One reload on a timeout: a web build
        # that lands while the page boots fails the download of a fingerprinted file once.
        $ready = "document.readyState === 'complete' && location.href.indexOf('about:') !== 0 && !document.querySelector('#app > .splash')" +
            " && !(document.getElementById('blazor-error-ui') && getComputedStyle(document.getElementById('blazor-error-ui')).display !== 'none')"
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            $deadline = (Get-Date).AddMilliseconds($Budget)
            $rendered = $false
            while (-not ($rendered = [bool](Invoke-CdpEval $socket $ready))) {
                if ((Get-Date) -gt $deadline) { break }
                Start-Sleep -Milliseconds 250
            }
            if ($rendered) { break }
            if ($attempt -eq 1) {
                Invoke-Cdp $socket 'Page.reload' @{ ignoreCache = $true } | Out-Null
            }
            else {
                Write-Warning "  the app did not render within $Budget ms (twice): $url (the screenshot shows the splash or the error strip)"
            }
        }
        Start-Sleep -Milliseconds $Settle

        if ($ScrollY -gt 0) {
            Invoke-CdpEval $socket "window.scrollTo(0, $ScrollY); window.scrollY" | Out-Null
            Start-Sleep -Milliseconds 400
        }

        $overflow = Invoke-CdpEval $socket 'document.documentElement.scrollWidth - window.innerWidth'
        if ($overflow -and [int]$overflow -gt 0) { Write-Warning "  horizontal overflow of $overflow px at ${width}x${height}: $url" }

        $shot = Invoke-Cdp $socket 'Page.captureScreenshot' @{ format = 'png' }
        [IO.File]::WriteAllBytes($file, [Convert]::FromBase64String($shot.data))
    }
    finally {
        try { $socket.Dispose() } catch { }
        Stop-Tree $browser.Process
        Remove-Item -Recurse -Force $profileDir -ErrorAction SilentlyContinue
    }
}

# ---- run -----------------------------------------------------------------------------------------------------------

$Routes = Split-List $Routes
$Sizes = Split-List $Sizes
$Themes = Split-List $Themes
$Langs = Split-List $Langs

foreach ($theme in $Themes) {
    if ($theme -notin @('light', 'dark', 'system', 'system-light', 'system-dark')) {
        throw "Unknown theme '$theme' (light, dark, system, system-light, system-dark)"
    }
}
foreach ($size in $Sizes) {
    if ($size -notmatch '^\d+x\d+$') { throw "Size '$size' is not WxH" }
}
if (-not (Test-Path $Edge)) { throw "Edge not found at $Edge" }
if ($Setup -and -not (Test-Path $Setup)) { throw "Setup script not found: $Setup" }

if (-not $OutDir) { $OutDir = Join-Path $repo "artifacts/screenshots/$Package" }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$OutDir = (Resolve-Path $OutDir).Path

$work = Join-Path ([IO.Path]::GetTempPath()) ("bridge-screens-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$service = $null
$keepWork = $false
$written = [System.Collections.Generic.List[string]]::new()

try {
    if ($NoStart) {
        if (-not (Test-Service)) { throw "-NoStart: no service answers on $baseUrl/api/status" }
    }
    else {
        if (Test-Service) { throw "Something already answers on $baseUrl; stop it, pick another -Port or pass -NoStart" }

        $dataDir = Join-Path $work 'data'
        $serviceArgs = @(
            'run', '--project', (Join-Path $repo 'src/Bridge.Service'), '--',
            '--Bridge:FakeReceivers=2', '--Bridge:EnableSpotify=false', '--Bridge:FileLogging=false',
            "--DataDirectory=$dataDir", "--Kestrel:Endpoints:Http:Url=$baseUrl"
        )
        Write-Host "Starting the bridge on $baseUrl (fake receivers, data in $dataDir)"
        $service = Start-Process -FilePath 'dotnet' -ArgumentList ($serviceArgs | ForEach-Object { ConvertTo-Argument $_ }) `
            -WorkingDirectory $repo -PassThru -WindowStyle Hidden `
            -RedirectStandardOutput (Join-Path $work 'service.out.log') -RedirectStandardError (Join-Path $work 'service.err.log')

        $deadline = (Get-Date).AddSeconds(90)
        while (-not (Test-Service)) {
            if ($service.HasExited) {
                Get-Content (Join-Path $work 'service.err.log') -ErrorAction SilentlyContinue | Write-Host
                throw "The bridge exited with code $($service.ExitCode) before answering (logs in $work)"
            }
            if ((Get-Date) -gt $deadline) { throw "The bridge did not answer $baseUrl/api/status within 90 s (logs in $work)" }
            Start-Sleep -Milliseconds 500
        }
        Write-Host 'The bridge answers.'
    }

    if ($Setup) {
        Write-Host "Running setup $Setup"
        & $Setup -BaseUrl $baseUrl
    }

    $profileDir = Join-Path $work 'edge-profile'
    New-Item -ItemType Directory -Force -Path $profileDir | Out-Null
    foreach ($route in $Routes) {
        $name = Get-RouteName $route
        $path = if ($route.StartsWith('/')) { $route } else { "/$route" }
        $separator = if ($path.Contains('?')) { '&' } else { '?' }
        foreach ($size in $Sizes) {
            $w, $h = $size -split 'x'
            foreach ($theme in $Themes) {
                $queryTheme = if ($theme -in @('light', 'dark')) { $theme } else { 'system' }
                foreach ($lang in $Langs) {
                    $suffix = if ($ScrollY -gt 0) { "-scroll$ScrollY" } else { '' }
                    $file = Join-Path $OutDir "$name-${w}x${h}-$theme-$lang$suffix.png"
                    Remove-Item $file -ErrorAction SilentlyContinue
                    $url = "$baseUrl$path${separator}theme=$queryTheme&lang=$lang"
                    Save-Screenshot $url ([int]$w) ([int]$h) ($theme -eq 'system-dark') $file $profileDir
                    if (-not (Test-Path $file)) { throw "No screenshot for $url" }
                    $written.Add($file)
                    Write-Host "  $file"
                }
            }
        }
    }
}
catch {
    $keepWork = $true # keep the service logs the error message points to
    throw
}
finally {
    if ($service -and -not $service.HasExited) {
        Write-Host 'Stopping the bridge.'
        Stop-Tree $service
    }
    if (-not $keepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

Write-Host "$($written.Count) screenshot(s) in $OutDir"

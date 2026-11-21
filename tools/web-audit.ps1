<#
.SYNOPSIS
    Layout audit of the web app with headless Edge (docs/DEVELOPMENT.md, "Layout audit"; docs/web/UI-SPEC.md 5.6).

.DESCRIPTION
    Starts the bridge with two in-process fake receivers (unless -NoStart), waits for /api/status, runs the optional
    -Setup script against it, then loads http://localhost:<Port>/_audit?route=<route>&theme=<theme>&lang=<lang> for
    every route x size x theme x language. The /_audit page renders the routed page inside the normal layout and, once
    the page has its data (State.Loaded + one more render + 500 ms), writes the JSON of app.js audit() into
    <pre id="audit-result">: { overflow, small, unnamed, truncated }.

    A run fails when
      - overflow > 0 (horizontal scroll: document scrollWidth - innerWidth),
      - unnamed is not empty (a control without an accessible name), or
      - small (interactive elements under 44 x 44 CSS px; computed at innerWidth <= 699 or with a coarse pointer, which
        -Pointer emulates below 1024px wide by default) has an entry that is not the allow-listed .theme-btn (UI-SPEC 2.4 / 5.4),
      - truncated is not empty (the top bar's connection word .status__text cut by an ellipsis, from 360px wide),
        or
      - the open-pill probe fails: with the top-bar theme picker unfolded (theme-picker--open added for one
        measurement with the picker's width transitions switched off, so the settled open pill is measured; the touch
        state of a first tap), the page still has no horizontal overflow, the connection word is still whole, all
        three theme buttons are wider than 0 (else the probe did not open the pill) and a visible top-bar language
        select keeps at least 72px (the open pill is the widest the bar gets).
    The small list is always printed, allow-listed entries marked "(allowed)". The exit code is 1 on any failure.

    How the page is read: a plain "msedge --dump-dom --window-size=390,844" would not do: headless Edge never lays a
    page out narrower than 504 CSS px whatever --window-size says (verified: a probe page reports innerWidth 504 at
    --window-size=390,844), so a dump would audit a 504px layout. Like tools/web-screenshots.ps1, this script therefore
    drives one headless Edge over the DevTools protocol: Emulation.setDeviceMetricsOverride (exactly W x H CSS px, scale
    1), the pointer of -Pointer (headless Edge itself reports (hover: hover) and no coarse pointer at every size, so a
    touch device is emulated with Emulation.setTouchEmulationEnabled, which is what makes (pointer: coarse) match
    in headless Edge, plus (pointer: coarse) and (hover: none) through Emulation.setEmulatedMedia for browsers
    that honour them there; the CSS touch block of UI-SPEC 5.4 and audit()'s small check then run at tablet
    sizes too), Page.navigate, then it polls the text of #audit-result until it is non-empty (at most -Budget ms, one
    reload half-way; this poll replaces growing --virtual-time-budget retries), parses it and checks it.

    The service only ever runs with --Bridge:FakeReceivers=2, Spotify off, the AirPlay receiver off, file logging off and a fresh temporary data
    directory, so it never contacts real receivers.

.EXAMPLE
    pwsh tools/web-audit.ps1

.EXAMPLE
    pwsh tools/web-audit.ps1 -Routes /,/receivers -Sizes 390x844 -Themes dark -Setup tools/screenshot-setups/mobile-polish.ps1

.EXAMPLE
    pwsh tools/web-audit.ps1 -NoStart -Port 18080 -Langs en,de,ru
#>
[CmdletBinding()]
param(
    # Routes to audit. Comma-separated strings are split too.
    [string[]] $Routes = @('/', '/receivers', '/settings', '/settings/diagnostics', '/groups'),

    # Viewport sizes as WxH (CSS px). The small-target check runs at widths <= 699 and wherever -Pointer is coarse.
    [string[]] $Sizes = @('390x844', '768x1024'),

    # light, dark or system (the one-shot ?theme= query of index.html, never stored).
    [string[]] $Themes = @('light', 'dark'),

    # Language codes for ?lang= (de, en, fr, es, ru, uk, pt).
    [string[]] $Langs = @('en'),

    # Port of the service (started here, or already running with -NoStart).
    [int] $Port = 8080,

    # Use a service that is already running on -Port instead of starting one. Restart it after a rebuild of src/Bridge.Web.
    [switch] $NoStart,

    # A PowerShell script run once the service answers: & $Setup -BaseUrl http://localhost:<Port>.
    [string] $Setup,

    # Longest wait in ms for the audit result of one page (WASM boot + data + settle). One reload at half of it.
    [int] $Budget = 30000,

    # The pointer the page sees: coarse (a touch device: (pointer: coarse), (hover: none), touch events), fine (a mouse,
    # headless Edge's own behaviour) or auto (coarse below 1024px width, phones and tablets; fine from 1024px).
    [ValidateSet('auto', 'coarse', 'fine')]
    [string] $Pointer = 'auto',

    # Path of msedge.exe.
    [string] $Edge = 'C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$baseUrl = "http://localhost:$Port"
$allowSmall = '.theme-btn'

function Split-List([string[]] $values) {
    @($values | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
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

function Stop-Tree($process, [string] $commandLineMark) {
    if ($process -and -not $process.HasExited) {
        # taskkill reports children that exit on their own as errors; keep that from becoming a terminating error.
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        try { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null }
        finally { $ErrorActionPreference = $previous }
    }
    if ($commandLineMark) {
        # Edge helper processes that left the launcher's tree (seen once, kept as a safety net): everything whose command line names this
        # run's own profile directory, and nothing else.
        Get-CimInstance Win32_Process -Filter "Name='msedge.exe'" -ErrorAction SilentlyContinue |
            Where-Object { $_.CommandLine -and $_.CommandLine.Contains($commandLineMark) } |
            ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
    }
}

# ---- a minimal DevTools protocol client (the same as tools/web-screenshots.ps1, which other packages do not edit) ----

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
function Start-Edge([string] $profileDir) {
    $portFile = Join-Path $profileDir 'DevToolsActivePort'
    Remove-Item $portFile -ErrorAction SilentlyContinue
    $edgeArgs = @(
        '--headless=new', '--disable-gpu', '--no-first-run', '--no-default-browser-check', '--disable-extensions',
        '--hide-scrollbars', '--remote-debugging-port=0', "--user-data-dir=$profileDir", 'about:blank'
    )
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

# Loads one audit URL at W x H and returns the parsed audit object plus the small entries outside the allow-list.
function Get-Audit([System.Net.WebSockets.ClientWebSocket] $socket, [string] $url, [int] $width, [int] $height, [bool] $touch) {
    Invoke-Cdp $socket 'Emulation.setDeviceMetricsOverride' @{ width = $width; height = $height; deviceScaleFactor = 1; mobile = $false } | Out-Null
    # A touch device (UI-SPEC 5.4 / 5.6). Touch emulation is what switches (pointer: coarse) on in headless Edge
    # (verified: setEmulatedMedia alone leaves matchMedia('(pointer: coarse)') false); the media features are sent
    # as well. Disabling touch emulation and an empty feature list restore Edge's own fine pointer with hover.
    $features = @(if ($touch) { @{ name = 'pointer'; value = 'coarse' }, @{ name = 'hover'; value = 'none' } })
    Invoke-Cdp $socket 'Emulation.setEmulatedMedia' @{ features = $features } | Out-Null
    Invoke-Cdp $socket 'Emulation.setTouchEmulationEnabled' @{ enabled = $touch; maxTouchPoints = $(if ($touch) { 5 } else { 1 }) } | Out-Null
    Invoke-Cdp $socket 'Page.navigate' @{ url = $url } | Out-Null

    $read = "(function () { var e = document.getElementById('audit-result'); return e ? e.textContent : ''; })()"
    $failed = "!!(document.getElementById('blazor-error-ui') && getComputedStyle(document.getElementById('blazor-error-ui')).display !== 'none')"
    $start = Get-Date
    $reloaded = $false
    $text = ''
    while (-not $text) {
        Start-Sleep -Milliseconds 250
        $text = [string](Invoke-CdpEval $socket $read)
        if ($text) { break }
        $elapsed = ((Get-Date) - $start).TotalMilliseconds
        if ([bool](Invoke-CdpEval $socket $failed) -and $reloaded) { throw "The app shows its error strip: $url" }
        if (-not $reloaded -and ($elapsed -gt $Budget / 2 -or [bool](Invoke-CdpEval $socket $failed))) {
            # A web build that lands while the page boots fails the download of a fingerprinted file once.
            Invoke-Cdp $socket 'Page.reload' @{ ignoreCache = $true } | Out-Null
            $reloaded = $true
            continue
        }
        if ($elapsed -gt $Budget) { throw "No audit result within $Budget ms: $url" }
    }

    $audit = $text | ConvertFrom-Json
    if ($audit.PSObject.Properties['error']) { throw "The audit page reported: $($audit.error) ($url)" }

    # The open-pill probe: the top-bar picker unfolded for one synchronous measurement (Blazor does not re-render in
    # between), then folded back. .theme-btn animates its width (.15s), so the class alone would measure the collapsed
    # pill (the inactive buttons still 0px wide at that instant): a temporary style switches the transitions off for the
    # probe, which makes the open state the settled state; the style is removed only after the pill is folded back and
    # laid out again, so nothing animates afterwards. The three button widths are returned and must all be > 0, so a
    # probe that does not really open the pill fails instead of passing. The top-bar language select (when shown) must
    # keep at least 72px (its 4.5rem floor: one readable character plus the chevron, UI-SPEC 5.5).
    $probe = @'
(function () {
    var picker = document.querySelector('.topbar .theme-picker');
    if (!picker || innerWidth < 360) return JSON.stringify({ skipped: true, overflow: 0, truncated: [], buttons: [], picker: 0, lang: null });
    var still = document.createElement('style');
    still.textContent = '.topbar .theme-picker, .topbar .theme-picker * { transition: none !important; }';
    document.head.appendChild(still);
    var had = picker.classList.contains('theme-picker--open');
    picker.classList.add('theme-picker--open');
    void picker.offsetWidth;
    var all = Array.prototype.slice.call(document.querySelectorAll('.status__text'));
    var lang = document.querySelector('.topbar .lang-select');
    var result = {
        skipped: false,
        overflow: document.documentElement.scrollWidth - innerWidth,
        truncated: all.filter(function (e) { return e.getClientRects().length > 0 && e.scrollWidth > e.clientWidth; })
                      .map(function (e) { return e.textContent.trim(); }),
        buttons: Array.prototype.slice.call(picker.querySelectorAll('.theme-btn'))
                      .map(function (b) { return Math.round(b.getBoundingClientRect().width); }),
        picker: Math.round(picker.getBoundingClientRect().width),
        lang: lang && lang.getClientRects().length > 0 ? Math.round(lang.getBoundingClientRect().width) : null
    };
    if (!had) picker.classList.remove('theme-picker--open');
    void picker.offsetWidth;
    still.remove();
    return JSON.stringify(result);
})()
'@
    $open = [string](Invoke-CdpEval $socket $probe) | ConvertFrom-Json

    # The allow-list is decided in the page: an entry is allowed when every element its selector names is a
    # .theme-btn (the top-bar .lang-select is 44px tall on touch since review cycle 1 of the redesign and no longer allowed).
    $notAllowed = @()
    foreach ($selector in @($audit.small)) {
        $quoted = ConvertTo-Json -InputObject ([string]$selector) -Compress
        $check = "(function (s) { try { var all = Array.from(document.querySelectorAll(s)); return all.length > 0 && all.every(function (e) { return e.matches('$allowSmall'); }); } catch (x) { return false; } })($quoted)"
        if (-not [bool](Invoke-CdpEval $socket $check)) { $notAllowed += [string]$selector }
    }
    @{ Audit = $audit; NotAllowed = $notAllowed; Open = $open }
}

# ---- run -----------------------------------------------------------------------------------------------------------

$Routes = Split-List $Routes
$Sizes = Split-List $Sizes
$Themes = Split-List $Themes
$Langs = Split-List $Langs

foreach ($theme in $Themes) {
    if ($theme -notin @('light', 'dark', 'system')) { throw "Unknown theme '$theme' (light, dark, system)" }
}
foreach ($size in $Sizes) {
    if ($size -notmatch '^\d+x\d+$') { throw "Size '$size' is not WxH" }
}
if (-not (Test-Path $Edge)) { throw "Edge not found at $Edge" }
if ($Setup -and -not (Test-Path $Setup)) { throw "Setup script not found: $Setup" }

$work = Join-Path ([IO.Path]::GetTempPath()) ("bridge-audit-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory -Force -Path $work | Out-Null
$service = $null
$browser = $null
$keepWork = $false
$failures = [System.Collections.Generic.List[string]]::new()
$checked = 0

try {
    if ($NoStart) {
        if (-not (Test-Service)) { throw "-NoStart: no service answers on $baseUrl/api/status" }
    }
    else {
        if (Test-Service) { throw "Something already answers on $baseUrl; stop it, pick another -Port or pass -NoStart" }

        $dataDir = Join-Path $work 'data'
        $serviceArgs = @(
            'run', '--project', (Join-Path $repo 'src/Bridge.Service'), '-p:_HotReloadBrowserToolsEnabled=false', '--',
            '--Bridge:FakeReceivers=2', '--Bridge:EnableSpotify=false', '--Bridge:Airplay:Enabled=false', '--Bridge:FileLogging=false',
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
    $browser = Start-Edge $profileDir

    foreach ($route in $Routes) {
        $path = if ($route.StartsWith('/')) { $route } else { "/$route" }
        foreach ($size in $Sizes) {
            $w, $h = $size -split 'x'
            $touch = $Pointer -eq 'coarse' -or ($Pointer -eq 'auto' -and [int]$w -lt 1024)
            $pointerName = if ($touch) { 'touch' } else { 'mouse' }
            foreach ($theme in $Themes) {
                foreach ($lang in $Langs) {
                    $label = "$path @ ${w}x${h} $pointerName $theme $lang"
                    $url = "$baseUrl/_audit?route=$([Uri]::EscapeDataString($path))&theme=$theme&lang=$lang"
                    $checked++
                    try {
                        $result = Get-Audit $browser.Socket $url ([int]$w) ([int]$h) $touch
                    }
                    catch {
                        $failures.Add("$label`: $($_.Exception.Message)")
                        Write-Host "FAIL $label`: $($_.Exception.Message)" -ForegroundColor Red
                        continue
                    }

                    $audit = $result.Audit
                    $problems = @()
                    if ([int]$audit.overflow -gt 0) { $problems += "overflow $($audit.overflow) px" }
                    $unnamed = @($audit.unnamed)
                    if ($unnamed.Count -gt 0) { $problems += "unnamed: $($unnamed -join ' | ')" }
                    if ($result.NotAllowed.Count -gt 0) { $problems += "small: $($result.NotAllowed -join ' | ')" }
                    $truncated = @(if ($audit.PSObject.Properties['truncated']) { $audit.truncated })
                    if ($truncated.Count -gt 0) { $problems += "truncated: $($truncated -join ' | ')" }
                    if ([int]$result.Open.overflow -gt 0) { $problems += "open picker: overflow $($result.Open.overflow) px" }
                    $openTruncated = @($result.Open.truncated)
                    if ($openTruncated.Count -gt 0) { $problems += "open picker: truncated $($openTruncated -join ' | ')" }
                    $openButtons = @($result.Open.buttons)
                    $openText = 'open pill -'
                    if (-not $result.Open.skipped) {
                        $openText = "open pill $($openButtons -join '/') = $($result.Open.picker)px" + $(if ($null -ne $result.Open.lang) { ", lang $($result.Open.lang)px" } else { '' })
                        if ($openButtons.Count -ne 3 -or @($openButtons | Where-Object { [int]$_ -le 0 }).Count -gt 0) {
                            $problems += "open picker: not unfolded (buttons $($openButtons -join '/'))"
                        }
                        if ($null -ne $result.Open.lang -and [int]$result.Open.lang -lt 72) { $problems += "open picker: language select $($result.Open.lang) px (< 72)" }
                    }

                    $small = @($audit.small | ForEach-Object { if ($result.NotAllowed -contains $_) { $_ } else { "$_ (allowed)" } })
                    $smallText = if ($small.Count -gt 0) { $small -join ' | ' } else { '-' }
                    if ($problems.Count -eq 0) {
                        Write-Host "ok   $label  overflow $($audit.overflow), unnamed 0, truncated 0, $openText, small: $smallText"
                    }
                    else {
                        $failures.Add("$label`: $($problems -join '; ')")
                        Write-Host "FAIL $label  $($problems -join '; ')" -ForegroundColor Red
                        Write-Host "     small: $smallText"
                    }
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
    if ($browser) {
        try { $browser.Socket.Dispose() } catch { }
        Stop-Tree $browser.Process $profileDir
    }
    if ($service -and -not $service.HasExited) {
        Write-Host 'Stopping the bridge.'
        Stop-Tree $service
    }
    if (-not $keepWork) { Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue }
}

if ($failures.Count -gt 0) {
    Write-Host ''
    Write-Host "$($failures.Count) of $checked audit(s) failed:" -ForegroundColor Red
    $failures | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    exit 1
}

Write-Host "All $checked audit(s) passed."

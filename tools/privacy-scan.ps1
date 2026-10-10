<#
.SYNOPSIS
  Fails when a tracked file contains personal or household-specific data: the maintainer's network layout, host names,
  device identifiers captured from real receivers, or Spotify developer credentials (docs/DEVELOPMENT.md, "Privacy scan").

.DESCRIPTION
  Scans every file `git ls-files` lists (tracked, plus new files that are not ignored), except reference/ (third-party sources) and this script. Binary files are
  read byte for byte (Latin-1), so captured binary plists are covered too. Checks:

    subnet        any 192.168.x.x address (the repository only uses the RFC 5737 documentation ranges)
    host          the home network's domain (the PC, TV and cluster host names come from the patterns file);
                  the public repository URL github.com/dasMulli/tune-shuttle is allowed
    device-name   the TV's user-given name
    mac           a MAC address (6 or 8 groups) that is globally administered: captured from real hardware. Synthetic
                  values must be locally administered (02:xx:..) or all-zero
    eui64         an IPv6 interface id derived from a globally administered MAC (xx:xxff:fexx:xxxx)
    ula           an IPv6 ULA with a real global id (fdxx:xxxx:xxxx:...); docs use 2001:db8::/32
    pk            a receiver public key (pk=...) other than the RFC 8032 test keys the docs and tests use
    client-id     a 32-hex id on a line that mentions a client or secret, other than Spotify's public client ids that
                  librespot upstream ships (keymaster, Android, iOS)
    literal       exact values from the git-ignored .local/privacy-scan-patterns.txt (one per line, # comments): the
                  real identifiers that were replaced. They are kept out of the repository on purpose; each is also
                  matched hex-encoded (captures that print bytes as hex)
    credentials   client id, secret, redirect URI and account e-mail from the git-ignored .local/spotify-dev-app.json

  Exit code 0 when clean, 1 with the offending lines. Works in Windows PowerShell 5.1 and PowerShell 7.

.EXAMPLE
  ./tools/privacy-scan.ps1
#>
[CmdletBinding()]
param(
    [string]$Root
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest
if (-not $Root) {
    # Not a parameter default: Windows PowerShell 5.1 has no $PSScriptRoot there.
    $Root = Split-Path -Parent $PSScriptRoot
}

$latin1 = [System.Text.Encoding]::GetEncoding(28591)
$self = "tools/privacy-scan.ps1"
$skipExtensions = @(".mp3", ".ogg", ".wav", ".flac", ".png", ".jpg", ".jpeg", ".gif", ".ico", ".webp", ".woff", ".woff2")

# Public test keys (RFC 8032 section 7.1, tests 1 and 2) that stand in for captured receiver keys.
$allowedPk = @(
    "d75a980182b10ab7d54bfed3c964073a0ee172f3daa62325af021a68f707511a",
    "3d4017c3e843895a92b70aa74d1b7ebc9c982ccf2ec4968cc0cd55f12af4660c"
)
# Spotify's public client ids as librespot upstream ships them (KEYMASTER_CLIENT_ID, ANDROID_CLIENT_ID, IOS_CLIENT_ID).
$allowedClientIds = @(
    "65b708073fc0480ea92a077233ca87bd",
    "9a8d2f0ce77a4e248bb71fefcb557637",
    "58bd3c95768941ea9eb4350aaa033eb3"
)

$patternChecks = @(
    @{ Name = "subnet"; Regex = '192\.168\.\d{1,3}\.' },
    # The owner's own domain outside the public repository URL; concrete host and cluster names are literals in the
    # git-ignored patterns file, so the script itself publishes none of them.
    @{ Name = "host"; Regex = '(?i)(?<!github\.com/)dasmulli' },
    @{ Name = "device-name"; Regex = 'Martin.{1,3}s TV' }
)

$macRegex = [regex]'(?<![0-9A-Fa-f:-])([0-9A-Fa-f]{2})((?:[:-][0-9A-Fa-f]{2}){5}|(?:[:-][0-9A-Fa-f]{2}){7})(?![0-9A-Fa-f:-])'
$euiRegex = [regex]'(?i)(?<![0-9a-f])([0-9a-f]{1,4}):([0-9a-f]{1,2})ff:fe([0-9a-f]{1,2}):([0-9a-f]{1,4})(?![0-9a-f])'
$ulaRegex = [regex]'(?i)(?<![0-9a-z:])f[cd][0-9a-f]{2}:[0-9a-f]{1,4}:[0-9a-f]{1,4}'
$pkRegex = [regex]'(?i)\bpk["''=:\s]+([0-9a-f]{64})(?![0-9a-f])'
$hex32Regex = [regex]'(?i)(?<![0-9a-f])[0-9a-f]{32}(?![0-9a-f])'
$clientWordRegex = [regex]'(?i)client|secret'

# Exact values that must not appear, from git-ignored files (never from this script).
$literals = New-Object System.Collections.Generic.List[string]
$patternFile = Join-Path $Root ".local/privacy-scan-patterns.txt"
if (Test-Path $patternFile) {
    foreach ($line in Get-Content -LiteralPath $patternFile -Encoding UTF8) {
        $value = $line.Trim()
        if ($value.Length -ge 4 -and -not $value.StartsWith("#")) {
            $literals.Add($value)
        }
    }
}

$credentialFile = Join-Path $Root ".local/spotify-dev-app.json"
$credentials = New-Object System.Collections.Generic.List[string]
if (Test-Path $credentialFile) {
    $json = Get-Content -LiteralPath $credentialFile -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($key in @("clientId", "clientSecret", "redirectUri")) {
        $property = $json.PSObject.Properties[$key]
        if ($property -and "$($property.Value)".Length -ge 8) {
            $credentials.Add("$($property.Value)")
        }
    }

    $account = $json.PSObject.Properties["spotifyAccount"]
    if ($account -and $account.Value) {
        foreach ($property in $account.Value.PSObject.Properties) {
            if ($property.Name -match '(?i)mail|user|id$' -and "$($property.Value)".Length -ge 4) {
                $credentials.Add("$($property.Value)")
            }
        }
    }
}

function ConvertTo-Hex([string]$text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($text)
    return -join ($bytes | ForEach-Object { $_.ToString("x2") })
}

$literalForms = @()
foreach ($value in $literals) {
    $literalForms += , @("literal", $value.ToLowerInvariant())
    $literalForms += , @("literal (hex)", (ConvertTo-Hex $value))
}
foreach ($value in $credentials) {
    $literalForms += , @("credentials", $value.ToLowerInvariant())
}

function Test-GlobalMac([byte[]]$octets) {
    $nonZero = $false
    foreach ($o in $octets) { if ($o -ne 0) { $nonZero = $true } }
    # Bit 1 of the first octet: locally administered; bit 0: multicast. Neither set and not all-zero: real hardware.
    return $nonZero -and (($octets[0] -band 3) -eq 0)
}

$findings = New-Object System.Collections.Generic.List[string]

Push-Location $Root
try {
    $files = & git -c core.quotepath=off ls-files --cached --others --exclude-standard
    if ($LASTEXITCODE -ne 0) {
        throw "git ls-files failed"
    }
}
finally {
    Pop-Location
}

foreach ($relative in $files) {
    if ($relative.StartsWith("reference/") -or $relative -eq $self) {
        continue
    }

    $extension = [System.IO.Path]::GetExtension($relative).ToLowerInvariant()
    if ($skipExtensions -contains $extension) {
        continue
    }

    $path = Join-Path $Root $relative
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        continue
    }

    $content = $latin1.GetString([System.IO.File]::ReadAllBytes($path))
    if ($content.Length -eq 0) {
        continue
    }

    $lineStarts = New-Object System.Collections.Generic.List[int]
    $lineStarts.Add(0)
    for ($i = $content.IndexOf("`n"); $i -ge 0; $i = $content.IndexOf("`n", $i + 1)) {
        $lineStarts.Add($i + 1)
    }

    $report = {
        param([string]$check, [int]$index)
        $lo = 0
        $hi = $lineStarts.Count - 1
        while ($lo -lt $hi) {
            $mid = [int][math]::Floor(($lo + $hi + 1) / 2)
            if ($lineStarts[$mid] -le $index) { $lo = $mid } else { $hi = $mid - 1 }
        }

        $start = $lineStarts[$lo]
        $end = $content.IndexOf("`n", $start)
        if ($end -lt 0) { $end = $content.Length }
        $text = $content.Substring($start, $end - $start).Trim()
        if ($text.Length -gt 200) {
            $offset = [math]::Max(0, [math]::Min($index - $start - 60, $text.Length - 200))
            $text = "..." + $text.Substring($offset, 200) + "..."
        }

        $text = [regex]::Replace($text, '[\x00-\x08\x0B\x0C\x0E-\x1F\x7F-\x9F]', '.')
        $findings.Add("${relative}:$($lo + 1): [$check] $text")
    }

    foreach ($check in $patternChecks) {
        foreach ($m in [regex]::Matches($content, $check.Regex)) {
            & $report $check.Name $m.Index
        }
    }

    foreach ($m in $macRegex.Matches($content)) {
        $hex = ($m.Value -replace '[:-]', '')
        $octets = for ($k = 0; $k -lt $hex.Length; $k += 2) { [Convert]::ToByte($hex.Substring($k, 2), 16) }
        if ($octets.Count -eq 8) {
            # An EUI-64 such as a PTP clock identity (MAC with FF:FE in the middle, IEEE 1588: no U/L flip).
            if ($octets[3] -ne 0xFF -or $octets[4] -ne 0xFE) { continue }
            if (Test-GlobalMac ([byte[]]@($octets[0], $octets[1], $octets[2], $octets[5], $octets[6], $octets[7]))) { & $report "mac" $m.Index }
            continue
        }

        if (Test-GlobalMac ([byte[]]$octets)) { & $report "mac" $m.Index }
    }

    foreach ($m in $euiRegex.Matches($content)) {
        $g1 = $m.Groups[1].Value.PadLeft(4, "0")
        $first = [Convert]::ToByte($g1.Substring(0, 2), 16) -bxor 2
        $octets = [byte[]]@($first, [Convert]::ToByte($g1.Substring(2, 2), 16), [Convert]::ToByte($m.Groups[2].Value, 16), [Convert]::ToByte($m.Groups[3].Value, 16), 0, 0)
        $g4 = $m.Groups[4].Value.PadLeft(4, "0")
        $octets[4] = [Convert]::ToByte($g4.Substring(0, 2), 16)
        $octets[5] = [Convert]::ToByte($g4.Substring(2, 2), 16)
        if (Test-GlobalMac $octets) { & $report "eui64" $m.Index }
    }

    foreach ($m in $ulaRegex.Matches($content)) {
        & $report "ula" $m.Index
    }

    foreach ($m in $pkRegex.Matches($content)) {
        if ($allowedPk -notcontains $m.Groups[1].Value.ToLowerInvariant()) { & $report "pk" $m.Index }
    }

    foreach ($m in $hex32Regex.Matches($content)) {
        if ($allowedClientIds -contains $m.Value.ToLowerInvariant()) { continue }
        $lineStart = $content.LastIndexOf("`n", [math]::Max(0, $m.Index - 1)) + 1
        $lineEnd = $content.IndexOf("`n", $m.Index)
        if ($lineEnd -lt 0) { $lineEnd = $content.Length }
        if ($clientWordRegex.IsMatch($content.Substring($lineStart, $lineEnd - $lineStart))) { & $report "client-id" $m.Index }
    }

    if ($literalForms.Count -gt 0) {
        $lower = $content.ToLowerInvariant()
        foreach ($form in $literalForms) {
            for ($i = $lower.IndexOf($form[1], [StringComparison]::Ordinal); $i -ge 0; $i = $lower.IndexOf($form[1], $i + 1, [StringComparison]::Ordinal)) {
                & $report $form[0] $i
            }
        }
    }
}

$sources = @("structural checks")
if ($literals.Count -gt 0) { $sources += "$($literals.Count) literal(s) from .local/privacy-scan-patterns.txt" }
if ($credentials.Count -gt 0) { $sources += "$($credentials.Count) value(s) from .local/spotify-dev-app.json" }

if ($findings.Count -gt 0) {
    Write-Host "privacy scan: $($findings.Count) finding(s) in tracked files ($($sources -join ', ')):" -ForegroundColor Red
    foreach ($finding in $findings) {
        Write-Host "  $finding"
    }

    exit 1
}

Write-Host "privacy scan: clean ($($files.Count) files; $($sources -join ', '))" -ForegroundColor Green
exit 0

#Requires -Version 5.1
<#
.SYNOPSIS
    zdtllmcli installer for Windows.
.DESCRIPTION
    Downloads the self-contained `zdt.exe` from the latest GitHub Release, verifies the
    SHA256 against the published checksums.txt, installs into %LOCALAPPDATA%\zdtllm\bin,
    and adds the directory to the user's persistent PATH. No .NET runtime is required —
    the binary bundles everything.
.PARAMETER Version
    Pin a specific release tag (e.g. "v0.1.0"). Defaults to latest.
.PARAMETER Uninstall
    Remove the installed binary. Settings under %USERPROFILE%\.zdtllm are preserved.
.PARAMETER InstallDir
    Override the install directory. Defaults to %LOCALAPPDATA%\zdtllm\bin.
.EXAMPLE
    irm https://raw.githubusercontent.com/ZERODAY-TECHNOLOGIES/ZDT-Agentic-CLI/main/install.ps1 | iex
.EXAMPLE
    # To pass parameters when piping from irm:
    & ([scriptblock]::Create((irm 'https://...install.ps1'))) -Version v0.1.0
    # …or set ZDT_VERSION / ZDT_UNINSTALL env vars before piping:
    $env:ZDT_VERSION = 'v0.1.0'; irm '...install.ps1' | iex
#>
[CmdletBinding()]
param(
    [string]$Version    = $env:ZDT_VERSION,
    [switch]$Uninstall  = ($env:ZDT_UNINSTALL -eq '1'),
    [string]$InstallDir = $(if ($env:ZDT_INSTALL_DIR) { $env:ZDT_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'zdtllm\bin' })
)

$ErrorActionPreference = 'Stop'
$Repo = 'ZERODAY-TECHNOLOGIES/ZDT-Agentic-CLI'

# --- Helpers ---------------------------------------------------------------

function Say($msg)  { Write-Host "→ $msg" -ForegroundColor Cyan }
function OK($msg)   { Write-Host "✓ $msg" -ForegroundColor Cyan }
function Warn($msg) { Write-Host "  $msg" -ForegroundColor Yellow }
function Die($msg)  { Write-Host "✗ $msg" -ForegroundColor Red; exit 1 }

# Ensure TLS 1.2 — older Windows defaults to TLS 1.0 which GitHub Releases rejects.
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# --- Uninstall path --------------------------------------------------------

if ($Uninstall) {
    $exe = Join-Path $InstallDir 'zdt.exe'
    if (Test-Path $exe) {
        Remove-Item -Force $exe
        # Try removing the empty parent dir too; non-empty -> leave alone.
        try { Remove-Item -Force $InstallDir -ErrorAction Stop } catch { }
        OK "removed $exe"
    } else {
        Say "$exe is not installed."
    }

    # Also clean the user PATH entry — best-effort, no-op if it's not there.
    $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
    if ($userPath) {
        $entries = $userPath -split ';' | Where-Object { $_ -and $_ -ne $InstallDir }
        $newPath = $entries -join ';'
        if ($newPath -ne $userPath) {
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
            OK "removed $InstallDir from user PATH"
        }
    }

    Write-Host "  Settings preserved at $env:USERPROFILE\.zdtllm — remove manually if no longer needed." -ForegroundColor DarkGray
    return
}

# --- Architecture ----------------------------------------------------------

# PROCESSOR_ARCHITECTURE reflects the running shell's bitness. PROCESSOR_ARCHITEW6432
# tells us the OS bitness when the shell itself is 32-bit-on-64. We always want the
# native arch so a 32-bit shell on 64-bit Windows still gets the x64 binary.
$nativeArch = if ($env:PROCESSOR_ARCHITEW6432) { $env:PROCESSOR_ARCHITEW6432 } else { $env:PROCESSOR_ARCHITECTURE }
$arch = switch ($nativeArch) {
    'AMD64' { 'x64' }
    'ARM64' { 'arm64' }
    default { Die "unsupported architecture: $nativeArch. zdt ships win-x64 and win-arm64 only." }
}
$rid = "win-$arch"
$asset = "zdt-$rid.zip"

# --- Resolve version -------------------------------------------------------

if (-not $Version) {
    Say "resolving latest release..."
    try {
        $latest = Invoke-RestMethod -UseBasicParsing "https://api.github.com/repos/$Repo/releases/latest"
        $Version = $latest.tag_name
    } catch {
        Die "could not resolve latest release tag from GitHub API: $($_.Exception.Message)"
    }
    if (-not $Version) { Die "GitHub API returned no tag_name. Pin a version with -Version v0.1.0." }
}

$downloadUrl = "https://github.com/$Repo/releases/download/$Version/$asset"
$checksumUrl = "https://github.com/$Repo/releases/download/$Version/checksums.txt"

# --- Download + verify -----------------------------------------------------

Say "downloading $Version for $rid..."
$tmp = New-Item -ItemType Directory -Force -Path (Join-Path $env:TEMP "zdt-install-$([guid]::NewGuid().ToString('N'))")
$zipPath = Join-Path $tmp.FullName $asset

try {
    try {
        Invoke-WebRequest -UseBasicParsing -Uri $downloadUrl -OutFile $zipPath
    } catch {
        Die "download failed: $downloadUrl ($($_.Exception.Message))"
    }

    # SHA256 verification — best-effort, skip with a warning if the release didn't ship checksums.txt.
    try {
        # Invoke-WebRequest -UseBasicParsing returns .Content as either a string OR a byte[]
        # depending on PS version + content-type sniffing. If it comes back as bytes,
        # `-split "`n"` would iterate ONE BYTE PER ENTRY (giving us "54", "51", "98", ...)
        # instead of one line per entry, and no pattern would ever match. Force UTF-8 decode.
        $response = Invoke-WebRequest -UseBasicParsing -Uri $checksumUrl -ErrorAction Stop
        $content = $response.Content
        if ($content -is [byte[]]) {
            $content = [System.Text.Encoding]::UTF8.GetString($content)
        }
        $checksumsRaw = $content
        # Build the regex by concatenation so PowerShell string interpolation can't mangle the
        # trailing end-of-line anchor. Earlier we had `\$` inside a "..." string which PowerShell
        # treated as literal `\$` and the regex matched against a non-existent literal $ in the
        # checksum lines — silently skipping every release that *did* ship checksums.
        $pattern = '^([0-9a-f]{64})\s+\*?' + [regex]::Escape($asset) + '$'
        $expected = $null
        foreach ($line in $checksumsRaw -split "`n") {
            $trimmed = $line.Trim()
            if ($trimmed -match $pattern) {
                $expected = $matches[1]
                break
            }
        }
        if ($expected) {
            $actual = (Get-FileHash -Algorithm SHA256 $zipPath).Hash.ToLower()
            if ($actual -ne $expected.ToLower()) {
                Die "SHA256 mismatch — expected $expected, got $actual. Aborting."
            }
            OK "checksum verified"
        } else {
            Warn "checksum entry for $asset not found in checksums.txt; skipping verification"
        }
    } catch {
        Warn "checksums.txt not available; skipping integrity check"
    }

    # --- Install ----------------------------------------------------------

    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    }
    # Force-overwrite to make re-running idempotent (and to handle upgrades cleanly).
    Expand-Archive -Force -Path $zipPath -DestinationPath $InstallDir
    OK "installed to $InstallDir\zdt.exe"
}
finally {
    if (Test-Path $tmp.FullName) { Remove-Item -Recurse -Force $tmp.FullName }
}

# --- PATH (user-scope, persistent) ----------------------------------------

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
$pathEntries = if ($userPath) { $userPath -split ';' } else { @() }
$pathAdded = $false
if ($pathEntries -notcontains $InstallDir) {
    $newPath = if ($userPath) { "$userPath;$InstallDir" } else { $InstallDir }
    [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
    $pathAdded = $true
}

# --- Banner ---------------------------------------------------------------

# Brand palette — same teal/gold combo the CLI's own startup banner uses.
$cyan  = "`e[38;2;27;234;205m"
$gold  = "`e[38;2;229;217;54m"
$mute  = "`e[38;2;104;123;137m"
$bold  = "`e[1m"
$reset = "`e[0m"

# PowerShell 5.1 doesn't support `e — fall back to plain text on older hosts.
if ($PSVersionTable.PSVersion.Major -lt 6) {
    $cyan = ""; $gold = ""; $mute = ""; $bold = ""; $reset = ""
}

Write-Host ""
Write-Host "$cyan╭──────────────────────────────────────────────────────────────╮$reset"
Write-Host "$cyan│$reset  $bold✓ zdt$reset installed at $bold$InstallDir\zdt.exe$reset"
Write-Host "$cyan│$reset"
if ($pathAdded) {
    Write-Host "$cyan│$reset  ${mute}PATH updated for the current user.$reset"
} else {
    Write-Host "$cyan│$reset  ${mute}PATH already contains$reset $InstallDir"
}
Write-Host "$cyan│$reset"
Write-Host "$cyan│$reset  ${bold}Add this directory to your PATH if your shell didn't pick it up:$reset"
Write-Host "$cyan│$reset      $gold$InstallDir$reset"
Write-Host "$cyan│$reset"
Write-Host "$cyan│$reset  ${bold}Activate in THIS shell (current PowerShell session):$reset"
Write-Host "$cyan│$reset      $gold`$env:Path = [Environment]::GetEnvironmentVariable('Path','User') + ';' + [Environment]::GetEnvironmentVariable('Path','Machine')$reset"
Write-Host "$cyan│$reset"
Write-Host "$cyan│$reset  Or open a new terminal — the PATH change persists across sessions."
Write-Host "$cyan│$reset"
Write-Host "$cyan│$reset  Then run $bold`zdt$reset for the first-run wizard, or $bold`zdt --help$reset."
Write-Host "$cyan╰──────────────────────────────────────────────────────────────╯$reset"
Write-Host ""

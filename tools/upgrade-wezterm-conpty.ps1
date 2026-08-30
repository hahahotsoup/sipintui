<#
.SYNOPSIS
    Side-load a newer ConPTY pair (conpty.dll + OpenConsole.exe) into WezTerm.

.DESCRIPTION
    WHY
    ---
    ConPTY is NOT a transparent pipe. It is a real conhost instance: your app
    writes into a console screen buffer, conhost parses the VT, then re-emits a
    translated VT stream to the terminal. Sixel is a DCS sequence (ESC P ... q
    ... ESC \) which has no representation in the screen-buffer model, so a
    conhost that does not know Sixel silently swallows it.

    The ConPTY passthrough mode requested in microsoft/terminal#1173 (open
    since 2019) has never shipped, so the Windows built-in conhost never
    forwards Sixel. Windows Terminal works around this by shipping its own
    OpenConsole (its bundled build parses and forwards Sixel). WezTerm does the
    same -- it side-loads conpty.dll + OpenConsole.exe from its install
    directory -- but the pair bundled with the 2024-02-03 release predates
    Sixel support (added in Windows Terminal 1.22, Aug 2024).

    Fix: replace BOTH files in the WezTerm install directory with a matched,
    newer pair from the official Microsoft.Windows.Console.ConPTY package.

    IMPORTANT: the two files must always be upgraded as a matched pair.
    Replacing only one crashes the host (see wezterm/wezterm#7774, pwsh exits
    with FailFast 0x80131623).

    Run as Administrator. Close all WezTerm windows afterwards (a config
    reload is not enough -- the old conpty.dll stays mapped in the process).

    WezTerm does not have to be closed before running this script: the old
    files are moved out of the way first, and a file that is mapped into a
    running process can still be renamed (only deleting it is blocked).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\upgrade-wezterm-conpty.ps1
#>
[CmdletBinding()]
param(
    [string] $WezTermDir = 'C:\Program Files\WezTerm',
    [string] $Version    = '1.24.260710001',
    [string] $Tag        = 'v1.24.11911.0',
    [string] $Sha256     = '9382AD7BECB7E4D84E300578D8E4F4DF28F43D979D9055D978C42913C47E0E9D',
    [ValidateSet('x64', 'arm64', 'x86')]
    [string] $Arch       = 'x64',
    [switch] $StopWezTerm
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Write-Step([string] $msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Write-Ok  ([string] $msg) { Write-Host "    $msg" -ForegroundColor Green }
function Write-Warn([string] $msg) { Write-Host "    $msg" -ForegroundColor Yellow }

# ---------------------------------------------------------------- 1. preflight
Write-Step 'Checking administrator privileges'
$principal = [Security.Principal.WindowsPrincipal] [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'This script must be run as Administrator (it writes to "C:\Program Files").'
}
Write-Ok 'Running elevated'

if (-not (Test-Path -LiteralPath $WezTermDir)) {
    throw "WezTerm directory not found: $WezTermDir`nPass -WezTermDir <path> if it is installed elsewhere."
}

# ------------------------------------------------------------ 2. wezterm state
if ($StopWezTerm) {
    $procs = @(Get-Process -Name 'wezterm', 'wezterm-gui', 'wezterm-mux-server' -ErrorAction SilentlyContinue)
    if ($procs.Count -gt 0) {
        Write-Step "Stopping $($procs.Count) WezTerm process(es)"
        $procs | Stop-Process -Force
        Start-Sleep -Milliseconds 800
        Write-Ok 'Stopped'
    } else {
        Write-Ok 'No WezTerm process running'
    }
} else {
    $running = @(Get-Process -Name 'wezterm', 'wezterm-gui', 'wezterm-mux-server' -ErrorAction SilentlyContinue)
    if ($running.Count -gt 0) {
        Write-Warn "$($running.Count) WezTerm process(es) running -- hot-swapping in place"
        Write-Warn 'You must restart WezTerm before the new ConPTY takes effect'
    } else {
        Write-Ok 'WezTerm is not running'
    }
}

# --------------------------------------------------------------- 3. download
$work = Join-Path $env:TEMP "conpty-upgrade-$Version"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$pkg = Join-Path $work 'conpty.nupkg'

$url = "https://github.com/microsoft/terminal/releases/download/$Tag/Microsoft.Windows.Console.ConPTY.$Version.nupkg"
if (Test-Path -LiteralPath $pkg) {
    Write-Step "Reusing cached package: $pkg"
} else {
    Write-Step "Downloading ConPTY $Version ($Tag)"
    Write-Host "    $url"
    [Net.ServicePointManager]::Tls12 = [Net.SecurityProtocolType]::Tls12
    (New-Object Net.WebClient).DownloadFile($url, $pkg)
}
Write-Ok ("{0:N1} KB" -f ((Get-Item $pkg).Length / 1KB))

# ------------------------------------------------------------ 4. verify hash
Write-Step 'Verifying SHA256'
$actual = (Get-FileHash -LiteralPath $pkg -Algorithm SHA256).Hash
if ($actual -ine $Sha256) {
    throw "Hash mismatch!`n  expected: $Sha256`n  actual:   $actual`nDelete $pkg and retry, or pass -Sha256 with the value printed by the release page."
}
Write-Ok $actual

# --------------------------------------------------------------- 5. extract
Write-Step 'Extracting'
$ext = Join-Path $work 'ext'
if (Test-Path -LiteralPath $ext) { Remove-Item -LiteralPath $ext -Recurse -Force }
$zip = Join-Path $work 'conpty.zip'
Copy-Item -LiteralPath $pkg -Destination $zip -Force
Expand-Archive -LiteralPath $zip -DestinationPath $ext -Force

$srcOpenConsole = Join-Path $ext "build/native/runtimes/$Arch/OpenConsole.exe"
$srcConpty      = Join-Path $ext "runtimes/win-$Arch/native/conpty.dll"
foreach ($f in @($srcOpenConsole, $srcConpty)) {
    if (-not (Test-Path -LiteralPath $f)) { throw "Package layout unexpected, missing: $f" }
}

# ------------------------------------------------------------ 6. verify sign
Write-Step 'Verifying Authenticode signatures (must be Microsoft Corporation)'
foreach ($f in @($srcOpenConsole, $srcConpty)) {
    $sig = Get-AuthenticodeSignature -LiteralPath $f
    if ($sig.Status -ne 'Valid') { throw "Signature invalid for $f : $($sig.Status)" }
    if ($sig.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw "Unexpected signer for $f : $($sig.SignerCertificate.Subject)"
    }
    Write-Ok "$(Split-Path $f -Leaf): Valid / $($sig.SignerCertificate.Subject)"
}

# ---------------------------------------------------------------- 7. backup
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backup = Join-Path $work "backup-$stamp"
New-Item -ItemType Directory -Path $backup -Force | Out-Null

$targets = @(
    [pscustomobject]@{ Name = 'OpenConsole.exe'; Src = $srcOpenConsole },
    [pscustomobject]@{ Name = 'conpty.dll';      Src = $srcConpty }
)

Write-Step "Backing up current files to $backup"
foreach ($t in $targets) {
    $dst = Join-Path $WezTermDir $t.Name
    if (Test-Path -LiteralPath $dst) {
        $old = Get-Item -LiteralPath $dst
        $t | Add-Member -NotePropertyName OldInfo -NotePropertyValue $old -Force
    } else {
        Write-Warn "$($t.Name) not present in $WezTermDir (nothing to back up)"
        $t | Add-Member -NotePropertyName OldInfo -NotePropertyValue $null -Force
    }
}

# ---------------------------------------------------------------- 8. install
# Hot-swap: a file mapped into a running process cannot be deleted, but it CAN
# be renamed. Moving the old one out of the way first lets us drop the new one
# in without killing WezTerm. (Backup dir is on the same volume as the install
# dir, so Move-Item stays a cheap rename rather than a copy.)
Write-Step "Installing ConPTY $Version into $WezTermDir"
foreach ($t in $targets) {
    $dst = Join-Path $WezTermDir $t.Name
    if (Test-Path -LiteralPath $dst) {
        Move-Item -LiteralPath $dst -Destination (Join-Path $backup $t.Name) -Force
        Write-Ok ("{0}: previous version moved aside (was {1}, {2:N0} bytes)" -f $t.Name, $t.OldInfo.LastWriteTime.ToString('yyyy-MM-dd'), $t.OldInfo.Length)
    }
    Copy-Item -LiteralPath $t.Src -Destination $dst -Force
    Write-Ok "$($t.Name): $Version in place"
}

# ------------------------------------------------------------- 9. post-check
Write-Step 'Post-install check'
foreach ($t in $targets) {
    $dst = Join-Path $WezTermDir $t.Name
    $sig = Get-AuthenticodeSignature -LiteralPath $dst
    $fi  = Get-Item -LiteralPath $dst
    Write-Ok ("{0}: {1:N0} bytes, signature {2}" -f $t.Name, $fi.Length, $sig.Status)
}

Write-Host ''
Write-Host 'Done.' -ForegroundColor Green
Write-Host 'Close ALL WezTerm windows and start it again (reloading the config is not enough).' -ForegroundColor Yellow
Write-Host 'Then verify Sixel with:' -ForegroundColor Yellow
Write-Host '    curl -sL https://raw.githubusercontent.com/saitoha/libsixel/master/images/snake.six | cat' -ForegroundColor Gray
Write-Host "Backup of the original files: $backup" -ForegroundColor Gray

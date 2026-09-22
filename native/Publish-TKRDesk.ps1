# Publish self-contained win-x64 release for TKR Desk
# Stages to temp first, verifies executable, packages ZIP, and updates website downloads.

param(
    [string]$DotnetPath = '.\.toolchain\dotnet\dotnet.exe'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\TKRDesk'
$stage = Join-Path $root 'dist\TKRDesk.staging'
$zip = Join-Path $root 'dist\TKRDesk-v1.0-win-x64.zip'
$zipStage = Join-Path $root 'dist\TKRDesk-v1.0-win-x64.staging.zip'
$webDownloads = "c:\Users\HP\Desktop\tkr\website\downloads"

function Assert-NoEmbeddedLegacyCodec([string]$filePath) {
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($marker in @('openh264-2.4.1-win64.dll', 'H264SharpNative-win64.dll')) {
        if ($ascii.IndexOf($marker, [System.StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Unsafe legacy codec marker '$marker' found in $filePath. Live dist was not modified."
        }
    }
}

function Get-UnsupportedCodecFiles([string]$directory) {
    Get-ChildItem -LiteralPath $directory -File -Recurse -ErrorAction Stop |
        Where-Object { $_.Name -like 'openh264-*.dll' -or $_.Name -in @('H264SharpNative-win64.dll', 'H264Sharp.dll') }
}

$previous = @{}
foreach ($name in @('DOTNET_CLI_HOME','DOTNET_CLI_TELEMETRY_OPTOUT','NUGET_PACKAGES','APPDATA','LOCALAPPDATA')) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}

try {
    $env:DOTNET_CLI_HOME = Join-Path $root '.dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:NUGET_PACKAGES = Join-Path $root '.nuget-packages'
    $env:APPDATA = Join-Path $env:DOTNET_CLI_HOME 'AppData\Roaming'
    $env:LOCALAPPDATA = Join-Path $env:DOTNET_CLI_HOME 'AppData\Local'

    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage }
    New-Item -ItemType Directory -Path $stage | Out-Null

    Write-Host "Publishing TKR Desk with $DotnetPath..."
    & $DotnetPath publish (Join-Path $PSScriptRoot 'ApnaRemote.Windows\ApnaRemote.Windows.csproj') `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $stage `
        --configfile (Join-Path $PSScriptRoot 'NuGet.Config')

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE. Live dist was not modified."
    }

    $exe = Join-Path $stage 'TKRDesk.exe'
    if (-not (Test-Path $exe)) {
        throw "Publish output missing TKRDesk.exe. Live dist was not modified."
    }
    Assert-NoEmbeddedLegacyCodec $exe

    if (Get-UnsupportedCodecFiles $stage) {
        throw 'Unsafe H264Sharp/OpenH264 native files remained in the staged payload.'
    }

    # Add quickstart README for users
    $readmeContent = @"
========================================================================
TKR Desk v1.0 — High-Speed Remote Desktop & Wireless Hub
Official Website: https://tkrdesk.com
========================================================================

QUICK START:
1. Double-click TKRDesk.exe to run. No installation or setup required.
2. 9-DIGIT TKR ID & REMOTE DESKTOP:
   - Your deterministic 9-digit TKR ID is displayed on the Share panel (e.g. 842 195 037).
   - Click 'Start sharing' on Host PC.
   - Enter the 9-digit TKR ID (or IP address) & 6-digit PIN on Viewer PC to connect instantly.
   - Remote Clipboard: Copy text on one PC, paste instantly on the other (Ctrl+C / Ctrl+V)!
3. LAB GRID (CCTV MATRIX):
   - Switch to 'Lab Grid (CCTV)' to monitor all classroom / cyber cafe workstations in real-time.
   - 1-click connect to take over any computer, or broadcast announcements to all lab screens.
4. SCREEN BROADCAST (1-TO-HUNDREDS):
   - Switch to 'Screen Broadcast' to stream presenter desktop to hundreds of attendee screens over UDP multicast.
5. FAST FILE TRANSFER:
   - Switch to 'File Transfer' to send multi-gigabyte files directly across Wi-Fi.
   - Received files are saved in Downloads\TKRDesk-Transfers with SHA-256 verification.
6. WIRELESS PRINTER (CTRL+P INTEGRATION):
   - Switch to 'USB Hub' tab -> 'Install / Fix Printer'.
   - Press Ctrl+P in Chrome, MS Word, Acrobat, Tally and pick 'TKR Desk Wireless Printer'.
7. WI-FI PEN DRIVE & USB PERIPHERALS:
   - Select your USB Drive to access files across Wi-Fi. Attach USB hardware over LAN.

FIREWALL NOTE:
If Windows Firewall blocks local incoming connections,
right-click 'Allow-Firewall.bat' and select 'Run as Administrator'.
========================================================================
"@
    Set-Content -Path (Join-Path $stage 'README.txt') -Value $readmeContent -Encoding utf8

    # Copy firewall helper if available
    $fwSrc = Join-Path $PSScriptRoot 'Allow-Firewall.bat'
    if (Test-Path $fwSrc) {
        Copy-Item $fwSrc (Join-Path $stage 'Allow-Firewall.bat') -Force
    }

    # Calculate SHA256
    $hash = (Get-FileHash -Algorithm SHA256 $exe).Hash
    "$hash  TKRDesk.exe" | Set-Content -Encoding ascii (Join-Path $stage 'SHA256SUMS.txt')

    # Create ZIP archive
    if (Test-Path $zipStage) { Remove-Item -Force $zipStage }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipStage -Force

    # Move staged folder to dist/TKRDesk
    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Rename-Item -Path $stage -NewName 'TKRDesk'
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Rename-Item -Path $zipStage -NewName 'TKRDesk-v1.0-win-x64.zip'

    # Copy to Website Downloads
    if (-not (Test-Path $webDownloads)) {
        New-Item -ItemType Directory -Path $webDownloads -Force | Out-Null
    }
    Copy-Item (Join-Path $out 'TKRDesk.exe') (Join-Path $webDownloads 'TKRDesk.exe') -Force
    Copy-Item $zip (Join-Path $webDownloads 'TKRDesk-v1.0-win-x64.zip') -Force

    $exeSize = (Get-Item (Join-Path $webDownloads 'TKRDesk.exe')).Length
    $zipSize = (Get-Item (Join-Path $webDownloads 'TKRDesk-v1.0-win-x64.zip')).Length

    Write-Host "`nSUCCESS: TKR Desk published successfully!"
    Write-Host "=========================================="
    Write-Host "Executable: $(Join-Path $out 'TKRDesk.exe') ($([math]::Round($exeSize/1MB, 2)) MB)"
    Write-Host "Zip Archive: $zip ($([math]::Round($zipSize/1MB, 2)) MB)"
    Write-Host "SHA256: $hash"
    Write-Host "Copied to Website Downloads: $webDownloads"
}
catch {
    if (Test-Path $stage) { Remove-Item -Recurse -Force $stage -ErrorAction SilentlyContinue }
    if (Test-Path $zipStage) { Remove-Item -Force $zipStage -ErrorAction SilentlyContinue }
    throw
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}

# Publish self-contained win-x64 folder for copying to a second PC.
# Stages to a temp folder first; only replaces the live dist output after success.
# Does not change firewall rules. Does not sign the binary.

param(
    [string]$DotnetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root 'dist\ApnaRemote-TwoPcLab'
$stage = Join-Path $root 'dist\ApnaRemote-TwoPcLab.staging'
$zip = Join-Path $root 'dist\ApnaRemote-TwoPcLab-win-x64.zip'
$zipStage = Join-Path $root 'dist\ApnaRemote-TwoPcLab-win-x64.staging.zip'

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

    $exe = Join-Path $stage 'ApnaRemote.exe'
    if (-not (Test-Path $exe)) {
        throw "Publish output missing ApnaRemote.exe. Live dist was not modified."
    }
    Assert-NoEmbeddedLegacyCodec $exe

    # H264Sharp 1.6.0 is ABI-incompatible with patched OpenH264 2.6.0 and its 2.4.1
    # dependency is vulnerable. The production lab payload is therefore JPEG-only.
    if (Get-UnsupportedCodecFiles $stage) {
        throw 'Unsafe H264Sharp/OpenH264 native files remained in the staged payload.'
    }

    Copy-Item (Join-Path $root 'docs\USER-GUIDE.md') (Join-Path $stage 'TWO-PC-LAB.md') -Force
    $batSrc = Join-Path $PSScriptRoot 'Install-Lab.bat'
    if (Test-Path $batSrc) {
        Copy-Item $batSrc (Join-Path $stage 'Install-Lab.bat') -Force
    }
    $fwSrc = Join-Path $PSScriptRoot 'Allow-Firewall.bat'
    if (Test-Path $fwSrc) {
        Copy-Item $fwSrc (Join-Path $stage 'Allow-Firewall.bat') -Force
    } elseif (Test-Path (Join-Path $root 'dist\ApnaRemote-TwoPcLab\Allow-Firewall.bat')) {
        Copy-Item (Join-Path $root 'dist\ApnaRemote-TwoPcLab\Allow-Firewall.bat') (Join-Path $stage 'Allow-Firewall.bat') -Force
    }

    Get-FileHash -Algorithm SHA256 $exe | ForEach-Object {
        "$($_.Hash)  ApnaRemote.exe" | Set-Content -Encoding ascii (Join-Path $stage 'SHA256SUMS.txt')
    }

    if (Test-Path $zipStage) { Remove-Item -Force $zipStage }
    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipStage -Force

    if (Test-Path $out) { Remove-Item -Recurse -Force $out }
    Rename-Item -Path $stage -NewName 'ApnaRemote-TwoPcLab'
    if (Test-Path $zip) { Remove-Item -Force $zip }
    Rename-Item -Path $zipStage -NewName 'ApnaRemote-TwoPcLab-win-x64.zip'

    Write-Host "Published folder: $out"
    Write-Host "Zip: $zip"
    Get-ChildItem $out | Select-Object Name, Length | Format-Table -AutoSize
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

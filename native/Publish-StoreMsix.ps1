# Publish full-trust MSIX for Microsoft Store sideload / Partner Center prep.
# Does not change firewall rules. Does not upload to the Store.
# Lab ZIP path remains: Publish-TwoPcLab.ps1

param(
    [string]$DotnetPath = 'dotnet',
    [string]$MakeAppxPath = '',
    [string]$SignToolPath = ''
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root 'dist\store\payload'
$outDir = Join-Path $root 'dist\store'
$manifestSrc = Join-Path $PSScriptRoot 'store-package\AppxManifest.xml'
$assetsSrc = Join-Path $PSScriptRoot 'store-package\Assets'
$certDir = Join-Path $root 'dist\store\certs'
$certPath = Join-Path $certDir 'ApnaRemoteLab.pfx'
$certPassword = 'ApnaRemoteLab-Sideload-Only'

function Assert-NoEmbeddedLegacyCodec([string]$filePath) {
    $bytes = [System.IO.File]::ReadAllBytes($filePath)
    $ascii = [System.Text.Encoding]::ASCII.GetString($bytes)
    foreach ($marker in @('openh264-2.4.1-win64.dll', 'H264SharpNative-win64.dll')) {
        if ($ascii.Contains($marker, [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe legacy codec marker '$marker' found in $filePath. Store payload was not packaged."
        }
    }
}

function Find-KitTool([string]$name, [string]$override) {
    if ($override -and (Test-Path $override)) { return $override }
    $pf86 = [Environment]::GetEnvironmentVariable('ProgramFiles(x86)')
    if ([string]::IsNullOrWhiteSpace($pf86)) { $pf86 = 'C:\Program Files (x86)' }
    $kit = Get-ChildItem (Join-Path $pf86 "Windows Kits\10\bin\*\x64\$name") -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($kit) { return $kit.FullName }
    $ack = Join-Path $pf86 "Windows Kits\10\App Certification Kit\$name"
    if (Test-Path $ack) { return $ack }
    throw "Could not find $name. Install Windows 10/11 SDK packaging tools."
}

function Get-FourPartVersion {
    $props = Join-Path $PSScriptRoot 'Directory.Build.props'
    $raw = Select-String -Path $props -Pattern '<Version>([^<]+)</Version>' | ForEach-Object { $_.Matches[0].Groups[1].Value }
    if (-not $raw) { return '0.14.0.0' }
    $core = ($raw -split '-', 2)[0]
    $parts = @($core.Split('.') | ForEach-Object { [int]$_ })
    while ($parts.Count -lt 4) { $parts += 0 }
    return ($parts[0..3] -join '.')
}

function Ensure-StoreAssets {
    if (-not (Test-Path $assetsSrc)) { New-Item -ItemType Directory -Path $assetsSrc | Out-Null }
    $icon = Join-Path $PSScriptRoot 'ApnaRemote.Windows\Assets\apna-remote-icon.png'
    if (-not (Test-Path $icon)) { throw "Missing icon: $icon" }

    Add-Type -AssemblyName System.Drawing
    $src = [System.Drawing.Image]::FromFile($icon)
    try {
        $specs = @{
            'StoreLogo.png' = 50
            'Square44x44Logo.png' = 44
            'Square150x150Logo.png' = 150
            'Wide310x150Logo.png' = @(310, 150)
            'SplashScreen.png' = @(620, 300)
        }
        foreach ($name in $specs.Keys) {
            $dest = Join-Path $assetsSrc $name
            $size = $specs[$name]
            if ($size -is [array]) {
                $w = [int]$size[0]; $h = [int]$size[1]
                $bmp = New-Object System.Drawing.Bitmap $w, $h
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.Clear([System.Drawing.Color]::FromArgb(255, 15, 26, 44))
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $side = [Math]::Min($w, $h) * 0.55
                $x = ($w - $side) / 2; $y = ($h - $side) / 2
                $g.DrawImage($src, $x, $y, $side, $side)
                $g.Dispose()
                $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
                $bmp.Dispose()
            }
            else {
                $s = [int]$size
                $bmp = New-Object System.Drawing.Bitmap $s, $s
                $g = [System.Drawing.Graphics]::FromImage($bmp)
                $g.Clear([System.Drawing.Color]::FromArgb(255, 37, 99, 235))
                $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
                $g.DrawImage($src, 0, 0, $s, $s)
                $g.Dispose()
                $bmp.Save($dest, [System.Drawing.Imaging.ImageFormat]::Png)
                $bmp.Dispose()
            }
        }
    }
    finally { $src.Dispose() }
}

function Ensure-SideloadCert {
    if (-not (Test-Path $certDir)) { New-Item -ItemType Directory -Path $certDir | Out-Null }
    $secure = ConvertTo-SecureString -String $certPassword -Force -AsPlainText
    if (-not (Test-Path $certPath)) {
        $cert = New-SelfSignedCertificate `
            -Type Custom `
            -Subject 'CN=Apna Remote Lab' `
            -FriendlyName 'Apna Remote Lab MSIX sideload' `
            -KeyUsage DigitalSignature `
            -KeyExportPolicy Exportable `
            -TextExtension @(
                '2.5.29.37={text}1.3.6.1.5.5.7.3.3',
                '2.5.29.19={text}CA=false'
            ) `
            -CertStoreLocation 'Cert:\CurrentUser\My'
        try {
            Export-PfxCertificate -Cert $cert -FilePath $certPath -Password $secure | Out-Null
        }
        finally {
            Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -ErrorAction SilentlyContinue
        }
    }

    # Prefer TrustedPeople only (adding to Root can pop a blocking security UI).
    # If Add-AppxPackage still rejects the signature, enable Developer Mode or
    # manually import the .cer into Current User Trusted Root (see SIDELOAD.txt).
    $loaded = New-Object System.Security.Cryptography.X509Certificates.X509Certificate2($certPath, $certPassword,
        [System.Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
    $store = New-Object System.Security.Cryptography.X509Certificates.X509Store('TrustedPeople', 'CurrentUser')
    $store.Open('ReadWrite')
    if (-not ($store.Certificates | Where-Object { $_.Thumbprint -eq $loaded.Thumbprint })) {
        $store.Add($loaded)
    }
    $store.Close()

    $cerPath = Join-Path $certDir 'ApnaRemoteLab.cer'
    [System.IO.File]::WriteAllBytes($cerPath, $loaded.Export([System.Security.Cryptography.X509Certificates.X509ContentType]::Cert))
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

    $makeappx = Find-KitTool 'makeappx.exe' $MakeAppxPath
    $signtool = Find-KitTool 'signtool.exe' $SignToolPath
    $version = Get-FourPartVersion

    Write-Host "MSIX version: $version"
    Write-Host "makeappx: $makeappx"

    Ensure-StoreAssets

    if (Test-Path $payload) { Remove-Item -Recurse -Force $payload }
    New-Item -ItemType Directory -Path $payload | Out-Null
    if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

    & $DotnetPath publish (Join-Path $PSScriptRoot 'ApnaRemote.Windows\ApnaRemote.Windows.csproj') `
        -c Release `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -o $payload `
        --configfile (Join-Path $PSScriptRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

    $exe = Join-Path $payload 'ApnaRemote.exe'
    if (-not (Test-Path $exe)) { throw "Publish output missing ApnaRemote.exe" }
    Assert-NoEmbeddedLegacyCodec $exe

    # Manifest with synced version
    [xml]$manifest = Get-Content -Raw $manifestSrc
    $manifest.Package.Identity.Version = $version
    $manifestPath = Join-Path $payload 'AppxManifest.xml'
    $manifest.Save($manifestPath)

    $assetsDest = Join-Path $payload 'Assets'
    if (Test-Path $assetsDest) { Remove-Item -Recurse -Force $assetsDest }
    Copy-Item -Recurse $assetsSrc $assetsDest

    # Remove files that break packaging / are unnecessary
    Get-ChildItem $payload -Filter '*.pdb' -Recurse | Remove-Item -Force
    Get-ChildItem $payload -Filter '*.xml' -File | Where-Object { $_.Name -ne 'AppxManifest.xml' } | ForEach-Object {
        # keep runtime config xml
        if ($_.Name -notmatch 'runtimeconfig|deps') { }
    }

    $msixName = "ApnaRemote_$version`_x64.msix"
    $msixPath = Join-Path $outDir $msixName
    if (Test-Path $msixPath) { Remove-Item -Force $msixPath }

    & $makeappx pack /d $payload /p $msixPath /o
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with exit code $LASTEXITCODE" }

    Ensure-SideloadCert
    & $signtool sign /fd SHA256 /a /f $certPath /p $certPassword $msixPath
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with exit code $LASTEXITCODE" }

    Copy-Item (Join-Path $root 'docs\RELEASE.md') (Join-Path $outDir 'STORE-PREP.md') -Force
    Copy-Item (Join-Path $root 'docs\PRIVACY.md') (Join-Path $outDir 'PRIVACY.md') -Force

    @"
Apna Remote MSIX (sideload)
Version: $version
Package: $msixPath

Install (PowerShell as user):
  Add-AppxPackage -Path "$msixPath"

Notes:
- Publisher CN=Apna Remote Lab is for local sideload only.
- Before Partner Center upload, sync Identity in native\store-package\AppxManifest.xml with Package identity.
- Store re-signs on publish; keep lab ZIP via Publish-TwoPcLab.ps1.
"@ | Set-Content -Encoding utf8 (Join-Path $outDir 'SIDELOAD.txt')

    Write-Host "Published MSIX: $msixPath"
    Get-Item $msixPath | Select-Object FullName, Length | Format-List
}
finally {
    foreach ($name in $previous.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}

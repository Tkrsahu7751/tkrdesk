[CmdletBinding()]
param(
    [string]$DotnetPath = 'dotnet',
    [switch]$NoRestore,
    [switch]$OpenApp
)

# Run only after the user authorizes tool/build execution. Does not install anything.
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$environmentNames = @('DOTNET_CLI_HOME', 'DOTNET_CLI_TELEMETRY_OPTOUT', 'DOTNET_SKIP_FIRST_TIME_EXPERIENCE', 'DOTNET_GENERATE_ASPNET_CERTIFICATE', 'DOTNET_ADD_GLOBAL_TOOLS_TO_PATH', 'DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE', 'NUGET_PACKAGES', 'NUGET_HTTP_CACHE_PATH', 'NUGET_SCRATCH', 'APPDATA', 'LOCALAPPDATA')
$previousEnvironment = @{}
foreach ($environmentName in $environmentNames) {
    $previousEnvironment[$environmentName] = [Environment]::GetEnvironmentVariable($environmentName, 'Process')
}

Push-Location -LiteralPath $PSScriptRoot
try {
    # Keep SDK first-use state and package caches inside the approved project.
    $env:DOTNET_CLI_HOME = Join-Path $projectRoot '.dotnet-home'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    $env:DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    $env:NUGET_PACKAGES = Join-Path $projectRoot '.nuget-packages'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $env:DOTNET_CLI_HOME 'nuget-http-cache'
    $env:NUGET_SCRATCH = Join-Path $env:DOTNET_CLI_HOME 'nuget-scratch'
    # NuGet probes profile locations even with --configfile; isolate those probes too.
    $env:APPDATA = Join-Path $env:DOTNET_CLI_HOME 'AppData\Roaming'
    $env:LOCALAPPDATA = Join-Path $env:DOTNET_CLI_HOME 'AppData\Local'
    foreach ($localDirectory in @($env:APPDATA, $env:LOCALAPPDATA, $env:NUGET_HTTP_CACHE_PATH, $env:NUGET_SCRATCH)) {
        New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null
    }

    & $DotnetPath --version
    if ($LASTEXITCODE -ne 0) { throw 'A stable .NET 10 SDK is required. No installation was attempted.' }

    $windowsProject = 'ApnaRemote.Windows\ApnaRemote.Windows.csproj'
    $checksProject = 'ApnaRemote.Core.Checks\ApnaRemote.Core.Checks.csproj'
    if (-not $NoRestore) {
        & $DotnetPath restore $windowsProject --configfile 'NuGet.Config'
        if ($LASTEXITCODE -ne 0) { throw 'Windows restore failed. Review the NuGet error above.' }
        & $DotnetPath restore $checksProject --configfile 'NuGet.Config'
        if ($LASTEXITCODE -ne 0) { throw 'Core checks restore failed. Review the NuGet error above.' }
    }

    # PC-to-PC Windows is the active product scope. The deferred Android experiment must not
    # make the primary Windows validation fail because of an unrelated mobile workload/RID.
    & $DotnetPath build $windowsProject --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Windows app build failed.' }
    & $DotnetPath build $checksProject --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core checks build failed.' }

    & $DotnetPath run --project $checksProject --configuration Release --no-build --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Core behavioral checks failed.' }

    # TLS server certificates use the signed-in user's temporary key container. NuGet's isolated
    # APPDATA/LOCALAPPDATA paths are useful for restore, but are not a valid Schannel profile.
    foreach ($profileName in @('APPDATA', 'LOCALAPPDATA')) {
        [Environment]::SetEnvironmentVariable($profileName, $previousEnvironment[$profileName], 'Process')
    }

    $appDll = Join-Path $PSScriptRoot 'ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\TKRDesk.dll'
    if (-not (Test-Path $appDll)) {
        $appDll = Join-Path $PSScriptRoot 'ApnaRemote.Windows\bin\Release\net10.0-windows10.0.19041.0\ApnaRemote.dll'
    }
    & $DotnetPath $appDll --discovery-lifecycle-selftest
    if ($LASTEXITCODE -ne 0) { throw 'Discovery lifecycle checks failed.' }
    & $DotnetPath $appDll --host-lifecycle-selftest
    if ($LASTEXITCODE -ne 0) { throw 'Host lifecycle checks failed.' }
    & $DotnetPath $appDll --network-h264-selftest
    if ($LASTEXITCODE -ne 0) { throw 'Network H.264 codec check failed.' }
    & $DotnetPath $appDll --viewer-lifecycle-selftest
    if ($LASTEXITCODE -ne 0) { throw 'Viewer lifecycle checks failed.' }

    if ($OpenApp) {
        # Explicit opt-in UI launch. A listener starts only after the user presses Start sharing.
        & $DotnetPath run --project $windowsProject --configuration Release --no-build --no-restore
        if ($LASTEXITCODE -ne 0) { throw 'Native app exited with an error.' }
    }
}
finally {
    Pop-Location
    foreach ($environmentName in $environmentNames) {
        [Environment]::SetEnvironmentVariable($environmentName, $previousEnvironment[$environmentName], 'Process')
    }
}

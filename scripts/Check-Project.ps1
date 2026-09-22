[CmdletBinding()]
param([switch]$RequireArtifacts)

$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
$errors = [Collections.Generic.List[string]]::new()
$warnings = [Collections.Generic.List[string]]::new()

function Resolve-InProject([string]$RelativePath) {
    $candidate = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    $prefix = $root + [IO.Path]::DirectorySeparatorChar
    if (-not $candidate.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
        $errors.Add("Path escapes project root: $RelativePath")
        return $null
    }
    return $candidate
}

$required = @(
    'AGENTS.md', 'README.md', 'STATE.json', 'CONTINUE-HERE.md', 'WORKLOG.md',
    'docs/ARCHITECTURE.md', 'docs/DECISIONS.md', 'docs/ROADMAP.md',
    'docs/TESTING.md', 'docs/USER-GUIDE.md', 'docs/RELEASE.md',
    'docs/PRIVACY.md', 'docs/PROJECT-MAINTENANCE.md',
    'native/AGENTS.md', 'native/README.md', 'evidence/README.md'
)

foreach ($relative in $required) {
    $path = Resolve-InProject $relative
    if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) {
        $errors.Add("Required file missing: $relative")
    }
}

$state = $null
try {
    $state = Get-Content -LiteralPath (Join-Path $root 'STATE.json') -Raw | ConvertFrom-Json
} catch {
    $errors.Add("STATE.json is invalid: $($_.Exception.Message)")
}

if ($state) {
    foreach ($property in $state.canonical_docs.PSObject.Properties) {
        $relative = [string]$property.Value
        $path = Resolve-InProject $relative
        if ($path -and -not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $errors.Add("STATE.json document missing: $relative")
        }
    }

    $declaredApps = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($property in $state.artifacts.PSObject.Properties) {
        $artifact = $property.Value
        $relative = [string]$artifact.path
        [void]$declaredApps.Add($relative.Replace('\', '/'))
        $path = Resolve-InProject $relative
        if (-not $path) { continue }
        if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
            $message = "Declared artifact missing: $relative"
            if ($RequireArtifacts) { $errors.Add($message) } else { $warnings.Add($message) }
            continue
        }
        $actualHash = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash
        if ($actualHash -ne [string]$artifact.sha256) {
            $errors.Add("Artifact hash mismatch: $relative")
        }
    }

    $dist = Join-Path $root 'dist'
    if (Test-Path -LiteralPath $dist) {
        $actualApps = Get-ChildItem -LiteralPath $dist -Recurse -File | Where-Object { $_.Extension -in '.exe', '.apk' }
        foreach ($app in $actualApps) {
            $relative = $app.FullName.Substring($root.Length + 1).Replace('\', '/')
            if (-not $declaredApps.Contains($relative)) {
                $errors.Add("Undeclared app artifact: $relative")
            }
        }
        if ($actualApps.Count -gt 2) {
            $errors.Add("dist contains more than one EXE and one APK")
        }
    }
}

$obsoleteRoots = @('APNAREMOTE-MASTER-PLAN.md', 'TESTING.md', 'TWO-PC-LAB.md', 'STORE-PREP.md', 'PRIVACY.md')
foreach ($name in $obsoleteRoots) {
    if (Test-Path -LiteralPath (Join-Path $root $name)) {
        $errors.Add("Obsolete duplicate root document exists: $name")
    }
}

$generated = Get-ChildItem -LiteralPath (Join-Path $root 'native') -Directory -Recurse -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'bin', 'obj' }
if ($generated) {
    $warnings.Add("Disposable native bin/obj folders exist: $($generated.Count)")
}

if ($warnings.Count) {
    Write-Host 'Warnings:' -ForegroundColor Yellow
    $warnings | ForEach-Object { Write-Host "- $_" }
}
if ($errors.Count) {
    Write-Host 'Project check failed:' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "- $_" }
    exit 1
}

Write-Host 'Project structure, state links and declared artifacts are consistent.' -ForegroundColor Green
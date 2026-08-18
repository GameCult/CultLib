param(
    [string] $EveRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "..\Eve"),
    [switch] $SkipDependencyInstall
)

$ErrorActionPreference = "Stop"
$cultLibRoot = Split-Path -Parent $PSScriptRoot
$EveRoot = [IO.Path]::GetFullPath($EveRoot)
$networkSample = Join-Path $cultLibRoot "samples\eve-browser-network\Program.cs"
$networkSampleSource = Get-Content -LiteralPath $networkSample -Raw
if ($networkSampleSource -match 'OnCultNet\s*<\s*CultNetOperationRequestMessage' -or
    $networkSampleSource -match 'Convert\.FromBase64String\s*\(\s*request\.Payload') {
    throw "The getting-started provider has regressed to hand-written CultNet operation envelope dispatch."
}
foreach ($requiredPrimitive in @("CultNetOperationServer", "EveSurface.Create")) {
    if ($networkSampleSource -notmatch [regex]::Escape($requiredPrimitive)) {
        throw "The getting-started provider is missing the public primitive '$requiredPrimitive'."
    }
}
if ($networkSampleSource -notmatch 'new\s+CultMeshSessionTarget\s*\(') {
    throw "The getting-started client must address one explicit Verse/authority-runtime target."
}
$cultMeshClientSource = Get-Content -LiteralPath (Join-Path $cultLibRoot "src\GameCult.Mesh\CultMeshClient.cs") -Raw
if ($cultMeshClientSource -match 'ConnectAsync\s*\(\s*string\s+(endpointId|verseId)' -or
    $cultMeshClientSource -match 'Lease(Document|Collection)Async[^\(]*\(\s*string\s+(endpointId|verseId)') {
    throw "CultMeshClient must not expose an ambiguous one-string session identity."
}
$dotnetPackageRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ("eve-getting-started-nuget-" + [guid]::NewGuid().ToString("N"))
$sourceRevision = (& git -C $cultLibRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($sourceRevision)) {
    throw "Could not determine the CultLib source revision for local package identity."
}
$verificationNonce = [guid]::NewGuid().ToString("N").Substring(0, 8)
$cultLibLocalVersion = "1.0.34-local.$sourceRevision.$verificationNonce"
$surfaceLocalVersion = "0.3.3-local.$sourceRevision.$verificationNonce"
try {
    & (Join-Path $EveRoot "scripts\pack-dotnet-surface.ps1") `
        -CultLibRoot $cultLibRoot `
        -OutputDirectory $dotnetPackageRoot `
        -CultLibPackageVersion $cultLibLocalVersion `
        -SurfacePackageVersion $surfaceLocalVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The clean .NET package-consumer checkpoint failed."
    }

    $cultLibPackage = Get-ChildItem -LiteralPath $dotnetPackageRoot -Filter "GameCult.Mesh.*.nupkg" |
        Where-Object { $_.Name -notlike "*.snupkg" } |
        Select-Object -First 1
    $surfacePackage = Get-ChildItem -LiteralPath $dotnetPackageRoot -Filter "GameCult.Eve.Surface.*.nupkg" |
        Where-Object { $_.Name -notlike "*.snupkg" } |
        Select-Object -First 1
    if (-not $cultLibPackage -or -not $surfacePackage) {
        throw "The local package feed is missing CultMesh or Eve surface artifacts."
    }
    $cultLibVersion = [regex]::Match($cultLibPackage.Name, '^GameCult\.Mesh\.(.+)\.nupkg$').Groups[1].Value
    $surfaceVersion = [regex]::Match($surfacePackage.Name, '^GameCult\.Eve\.Surface\.(.+)\.nupkg$').Groups[1].Value

    & (Join-Path $PSScriptRoot "verify-eve-two-runtime-sample.ps1") `
        -EveRoot $EveRoot `
        -SkipDependencyInstall:$SkipDependencyInstall
    if ($LASTEXITCODE -ne 0) {
        throw "The clean package-consumer checkpoint failed."
    }

    $nodeCommand = Get-Command node -ErrorAction Stop
    $playwrightCli = Join-Path $cultLibRoot "node_modules\playwright-core\cli.js"
    if (-not (Test-Path -LiteralPath $playwrightCli -PathType Leaf)) {
        throw "playwright-core is missing after dependency setup: $playwrightCli"
    }

    & $nodeCommand.Source (Join-Path $PSScriptRoot "verify-eve-browser-network.mjs") `
        --eve-root $EveRoot `
        --package-feed $dotnetPackageRoot `
        --cultlib-package-version $cultLibVersion `
        --eve-surface-package-version $surfaceVersion
    if ($LASTEXITCODE -ne 0) {
        throw "The real browser/C# network checkpoint failed."
    }

    Write-Host "Eve getting-started verification passed: clean .NET and TypeScript artifacts, DOM and headless convergence, real Chromium and C# clients, Odin discovery, authority-bound routes, receipts, persistence, and route replacement."
} finally {
    if (Test-Path -LiteralPath $dotnetPackageRoot) {
        Remove-Item -LiteralPath $dotnetPackageRoot -Recurse -Force
    }
}

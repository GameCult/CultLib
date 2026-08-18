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
$discoverySource = Get-Content -LiteralPath (Join-Path $cultLibRoot "src\GameCult.Mesh\CultMeshDiscoveryService.cs") -Raw
if ($discoverySource -notmatch 'AuthorityRuntimeIds\.Contains\(query\.AuthorityRuntimeId') {
    throw "CultMesh discovery must prove that a selected Verse route advertises the requested authority runtime."
}

$dotnetPackageRoot = Join-Path ([IO.Path]::GetTempPath()) `
    ("eve-getting-started-nuget-" + [guid]::NewGuid().ToString("N"))
try {
    & (Join-Path $EveRoot "scripts\pack-dotnet-surface.ps1") `
        -CultLibRoot $cultLibRoot `
        -OutputDirectory $dotnetPackageRoot
    if ($LASTEXITCODE -ne 0) {
        throw "The clean .NET package-consumer checkpoint failed."
    }
} finally {
    if (Test-Path -LiteralPath $dotnetPackageRoot) {
        Remove-Item -LiteralPath $dotnetPackageRoot -Recurse -Force
    }
}

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
    --eve-root $EveRoot
if ($LASTEXITCODE -ne 0) {
    throw "The real browser/C# network checkpoint failed."
}

Write-Host "Eve getting-started verification passed: clean .NET and TypeScript artifacts, DOM and headless convergence, real Chromium and C# clients, Odin discovery, receipts, persistence, and route replacement."

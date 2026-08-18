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

Write-Host "Eve getting-started verification passed: clean artifacts, DOM and headless convergence, real Chromium and C# clients, Odin discovery, receipts, persistence, and route replacement."

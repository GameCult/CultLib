param(
    [string] $EveRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "..\Eve"),
    [switch] $SkipDependencyInstall
)

$ErrorActionPreference = "Stop"
$cultLibRoot = Split-Path -Parent $PSScriptRoot
$EveRoot = [IO.Path]::GetFullPath($EveRoot)

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

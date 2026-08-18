param(
    [string] $EveRoot = (Join-Path (Split-Path -Parent $PSScriptRoot) "..\Eve")
)

$ErrorActionPreference = "Stop"

$cultLibRoot = Split-Path -Parent $PSScriptRoot
$samplePath = Join-Path $cultLibRoot "samples\eve-two-runtime\sample.mjs"
$eveBrowserPackage = Join-Path $EveRoot "packages\eve-browser-lowering"
$eveContractsPackage = Join-Path $EveRoot "packages\eve-contracts"
$packageRoots = @(
    (Join-Path $cultLibRoot "packages\cultcache-ts"),
    (Join-Path $cultLibRoot "packages\cultnet-ts"),
    (Join-Path $cultLibRoot "packages\cultmesh-ts"),
    $eveContractsPackage,
    $eveBrowserPackage
)
$onWindows = [System.Environment]::OSVersion.Platform -eq [System.PlatformID]::Win32NT

$nodeCommand = Get-Command node -ErrorAction SilentlyContinue
if (-not $nodeCommand) {
    $standardNode = if ($onWindows -and $env:ProgramFiles) {
        Join-Path $env:ProgramFiles "nodejs\node.exe"
    }
    else {
        ""
    }
    if (Test-Path -LiteralPath $standardNode) {
        $nodeCommand = Get-Item -LiteralPath $standardNode
    }
}
if (-not $nodeCommand) {
    throw "Node.js is required to verify the Eve two-runtime sample."
}
$nodePath = $nodeCommand.Source
$nodeDirectory = Split-Path -Parent $nodePath
$npmPath = if ($onWindows) {
    Join-Path $nodeDirectory "npm.cmd"
}
else {
    (Get-Command npm -ErrorAction Stop).Source
}
if (-not (Test-Path -LiteralPath $npmPath)) {
    throw "npm was not found beside Node.js: $npmPath"
}
$env:PATH = $nodeDirectory + [IO.Path]::PathSeparator + $env:PATH
if (-not (Test-Path -LiteralPath $samplePath)) {
    throw "Sample not found: $samplePath"
}
foreach ($packageRoot in $packageRoots) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot "package.json"))) {
        throw "Package root not found: $packageRoot"
    }
}

$tempBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$workRoot = Join-Path $tempBase ("gamecult-eve-two-runtime-" + [guid]::NewGuid().ToString("N"))
$artifactRoot = Join-Path $workRoot "artifacts"
$consumerRoot = Join-Path $workRoot "consumer"
New-Item -ItemType Directory -Path $artifactRoot, $consumerRoot -Force | Out-Null

try {
    foreach ($packageRoot in $packageRoots) {
        Push-Location $packageRoot
        try {
            if ((Split-Path -Leaf $packageRoot) -eq "cultnet-ts") {
                & $nodePath (Join-Path $packageRoot "tools\generate-swarm-contracts.mjs")
                if ($LASTEXITCODE -ne 0) {
                    throw "CultNet TypeScript contract generation failed: $packageRoot"
                }
            }
            $typescriptPath = if ($packageRoot -eq $eveBrowserPackage -or $packageRoot -eq $eveContractsPackage) {
                Join-Path $packageRoot "node_modules\typescript\bin\tsc"
            }
            else {
                Join-Path $cultLibRoot "node_modules\typescript\bin\tsc"
            }
            & $nodePath $typescriptPath -p (Join-Path $packageRoot "tsconfig.json") --pretty false
            if ($LASTEXITCODE -ne 0) {
                throw "Package build failed: $packageRoot"
            }
            & $npmPath pack --ignore-scripts --silent --pack-destination $artifactRoot | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Package pack failed: $packageRoot"
            }
        }
        finally {
            Pop-Location
        }
    }

    Push-Location $consumerRoot
    try {
        & $npmPath init --yes --silent | Out-Null
        $tarballs = Get-ChildItem -LiteralPath $artifactRoot -Filter "*.tgz" |
            Sort-Object Name |
            ForEach-Object FullName
        $installArguments = @(
            "install",
            "--silent",
            "--no-audit",
            "--no-fund",
            "@msgpack/msgpack@3",
            "ajv@8",
            "jsdom@30",
            "zod@3"
        ) + @($tarballs)
        & $npmPath @installArguments
        if ($LASTEXITCODE -ne 0) {
            throw "Clean consumer package installation failed."
        }
        Copy-Item -LiteralPath $samplePath -Destination (Join-Path $consumerRoot "sample.mjs")
        & $nodePath .\sample.mjs
        if ($LASTEXITCODE -ne 0) {
            throw "Eve two-runtime sample failed."
        }
    }
    finally {
        Pop-Location
    }
}
finally {
    $resolvedWorkRoot = [IO.Path]::GetFullPath($workRoot)
    if ($resolvedWorkRoot.StartsWith($tempBase, [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedWorkRoot)) {
        Remove-Item -LiteralPath $resolvedWorkRoot -Recurse -Force
    }
}

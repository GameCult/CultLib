# org.gamecult.caching.unity has no build step: it is pure C# editor source
# tagged directly from src/GameCult.Unity/Assets/Caching, unlike
# org.gamecult.cultlib and org.gamecult.cultmath which compile a DLL first.
# That means it has no existing script this repo's semver check can ride
# along with (docs/semver-policy.md, "Coverage gaps"). This script exists
# solely to be that gate: run it before tagging a caching-unity-v<version>
# release.

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$packageRoot = Join-Path $repoRoot "src\GameCult.Unity\Assets\Caching"
$manifestPath = Join-Path $packageRoot "package.json"

$version = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version

# The suite pins the checker's rules and the declared tag prefixes, so it runs first.
& node --test (Join-Path $repoRoot "scripts\check-changelog-semver.test.mjs")
if ($LASTEXITCODE -ne 0) {
  throw "The release check suite failed (scripts/check-changelog-semver.test.mjs); not releasing."
}

& node (Join-Path $repoRoot "scripts\check-changelog-semver.mjs") `
  --package "org.gamecult.caching.unity" `
  --version $version `
  --cwd $repoRoot
if ($LASTEXITCODE -ne 0) {
  throw "org.gamecult.caching.unity failed the semver policy check (see docs/semver-policy.md)."
}

Write-Host "org.gamecult.caching.unity $version cleared the semver policy check."

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
$changelogPath = Join-Path $packageRoot "CHANGELOG.md"

$version = (Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json).version

& node (Join-Path $repoRoot "scripts\check-changelog-semver.mjs") `
  --package "org.gamecult.caching.unity" `
  --changelog $changelogPath `
  --version $version `
  --tag-prefix "caching-unity" `
  --cwd $repoRoot
if ($LASTEXITCODE -ne 0) {
  throw "org.gamecult.caching.unity failed the semver policy check (see docs/semver-policy.md)."
}

Write-Host "org.gamecult.caching.unity $version cleared the semver policy check."

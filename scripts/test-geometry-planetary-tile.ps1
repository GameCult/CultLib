$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$fixture = Join-Path $root ".tools\planetary-tile\fixture.cmpt"
dotnet run --project (Join-Path $root "tools\GameCult.Geometry.PlanetaryTileFixture\GameCult.Geometry.PlanetaryTileFixture.csproj") -- $fixture
if ($LASTEXITCODE -ne 0) { throw "Planetary tile fixture generation failed." }
node --no-warnings --experimental-strip-types (Join-Path $root "packages\gamecult-geometry-ts\test\planetary-tile.cross-runtime.test.mjs") $fixture
if ($LASTEXITCODE -ne 0) { throw "Planetary tile web decoding failed." }
node --no-warnings --experimental-strip-types (Join-Path $root "packages\gamecult-geometry-ts\test\planetary-tile.test.mjs")
if ($LASTEXITCODE -ne 0) { throw "Planetary tile decoder unit test failed." }

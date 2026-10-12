# Cycles oracle

`oracle.py` produces the fixtures that `CyclesOracleTests` holds `math.cycles_*` to: for each Cycles node
CultMath ports, the node's results as Blender's own Cycles computes them.

## Run

```
CYCLES_CPU_NO_AVX2=1 blender --background --factory-startup --python oracle.py -- <fixture directory> [<family> ...]
```

The fixture directory is `tests/CultMath.Tests/fixtures/cycles-oracle`. Families are `math`, `map-range`,
`clamp`, `mix`, `rgb-to-bw` and `ramp`; with none named, all are written. The tool is a Blender Python
script and runs only under Blender, never under a plain Python.

## The pin

It refuses to run under any Blender but **5.2.2 LTS, build hash `d13f752e3b9c`**, exits nonzero and names
the version and build it found beside the ones it needs. Blender 5.2.2 is
<https://download.blender.org/release/Blender5.2/> (`blender-5.2.2-linux-x64.tar.xz`, or the Windows build
from Steam). The fixtures record the version, the hash and the platform they were written on.

## The CPU kernel

Cycles' AVX2 CPU kernel is compiled with fused multiply-add, so an expression such as `a - floor(a / b) b` is
one fused operation there, and CultMath's C# (which fuses nothing) computes the unfused one. The fixtures pin
the SSE4.2 kernel, which has no FMA, by `CYCLES_CPU_NO_AVX2=1`; the tool bakes `10000 mod 0.1` before anything
else and exits 4 if the result is the fused one, so a run on the wrong kernel writes nothing.

## Method

Operands go into float, Non-Color input images (channel packed, read with Closest interpolation), three
operands per image. The node under test reads them. One Emit bake per output channel (Cycles on the CPU, 1
sample, margin 0, no denoising) writes the result to a float image; each pixel is one case. The tool first
checks that a bake returns an operand image bit for bit, and stops if it does not. The fixtures hold the
operands and the results as float32 bit patterns, so a test reads exactly what Cycles computed.

A Map Range node's Clamp option is baked as the node with Clamp on, which Cycles expands into a Clamp node
of type Range on the result. The ramp family bakes a Color Ramp whose table entries are each read at a `t`
whose product with 255 is the entry's index, so the first 256 cases are the ramp's own table.

## Fixtures

Written by this tool and never edited by hand. Rerunning it under the same Blender build on the same
platform rewrites them byte for byte; another platform's libm can differ in the transcendental families
(`power`, `logarithm`), which is why `CyclesOracleTests` states a tolerance for them.

## Licence

`oracle.py` is GPL-3.0-or-later, as a script that runs inside Blender (`bpy`). It ships in no package: the
`GameCult.Math` NuGet package and the Unity package do not contain `tools/`. The fixtures are data it
produced. The Cycles functions they test are Apache-2.0 (`math.Cycles.cs`, `CultMath.Cycles.hlsl`).

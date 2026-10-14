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
<https://download.blender.org/release/Blender5.2/> (`blender-5.2.2-linux-x64.tar.xz`). The platform is Linux x64: the fixtures reproduce
byte for byte only there, and the Steam/Windows build is another build (see below). The fixtures record the
version, the hash and the platform they were written on, as provenance.

## The reference and other builds

Ruling `cultmath-tapes:ruling:cycles-kernel-reference-ruled`: CultMath's Cycles converters mean Cycles' formulas
evaluated with IEEE single rounding per operation, and the Linux SSE4.2 oracle stays their bit-exact witness.
Every other Cycles build (AVX2 FMA-contracted, MSVC /fp:fast, GPU backends) and CultMath's own HLSL lowering are
held to a stated per-function tolerance class measured on that build; cancellation-prone functions (floored
modulo, wrap) get an error bound relative to |b| and a documented divergence note.

Cycles' AVX2 CPU kernel is compiled with fused multiply-add, so `a - floor(a / b) b` is one fused operation
there, and CultMath's C# (which fuses nothing) computes the unfused one. The fixtures pin the SSE4.2 kernel,
which has no FMA, by `CYCLES_CPU_NO_AVX2=1`; the tool bakes `10000 mod 0.1` before anything else and exits 4 if
the result is the fused one, so a run on the wrong kernel writes nothing. Other kernels are other builds:
measured under the table below, never written to these fixtures.

| build | how it was measured | fixture | per-function ulp or bound |
| --- | --- | --- | --- |
| Blender 5.2.2 Linux x64, SSE4.2 kernel (the reference) | `oracle.py` with `CYCLES_CPU_NO_AVX2=1`; `CyclesOracleTests` holds every function to the fixtures | `tests/CultMath.Tests/fixtures/cycles-oracle/*.json` | 0 ulp, every function |
| Blender 5.2.2 Linux x64, AVX2 kernel | the same oracle without the pin, 1024 rows per function, against the single-rounding model `fma(-floor(a / b), b, a)` (wrap: `fma(-range, floor((value - min) / range), value)`) | none committed | floored modulo: at most 2^-23 (\|a / b\| + 1) \|b\|, measured 0.91 of 2^-24 (\|a\| + \|b\|) on 151 rows; wrap: at most 2^-23 (\|value\| + \|min\| + \|range\|), measured 0.77 of 2^-24 on 30 rows; other functions unmeasured |

The divergence note: a build compiled with fast-math may also move `floor(a / b)` by one near an integer
quotient, so the result differs by a whole \|b\| (wrap: a whole range). Further rows come from follow-up
`cultmath-tapes:follow_up:cycles-tolerance-classes`; its per-build fixtures go under
`fixtures/cycles-oracle/<build>/` with their tolerances beside `Tolerances` in `CyclesOracleTests`.

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
platform rewrites them byte for byte; `CyclesOracleTests` holds every function to them at 0 ulp. Run the
oracle on Yggdrasil-class Linux, never on the workstation.

## Licence

`oracle.py` is GPL-3.0-or-later, as a script that runs inside Blender (`bpy`). It ships in no package: the
`GameCult.Math` NuGet package and the Unity package do not contain `tools/`. The fixtures are data it
produced. The Cycles functions they test are Apache-2.0 (`math.Cycles.cs`, `CultMath.Cycles.hlsl`).

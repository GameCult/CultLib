# GameCult.Geometry source provenance

## CultMath planetary geometry

The C# files under `Planetary/` and the HLSL files under `Shaders/` are licensed
under the Mozilla Public License 2.0 and retain MPL-2.0 notices. CultMath owns
the numeric primitives used by these geometry systems; GameCult.Geometry owns
their spatial meaning.

## Advanced Terrain Erosion Filter

`Planetary/AdvancedErosionFilter.cs` and `Shaders/AdvancedErosionFilter.hlsl`
adapt the mathematical procedure from
Rune Skovbo Johansen's **Advanced Terrain Erosion Filter**.

- Copyright 2025 Rune Skovbo Johansen.
- Upstream: <https://www.shadertoy.com/view/wXcfWn>
- Explanation: <https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html>
- License: Mozilla Public License 2.0, <https://www.mozilla.org/MPL/2.0/>

The implementation includes C#/HLSL type lowering, bounded octave counts,
finite zero-rounding behavior, and a shared integer avalanche hash for CPU/GPU
cell-selection parity. The technique builds on earlier work credited upstream
to Fewes and Clay John; the hash form follows public shader math by Inigo Quilez.

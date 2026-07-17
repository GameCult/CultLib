# Third-Party Notices

## 2D Simplex Noise

`math.simplex_noise` and `cultmath_simplex_noise` port the textureless 2D
simplex-noise function by Ian McEwan and Ashima Arts, as distributed by Stefan
Gustavson's `webgl-noise` project and Unity Mathematics.

- Copyright 2011 Ashima Arts.
- Upstream: <https://github.com/ashima/webgl-noise>
- License: MIT.

## Advanced Terrain Erosion Filter

`src/CultMath/AdvancedErosionFilter.cs` and
`shaders/AdvancedErosionFilter.hlsl` adapt the mathematical procedure from
Rune Skovbo Johansen's **Advanced Terrain Erosion Filter**:

- Copyright 2025 Rune Skovbo Johansen.
- Upstream: <https://www.shadertoy.com/view/wXcfWn>
- Explanation: <https://blog.runevision.com/2026/03/fast-and-gorgeous-erosion-filter.html>
- License: Mozilla Public License 2.0,
  <https://www.mozilla.org/MPL/2.0/>

The CultMath files retain the MPL notice and separate the faithful planar
filter from CultMath's spherical adaptation. Modifications include C#/HLSL
type lowering, explicit bounded octave counts, finite zero-rounding behavior,
and CultMath naming. CultMath replaces the reference floating hash with a
shared integer avalanche hash so C# and GPU evaluation select identical cell
pivots across devices; this changes the stochastic pattern, not the filter
mechanics.

The technique builds on earlier work credited by the upstream author to Fewes
and Clay John. The hash form follows the public shader math of Inigo Quilez.

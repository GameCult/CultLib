# Planetary Field Viewer

Import this sample through Unity's Package Manager, then choose
`GameObject > CultMath > Planetary Field Viewer`.

The host project must contain `Assets/csc.rsp` with
`-langversion:latest`, as documented in the CultMath package README.

The generated six-face mesh uses `PlanetaryPatch`. Its shader calls
`cultmath_planetary_field_sample` from the packaged CultMath HLSL, while the
component exposes the matching CPU `Sample` method. Seed, radius, physical
sample footprint, erosion settings, and base-field equations are shared between
the two bodies.

This is a renderer integration fixture, not a gameplay owner. It deliberately
contains no Aetheria state, biomes, resources, or simulation decisions.

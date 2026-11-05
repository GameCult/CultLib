# sprite-silhouette

Turns a sprite's alpha channel into a simplified polygon in percent coordinates, for CSS
`shape-outside: polygon()`, so text wraps around the figure instead of its rectangle. Zero
dependencies: Node 18+ (`node:zlib` does the PNG inflate). Entry point: `silhouette.mjs`.
Live and tested; there is no packaging or release for it, it is a repo tool.

## First call

```
node tools/sprite-silhouette/silhouette.mjs /opt/commons/cast-art/poses --out silhouettes.json
node --test tools/sprite-silhouette/silhouette.test.mjs
```

The first argument is a poses directory holding `catalogue.json` (the cast-art catalogue); the
tool reads it and every PNG it names, and writes nothing there. Output is a JSON object keyed by
pose id, `mind/label` in catalogue order (`void/neutral`, `eureka/probe`, ...):

```json
{ "void/neutral": { "width": 577, "height": 579,
    "points": [[42.98, 6.72], [49.59, 8.24], "..."],
    "css": "polygon(42.98% 6.72%, 49.59% 8.24%, ...)" } }
```

Use the `css` string as the value of `shape-outside` on a floated image of the same aspect ratio
(percentages are of the image's box):

```css
img.pose { float: left; shape-outside: polygon(42.98% 6.72%, 49.59% 8.24%, ...); shape-margin: 0.5em; }
```

An empty sprite (nothing at or above the threshold) gets `points: []` and `css: "none"`.

From code: `silhouette(pngBuffer, { threshold, tolerance })` in `silhouette.mjs`.

## Options

- `--threshold 0.35`: a pixel is inside when `alpha/255 >= threshold` (0.35 is the Void mock-up's value).
- `--tolerance 1.5`: Douglas-Peucker distance in source pixels; the simplified polygon stays within
  this of the traced edge in both directions (tested). 0 keeps every corner.
- `--out file`: write there instead of stdout.

## What it does and does not trace

Threshold, then the outer boundary of the largest 8-connected shape, by walking the pixel edges
(so vertices sit on pixel corners and the walk is exact), then simplification of that closed ring.
Holes are ignored (wrapping text goes around a figure, not through its gaps), and smaller
disconnected islands (a floating hand, a stray sparkle) are dropped: `shape-outside` takes one
polygon. The contour is a staircase of pixel edges, so below about 1 px the tolerance only
reproduces the staircase; there is no sub-pixel interpolation of the alpha ramp.

Input: 8-bit non-interlaced PNG (RGBA, grey+alpha, palette with tRNS, or opaque RGB/grey, which is
a rectangle). Anything else is refused with its name.

## Prior art

- **Marching squares** (Lorensen and Cline's cubes, 2D case) interpolates along the alpha ramp for
  sub-pixel contours and one pass emits every contour, holes included. Not used: the pose art is
  anti-aliased over a pixel or two, a 1.5 px tolerance swallows that, and we want exactly one outer ring.
- **Potrace** (Selinger, 2003) fits optimal polygons and then Bezier curves to a bitmap. CSS
  `polygon()` has no curves, so only its idea of an optimal polygon applies; this tool keeps the
  simpler greedy Douglas-Peucker and measures its error directly.
- **Douglas-Peucker** (1973; Ramer 1972) is the simplifier: keep the vertex farthest from the chord
  if it is beyond the tolerance, recurse. Its guarantee (every dropped vertex within the tolerance
  of the result) is what the tests assert. It is not topology-preserving; on all 99 current poses at
  the defaults no output self-intersects (checked when this was written, not asserted by the suite).
- **Boundary following** (Moore-neighbour / square tracing) is the contour walk, with the
  8-connected rule so diagonally touching pixels stay one shape.

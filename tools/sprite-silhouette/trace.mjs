// Silhouette of an alpha plane: threshold -> outer contour of the largest 8-connected shape -> Douglas-Peucker.
// Coordinates are pixel-corner lattice points: x in 0..width, y in 0..height.

/** A pixel is inside when alpha/255 >= threshold. */
export function mask(alpha, threshold) {
  const m = new Uint8Array(alpha.length);
  for (let i = 0; i < alpha.length; i++) m[i] = alpha[i] / 255 >= threshold ? 1 : 0;
  return m;
}

/** Outer contour (lattice vertices at turns, clockwise on screen) of the 8-connected component with the most pixels, or null. */
export function largestContour(m, width, height) {
  const seen = new Uint8Array(m.length);
  let best = null;
  for (let start = 0; start < m.length; start++) {
    if (!m[start] || seen[start]) continue;
    // Raster order: `start` is the component's first pixel, so the pixels above and up-left of it are empty
    // or belong to another component.
    let count = 0;
    const stack = [start];
    seen[start] = 1;
    while (stack.length) {
      const p = stack.pop();
      count++;
      const px = p % width, py = (p / width) | 0;
      for (let dy = -1; dy <= 1; dy++) for (let dx = -1; dx <= 1; dx++) {
        const x = px + dx, y = py + dy;
        if (x < 0 || y < 0 || x >= width || y >= height) continue;
        const q = y * width + x;
        if (m[q] && !seen[q]) { seen[q] = 1; stack.push(q); }
      }
    }
    if (!best || count > best.count) best = { count, start };
  }
  return best && traceOuter(m, width, height, best.start % width, (best.start / width) | 0);
}

// Directions: 0 east, 1 south, 2 west, 3 north. The shape is kept on the walker's right, so an
// outer boundary comes out clockwise; at each corner a filled left-ahead pixel turns left (which
// joins pixels that only touch diagonally), else a filled right-ahead pixel goes straight, else right.
const DX = [1, 0, -1, 0], DY = [0, 1, 0, -1];
const AHEAD_LEFT = [[0, -1], [0, 0], [-1, 0], [-1, -1]];
const AHEAD_RIGHT = [[0, 0], [-1, 0], [-1, -1], [0, -1]];

function traceOuter(m, width, height, px, py) {
  const at = (x, y) => x >= 0 && y >= 0 && x < width && y < height && m[y * width + x] === 1;
  const vertices = [];
  let x = px, y = py, dir = 3, first = true; // arrive at the start corner heading north, so it is counted as a turn
  for (;;) {
    const left = at(x + AHEAD_LEFT[dir][0], y + AHEAD_LEFT[dir][1]);
    const right = at(x + AHEAD_RIGHT[dir][0], y + AHEAD_RIGHT[dir][1]);
    const turn = left ? 3 : right ? 0 : 1;
    const next = (dir + turn) & 3;
    // Back at the start corner and about to leave east: the ring is closed.
    if (!first && x === px && y === py && next === 0) break;
    first = false;
    if (turn !== 0) vertices.push([x, y]);
    dir = next;
    x += DX[dir]; y += DY[dir];
  }
  return vertices;
}

/** Douglas-Peucker on a closed ring: split at the vertex farthest from the first, simplify both halves. */
export function simplifyRing(ring, tolerance) {
  if (ring.length <= 3 || tolerance <= 0) return ring.slice();
  let far = 0, best = -1;
  for (let i = 1; i < ring.length; i++) {
    const d = (ring[i][0] - ring[0][0]) ** 2 + (ring[i][1] - ring[0][1]) ** 2;
    if (d > best) { best = d; far = i; }
  }
  const a = dp(ring.slice(0, far + 1), tolerance);
  const b = dp(ring.slice(far).concat([ring[0]]), tolerance);
  return a.slice(0, -1).concat(b.slice(0, -1));
}

function dp(points, tolerance) {
  const keep = new Uint8Array(points.length);
  keep[0] = keep[points.length - 1] = 1;
  const stack = [[0, points.length - 1]];
  while (stack.length) {
    const [lo, hi] = stack.pop();
    let worst = -1, at = -1;
    for (let i = lo + 1; i < hi; i++) {
      const d = distanceToSegment(points[i], points[lo], points[hi]);
      if (d > worst) { worst = d; at = i; }
    }
    if (worst > tolerance) { keep[at] = 1; stack.push([lo, at], [at, hi]); }
  }
  return points.filter((_, i) => keep[i]);
}

export function distanceToSegment(p, a, b) {
  const vx = b[0] - a[0], vy = b[1] - a[1];
  const len2 = vx * vx + vy * vy;
  const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((p[0] - a[0]) * vx + (p[1] - a[1]) * vy) / len2));
  return Math.hypot(p[0] - (a[0] + t * vx), p[1] - (a[1] + t * vy));
}

/** Largest distance from any point of ring `a`'s edges' vertices to ring `b`'s edges. */
export function maxDistanceToRing(points, ring) {
  let worst = 0;
  for (const p of points) {
    let d = Infinity;
    for (let i = 0; i < ring.length; i++) d = Math.min(d, distanceToSegment(p, ring[i], ring[(i + 1) % ring.length]));
    worst = Math.max(worst, d);
  }
  return worst;
}

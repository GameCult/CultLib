#!/usr/bin/env node
// Sprite alpha silhouette -> simplified polygon in percent coordinates for CSS shape-outside: polygon().
// First call: node tools/sprite-silhouette/silhouette.mjs /opt/commons/cast-art/poses > silhouettes.json
import { readFileSync, writeFileSync } from "node:fs";
import { basename, join } from "node:path";
import { pathToFileURL } from "node:url";
import { decodeAlpha } from "./png.mjs";
import { largestContour, mask, simplifyRing } from "./trace.mjs";

export const DEFAULTS = { threshold: 0.35, tolerance: 1.5 };

const pct = (v, total) => Math.round((v / total) * 10000) / 100;

/** @returns {{ width: number, height: number, points: number[][], css: string }} points are [x%, y%]; css is "none" for an empty sprite. */
export function silhouette(png, { threshold = DEFAULTS.threshold, tolerance = DEFAULTS.tolerance } = {}) {
  const { width, height, alpha } = decodeAlpha(png);
  const ring = largestContour(mask(alpha, threshold), width, height);
  if (!ring) return { width, height, points: [], css: "none" };
  const points = simplifyRing(ring, tolerance).map(([x, y]) => [pct(x, width), pct(y, height)]);
  const css = `polygon(${points.map(([x, y]) => `${x}% ${y}%`).join(", ")})`;
  return { width, height, points, css };
}

/** Pose ids are `mind/label`, in catalogue order; the value is the path under the poses root. */
export function poseFiles(catalogue) {
  const files = {};
  for (const [mind, entry] of Object.entries(catalogue.minds)) {
    for (const group of [entry.shared, entry.signatures]) {
      for (const [label, path] of Object.entries(group ?? {})) files[`${mind}/${label}`] = path;
    }
  }
  return files;
}

export function silhouettes(root, options) {
  const catalogue = JSON.parse(readFileSync(join(root, "catalogue.json"), "utf8"));
  const out = {};
  for (const [id, path] of Object.entries(poseFiles(catalogue))) out[id] = silhouette(readFileSync(join(root, path)), options);
  return out;
}

function main(argv) {
  const options = { ...DEFAULTS };
  let out = null, root = null;
  for (let i = 0; i < argv.length; i++) {
    if (argv[i] === "--threshold") options.threshold = Number(argv[++i]);
    else if (argv[i] === "--tolerance") options.tolerance = Number(argv[++i]);
    else if (argv[i] === "--out") out = argv[++i];
    else if (argv[i].startsWith("--")) throw new Error(`unknown option ${argv[i]}`);
    else root = argv[i];
  }
  if (!root || !(options.threshold > 0 && options.threshold <= 1) || !(options.tolerance >= 0)) {
    console.error("usage: silhouette.mjs <poses-dir> [--threshold 0.35] [--tolerance 1.5] [--out file.json]");
    process.exit(2);
  }
  const text = JSON.stringify(silhouettes(root, options), null, 1) + "\n";
  if (out) writeFileSync(out, text);
  else process.stdout.write(text);
}

if (import.meta.url === pathToFileURL(process.argv[1] ?? "").href) main(process.argv.slice(2));

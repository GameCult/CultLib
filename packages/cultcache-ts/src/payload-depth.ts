// The one payload nesting rule in TypeScript, the twin of the C# reference's CultPayloadDepth.Check: every MessagePack
// array and map counts, whether a reader reads it or skips past it, and the outermost array is depth 1. Depth
// MAX_PAYLOAD_DEPTH is accepted and one container deeper is refused, on read and on write.
export const MAX_PAYLOAD_DEPTH = 256;

export class CultCachePayloadDepthError extends Error {
  constructor(readonly depth: number, readonly limit: number, subject: string) {
    super(`${subject} nests ${depth} MessagePack containers deep; the limit is ${limit}.`);
    this.name = "CultCachePayloadDepthError";
  }
}

// Walks the headers of one MessagePack value iteratively (a count stack bounded by the limit, scalars skipped by length,
// nothing read) and throws at the first container past the limit. The message names the subject, the depth and the limit,
// never payload bytes. A payload that ends early is the reader's to refuse.
export function checkPayloadDepth(bytes: Uint8Array, subject = "document payload"): void {
  const view = new DataView(bytes.buffer, bytes.byteOffset, bytes.byteLength);
  const remaining = new Float64Array(MAX_PAYLOAD_DEPTH);
  let depth = 0;
  let position = 0;
  // The big-endian length that follows the code byte, when the payload is long enough to hold it.
  const width = (size: 1 | 2 | 4): number | undefined => {
    if (position + 1 + size > bytes.length) return undefined;
    return size === 1 ? view.getUint8(position + 1) : size === 2 ? view.getUint16(position + 1) : view.getUint32(position + 1);
  };
  while (position < bytes.length) {
    const code = bytes[position];
    let header = 0;
    let children = 0;
    let skip = 0;
    let container = false;
    if (code <= 0x7f || code >= 0xe0 || code === 0xc0 || code === 0xc1 || code === 0xc2 || code === 0xc3) skip = 1;
    else if (code <= 0x8f) { container = true; header = 1; children = 2 * (code & 0x0f); }
    else if (code <= 0x9f) { container = true; header = 1; children = code & 0x0f; }
    else if (code <= 0xbf) skip = 1 + (code & 0x1f);
    else if (code === 0xc4 || code === 0xd9) { const n = width(1); if (n === undefined) return; skip = 2 + n; }
    else if (code === 0xc5 || code === 0xda) { const n = width(2); if (n === undefined) return; skip = 3 + n; }
    else if (code === 0xc6 || code === 0xdb) { const n = width(4); if (n === undefined) return; skip = 5 + n; }
    else if (code === 0xc7) { const n = width(1); if (n === undefined) return; skip = 3 + n; }
    else if (code === 0xc8) { const n = width(2); if (n === undefined) return; skip = 4 + n; }
    else if (code === 0xc9) { const n = width(4); if (n === undefined) return; skip = 6 + n; }
    else if (code === 0xca || code === 0xce || code === 0xd2) skip = 5;
    else if (code === 0xcb || code === 0xcf || code === 0xd3) skip = 9;
    else if (code === 0xcc || code === 0xd0) skip = 2;
    else if (code === 0xcd || code === 0xd1 || code === 0xd4) skip = 3;
    else if (code === 0xd5) skip = 4;
    else if (code === 0xd6) skip = 6;
    else if (code === 0xd7) skip = 10;
    else if (code === 0xd8) skip = 18;
    else if (code === 0xdc) { const n = width(2); if (n === undefined) return; container = true; header = 3; children = n; }
    else if (code === 0xdd) { const n = width(4); if (n === undefined) return; container = true; header = 5; children = n; }
    else if (code === 0xde) { const n = width(2); if (n === undefined) return; container = true; header = 3; children = 2 * n; }
    else { const n = width(4); if (n === undefined) return; container = true; header = 5; children = 2 * n; }

    if (container) {
      if (depth === MAX_PAYLOAD_DEPTH) throw new CultCachePayloadDepthError(MAX_PAYLOAD_DEPTH + 1, MAX_PAYLOAD_DEPTH, subject);
      position += header;
      if (children > 0) {
        remaining[depth++] = children;
        continue;
      }
    } else {
      position += skip;
    }

    // One element is complete; close every container it was the last element of.
    while (depth > 0 && --remaining[depth - 1] === 0) depth--;
    if (depth === 0) return;
  }
}

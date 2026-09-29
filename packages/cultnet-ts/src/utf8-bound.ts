/**
 * Longest prefix of `value` whose UTF-8 encoding fits in `maxBytes`, cut on a
 * code point boundary. Rust owners (`cultnet-rs`) bound strings by UTF-8 bytes,
 * not UTF-16 units, so every TypeScript publisher bounds through this one path.
 *
 * Lone surrogates are replaced with U+FFFD first: they have no UTF-8 encoding,
 * and a msgpack writer would otherwise emit invalid bytes that Rust refuses.
 * A value is therefore in bounds exactly when this returns it unchanged.
 */
export function truncateUtf8Bytes(value: string, maxBytes: number): string {
  const wellFormed = (value as string & { toWellFormed(): string }).toWellFormed();
  let bytes = 0;
  let end = 0;
  for (const char of wellFormed) {
    bytes += Buffer.byteLength(char, "utf8");
    if (bytes > maxBytes) break;
    end += char.length;
  }
  return wellFormed.slice(0, end);
}

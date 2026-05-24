const FRAME_HEADER_BYTES = 4;

export function encodeFrame(payload: Uint8Array): Uint8Array {
  const frame = new Uint8Array(FRAME_HEADER_BYTES + payload.length);
  new DataView(frame.buffer, frame.byteOffset, FRAME_HEADER_BYTES)
    .setUint32(0, payload.length, false);
  frame.set(payload, FRAME_HEADER_BYTES);
  return frame;
}

export class LengthPrefixedMessageFramer {
  #buffer: Uint8Array<ArrayBufferLike> = new Uint8Array(0);

  push(chunk: Uint8Array): Uint8Array[] {
    this.#buffer = concatBytes(this.#buffer, chunk);
    const frames: Uint8Array[] = [];

    while (this.#buffer.length >= FRAME_HEADER_BYTES) {
      const payloadLength = new DataView(
        this.#buffer.buffer,
        this.#buffer.byteOffset,
        FRAME_HEADER_BYTES,
      ).getUint32(0, false);
      const totalLength = FRAME_HEADER_BYTES + payloadLength;

      if (this.#buffer.length < totalLength) {
        break;
      }

      frames.push(this.#buffer.subarray(FRAME_HEADER_BYTES, totalLength));
      this.#buffer = this.#buffer.subarray(totalLength);
    }

    return frames;
  }
}

function concatBytes(left: Uint8Array, right: Uint8Array): Uint8Array<ArrayBufferLike> {
  const combined = new Uint8Array(left.length + right.length);
  combined.set(left, 0);
  combined.set(right, left.length);
  return combined;
}

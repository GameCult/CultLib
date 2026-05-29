const FRAME_HEADER_BYTES = 4;

export function encodeFrame(payload: Uint8Array): Buffer {
  const header = Buffer.allocUnsafe(FRAME_HEADER_BYTES);
  header.writeUInt32BE(payload.length, 0);
  return Buffer.concat([header, Buffer.from(payload)]);
}

export class LengthPrefixedMessageFramer {
  #buffer = Buffer.alloc(0);

  push(chunk: Uint8Array): Uint8Array[] {
    this.#buffer = Buffer.concat([this.#buffer, Buffer.from(chunk)]);
    const frames: Uint8Array[] = [];

    while (this.#buffer.length >= FRAME_HEADER_BYTES) {
      const payloadLength = this.#buffer.readUInt32BE(0);
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

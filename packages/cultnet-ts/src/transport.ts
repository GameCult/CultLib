export interface CultNetTransport {
  send(bytes: Uint8Array): void;
  close(): void;
  onBytes(handler: (bytes: Uint8Array) => void): void;
  onClose(handler: () => void): void;
  onError(handler: (error: Error) => void): void;
}

export interface CultNetLegacyDuplexLike {
  write(chunk: Uint8Array): unknown;
  end(): unknown;
  on(event: "data", handler: (chunk: Uint8Array) => void): unknown;
  on(event: "close", handler: () => void): unknown;
  on(event: "error", handler: (error: unknown) => void): unknown;
}

export interface CultNetWebSocketLike {
  binaryType?: BinaryType;
  send(data: ArrayBuffer | ArrayBufferView): void;
  close(): void;
  addEventListener(
    type: "message",
    handler: (event: MessageEvent<ArrayBuffer | Blob | Uint8Array>) => void,
  ): void;
  addEventListener(type: "close", handler: () => void): void;
  addEventListener(type: "error", handler: (event: Event) => void): void;
}

export function createCultNetDuplexTransport(stream: CultNetLegacyDuplexLike): CultNetTransport {
  return {
    send(bytes) {
      stream.write(bytes);
    },
    close() {
      stream.end();
    },
    onBytes(handler) {
      stream.on("data", (chunk) => handler(toUint8Array(chunk)));
    },
    onClose(handler) {
      stream.on("close", handler);
    },
    onError(handler) {
      stream.on("error", (error) => handler(toError(error)));
    },
  };
}

export function createCultNetWebSocketTransport(socket: CultNetWebSocketLike): CultNetTransport {
  socket.binaryType = "arraybuffer";
  return {
    send(bytes) {
      socket.send(bytes);
    },
    close() {
      socket.close();
    },
    onBytes(handler) {
      socket.addEventListener("message", (event) => {
        const data = event.data;
        if (data instanceof ArrayBuffer) {
          handler(new Uint8Array(data));
          return;
        }
        if (ArrayBuffer.isView(data)) {
          handler(new Uint8Array(data.buffer, data.byteOffset, data.byteLength));
          return;
        }
        void data.arrayBuffer().then((buffer) => handler(new Uint8Array(buffer)));
      });
    },
    onClose(handler) {
      socket.addEventListener("close", handler);
    },
    onError(handler) {
      socket.addEventListener("error", (event) => {
        handler(new Error(`CultNet WebSocket transport error: ${event.type}`));
      });
    },
  };
}

export function isCultNetTransport(value: unknown): value is CultNetTransport {
  return typeof value === "object" &&
    value !== null &&
    typeof (value as CultNetTransport).send === "function" &&
    typeof (value as CultNetTransport).close === "function" &&
    typeof (value as CultNetTransport).onBytes === "function" &&
    typeof (value as CultNetTransport).onClose === "function" &&
    typeof (value as CultNetTransport).onError === "function";
}

export function toError(error: unknown): Error {
  return error instanceof Error ? error : new Error(String(error));
}

function toUint8Array(value: Uint8Array): Uint8Array {
  return new Uint8Array(value.buffer, value.byteOffset, value.byteLength);
}

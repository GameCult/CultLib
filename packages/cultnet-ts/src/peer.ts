import { EventEmitter } from "node:events";
import type { Duplex } from "node:stream";

import { decode, encode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetDocumentDeleteMessage, type CultNetDocumentPutMessage, type CultNetErrorMessage, type CultNetHelloMessage, type CultNetLoginMessage, type CultNetLoginSuccessMessage, type CultNetMessage, type CultNetRegisterMessage, type CultNetSampleChangeNameMessage, type CultNetSampleChatMessage, type CultNetSchemaCatalogRequestMessage, type CultNetSchemaCatalogResponseMessage, type CultNetSnapshotRequestMessage, type CultNetSnapshotResponseMessage, type CultNetTransportProfile, type CultNetVerifyMessage, type CultNetWireContract } from "./contracts";
import { encodeFrame, LengthPrefixedMessageFramer } from "./framing";
import { createTcpFramedTransportProfile, TcpFramedTransportConnection, type CultNetTransportConnection, type TcpFramedTransportProfileOptions } from "./transport";

export interface CultNetPeerEvents {
  message: (message: CultNetMessage) => void;
  invalidMessage: (error: Error) => void;
  close: () => void;
  error: (error: Error) => void;
}

export interface CultNetPeerOptions {
  wireContract: CultNetWireContract;
}

export interface TcpFramedCultNetPeerOptions extends CultNetPeerOptions, TcpFramedTransportProfileOptions {
  runtimeId: string;
}

export function createTcpFramedCultNetPeer(
  stream: Duplex,
  options: TcpFramedCultNetPeerOptions,
): CultNetPeer {
  const {
    runtimeId,
    wireContract,
    transportId,
    host,
    port,
    maxPayloadBytes,
    maxFragmentBytes,
  } = options;
  const transport = new TcpFramedTransportConnection(
    stream,
    createTcpFramedTransportProfile(runtimeId, {
      transportId,
      host,
      port,
      maxPayloadBytes,
      maxFragmentBytes,
    }),
  );
  return new CultNetPeer(transport, { wireContract });
}

export class CultNetPeer extends EventEmitter {
  readonly #stream?: Duplex;
  readonly #transport?: CultNetTransportConnection;
  readonly #framer?: LengthPrefixedMessageFramer;
  readonly #wireContract: CultNetWireContract;

  constructor(stream: Duplex | CultNetTransportConnection, options: CultNetPeerOptions) {
    super();
    if (!options?.wireContract) {
      throw new Error("CultNetPeer requires an explicit wireContract.");
    }

    this.#wireContract = options.wireContract;
    if (isCultNetTransportConnection(stream)) {
      this.#transport = stream;
      this.#transport.on("frame", (frame) => this.#handlePayload(frame.payload));
      this.#transport.on("close", () => this.emit("close"));
      this.#transport.on("error", (error) => this.emit("error", error));
    } else {
      this.#stream = stream;
      this.#framer = new LengthPrefixedMessageFramer();
      this.#stream.on("data", (chunk: Buffer) => {
        for (const frame of this.#framer?.push(chunk) ?? []) {
          this.#handlePayload(frame);
        }
      });
      this.#stream.on("close", () => this.emit("close"));
      this.#stream.on("error", (error) => this.emit("error", error instanceof Error ? error : new Error(String(error))));
    }
  }

  get transportProfile(): CultNetTransportProfile | undefined {
    return this.#transport?.profile;
  }

  send(message: CultNetMessage): void {
    const wireValue = encodeCultNetMessageForWire(message, this.#wireContract);
    const payload = encode(wireValue);
    if (this.#transport) {
      this.#transport.send("schema", payload);
      return;
    }

    this.#stream?.write(encodeFrame(payload));
  }

  sendHello(message: CultNetHelloMessage): void {
    this.send(message);
  }

  sendLogin(message: CultNetLoginMessage): void {
    this.send(message);
  }

  sendRegister(message: CultNetRegisterMessage): void {
    this.send(message);
  }

  sendVerify(message: CultNetVerifyMessage): void {
    this.send(message);
  }

  sendLoginSuccess(message: CultNetLoginSuccessMessage): void {
    this.send(message);
  }

  sendError(message: CultNetErrorMessage): void {
    this.send(message);
  }

  sendSampleChangeName(message: CultNetSampleChangeNameMessage): void {
    this.send(message);
  }

  sendSampleChat(message: CultNetSampleChatMessage): void {
    this.send(message);
  }

  sendDocumentPut(message: CultNetDocumentPutMessage): void {
    this.send(message);
  }

  sendDocumentDelete(message: CultNetDocumentDeleteMessage): void {
    this.send(message);
  }

  sendSnapshotRequest(message: CultNetSnapshotRequestMessage): void {
    this.send(message);
  }

  sendSnapshotResponse(message: CultNetSnapshotResponseMessage): void {
    this.send(message);
  }

  sendSchemaCatalogRequest(message: CultNetSchemaCatalogRequestMessage): void {
    this.send(message);
  }

  sendSchemaCatalogResponse(message: CultNetSchemaCatalogResponseMessage): void {
    this.send(message);
  }

  close(): void {
    this.#transport?.close();
    this.#stream?.end();
  }

  #handlePayload(payload: Uint8Array): void {
    try {
      const decoded = decode(payload);
      const message = parseCultNetMessage(decoded, this.#wireContract);
      this.emit("message", message);
    } catch (error) {
      this.emit("invalidMessage", error instanceof Error ? error : new Error(String(error)));
    }
  }
}

function isCultNetTransportConnection(value: Duplex | CultNetTransportConnection): value is CultNetTransportConnection {
  return typeof (value as CultNetTransportConnection).send === "function"
    && typeof (value as CultNetTransportConnection).close === "function"
    && "profile" in value;
}

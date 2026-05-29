import { EventEmitter } from "node:events";
import type { Duplex } from "node:stream";

import { decode, encode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetDocumentDeleteMessage, type CultNetDocumentPutMessage, type CultNetErrorMessage, type CultNetHelloMessage, type CultNetLoginMessage, type CultNetLoginSuccessMessage, type CultNetMessage, type CultNetRegisterMessage, type CultNetSampleChangeNameMessage, type CultNetSampleChatMessage, type CultNetSchemaCatalogRequestMessage, type CultNetSchemaCatalogResponseMessage, type CultNetSnapshotRequestMessage, type CultNetSnapshotResponseMessage, type CultNetVerifyMessage, type CultNetWireContract } from "./contracts";
import { encodeFrame, LengthPrefixedMessageFramer } from "./framing";

export interface CultNetPeerEvents {
  message: (message: CultNetMessage) => void;
  invalidMessage: (error: Error) => void;
  close: () => void;
  error: (error: Error) => void;
}

export interface CultNetPeerOptions {
  wireContract: CultNetWireContract;
}

export class CultNetPeer extends EventEmitter {
  readonly #stream: Duplex;
  readonly #framer = new LengthPrefixedMessageFramer();
  readonly #wireContract: CultNetWireContract;

  constructor(stream: Duplex, options: CultNetPeerOptions) {
    super();
    if (!options?.wireContract) {
      throw new Error("CultNetPeer requires an explicit wireContract.");
    }

    this.#stream = stream;
    this.#wireContract = options.wireContract;
    this.#stream.on("data", (chunk: Buffer) => {
      for (const frame of this.#framer.push(chunk)) {
        try {
          const decoded = decode(frame);
          const message = parseCultNetMessage(decoded, this.#wireContract);
          this.emit("message", message);
        } catch (error) {
          this.emit("invalidMessage", error instanceof Error ? error : new Error(String(error)));
        }
      }
    });
    this.#stream.on("close", () => this.emit("close"));
    this.#stream.on("error", (error) => this.emit("error", error instanceof Error ? error : new Error(String(error))));
  }

  send(message: CultNetMessage): void {
    const wireValue = encodeCultNetMessageForWire(message, this.#wireContract);
    this.#stream.write(encodeFrame(encode(wireValue)));
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
    this.#stream.end();
  }
}

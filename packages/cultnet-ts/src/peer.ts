import { decode, encode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetDocumentDeleteMessage, type CultNetDocumentPutMessage, type CultNetErrorMessage, type CultNetHelloMessage, type CultNetLoginMessage, type CultNetLoginSuccessMessage, type CultNetMessage, type CultNetRegisterMessage, type CultNetSampleChangeNameMessage, type CultNetSampleChatMessage, type CultNetSchemaCatalogRequestMessage, type CultNetSchemaCatalogResponseMessage, type CultNetSnapshotRequestMessage, type CultNetSnapshotResponseMessage, type CultNetVerifyMessage, type CultNetWireContract } from "./contracts";
import { encodeFrame, LengthPrefixedMessageFramer } from "./framing";
import {
  createCultNetDuplexTransport,
  isCultNetTransport,
  toError,
  type CultNetLegacyDuplexLike,
  type CultNetTransport,
} from "./transport";

export interface CultNetPeerEvents {
  message: (message: CultNetMessage) => void;
  invalidMessage: (error: Error) => void;
  close: () => void;
  error: (error: Error) => void;
}

export interface CultNetPeerOptions {
  wireContract: CultNetWireContract;
}

export class CultNetPeer {
  readonly #events = new CultNetPeerEventEmitter();
  readonly #framer = new LengthPrefixedMessageFramer();
  readonly #transport: CultNetTransport;
  readonly #wireContract: CultNetWireContract;

  constructor(transport: CultNetTransport | CultNetLegacyDuplexLike, options: CultNetPeerOptions) {
    if (!options?.wireContract) {
      throw new Error("CultNetPeer requires an explicit wireContract.");
    }

    this.#transport = isCultNetTransport(transport)
      ? transport
      : createCultNetDuplexTransport(transport);
    this.#wireContract = options.wireContract;
    this.#transport.onBytes((chunk) => {
      for (const frame of this.#framer.push(chunk)) {
        try {
          const decoded = decode(frame);
          const message = parseCultNetMessage(decoded, this.#wireContract);
          this.emit("message", message);
        } catch (error) {
          this.emit("invalidMessage", toError(error));
        }
      }
    });
    this.#transport.onClose(() => this.emit("close"));
    this.#transport.onError((error) => this.emit("error", error));
  }

  on<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    handler: CultNetPeerEvents[TEvent],
  ): this {
    this.#events.on(event, handler);
    return this;
  }

  once<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    handler: CultNetPeerEvents[TEvent],
  ): this {
    this.#events.once(event, handler);
    return this;
  }

  off<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    handler: CultNetPeerEvents[TEvent],
  ): this {
    this.#events.off(event, handler);
    return this;
  }

  emit<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    ...args: Parameters<CultNetPeerEvents[TEvent]>
  ): boolean {
    return this.#events.emit(event, ...args);
  }

  send(message: CultNetMessage): void {
    const wireValue = encodeCultNetMessageForWire(message, this.#wireContract);
    this.#transport.send(encodeFrame(encode(wireValue)));
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
    this.#transport.close();
  }
}

class CultNetPeerEventEmitter {
  readonly #handlers = new Map<keyof CultNetPeerEvents, Set<(...args: never[]) => void>>();

  on<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    handler: CultNetPeerEvents[TEvent],
  ): void {
    const handlers = this.#handlers.get(event) ?? new Set();
    handlers.add(handler as (...args: never[]) => void);
    this.#handlers.set(event, handlers);
  }

  once<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    handler: CultNetPeerEvents[TEvent],
  ): void {
    const onceHandler = ((...args: Parameters<CultNetPeerEvents[TEvent]>) => {
      this.off(event, onceHandler as CultNetPeerEvents[TEvent]);
      (handler as (...handlerArgs: Parameters<CultNetPeerEvents[TEvent]>) => void)(...args);
    }) as CultNetPeerEvents[TEvent];
    this.on(event, onceHandler);
  }

  off<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    handler: CultNetPeerEvents[TEvent],
  ): void {
    this.#handlers.get(event)?.delete(handler as (...args: never[]) => void);
  }

  emit<TEvent extends keyof CultNetPeerEvents>(
    event: TEvent,
    ...args: Parameters<CultNetPeerEvents[TEvent]>
  ): boolean {
    const handlers = this.#handlers.get(event);
    if (!handlers || handlers.size === 0) {
      return false;
    }
    for (const handler of [...handlers]) {
      handler(...args as never[]);
    }
    return true;
  }
}

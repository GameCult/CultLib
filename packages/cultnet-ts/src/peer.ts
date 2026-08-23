import { EventEmitter } from "node:events";
import type { Duplex } from "node:stream";

import { decode, encode } from "@msgpack/msgpack";

import { encodeCultNetMessageForWire, parseCultNetMessage, type CultNetDocumentDeleteMessage, type CultNetDocumentPutMessage, type CultNetErrorMessage, type CultNetHelloMessage, type CultNetLoginMessage, type CultNetLoginSuccessMessage, type CultNetMessage, type CultNetRegisterMessage, type CultNetSampleChangeNameMessage, type CultNetSampleChatMessage, type CultNetSchemaCatalogRequestMessage, type CultNetSchemaCatalogResponseMessage, type CultNetShardCatalogRequestMessage, type CultNetShardCatalogResponseMessage, type CultNetSnapshotRequestMessage, type CultNetSnapshotResponseMessage, type CultNetTransportProfile, type CultNetVerifyMessage, type CultNetWireContract } from "./contracts";
import { encodeFrame, LengthPrefixedMessageFramer } from "./framing";
import { CultNetSchemaCatalog, type CultNetSchemaCatalogOptions } from "./schema-discovery";
import { CultNetShardCatalog, type CultNetShardCatalogOptions } from "./shard-catalog";
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

export interface CultNetPeerSchemaCatalogRequestOptions extends CultNetSchemaCatalogOptions {
  messageId?: string;
  timeoutMs?: number;
}

export interface CultNetPeerShardCatalogRequestOptions extends CultNetShardCatalogOptions {
  messageId?: string;
  timeoutMs?: number;
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

  async flush(timeoutMs?: number): Promise<void> {
    await this.#transport?.flush?.(timeoutMs);
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

  sendShardCatalogRequest(message: CultNetShardCatalogRequestMessage): void {
    this.send(message);
  }

  sendShardCatalogResponse(message: CultNetShardCatalogResponseMessage): void {
    this.send(message);
  }

  requestSchemaCatalog(
    options: CultNetPeerSchemaCatalogRequestOptions = {},
  ): Promise<CultNetSchemaCatalogResponseMessage> {
    const messageId = options.messageId ?? createMessageId("cultnet-ts-schema-catalog");
    const timeoutMs = options.timeoutMs ?? 4_000;

    return new Promise<CultNetSchemaCatalogResponseMessage>((resolve, reject) => {
      const cleanup = (): void => {
        clearTimeout(timer);
        this.off("message", onMessage);
        this.off("invalidMessage", onInvalidMessage);
        this.off("error", onError);
        this.off("close", onClose);
      };
      const rejectWith = (error: Error): void => {
        cleanup();
        reject(error);
      };
      const onMessage = (message: CultNetMessage): void => {
        if (message.schemaVersion !== "cultnet.schema_catalog_response.v0" || message.messageId !== messageId) {
          return;
        }
        cleanup();
        resolve(message);
      };
      const onInvalidMessage = (error: Error): void => rejectWith(error);
      const onError = (error: Error): void => rejectWith(error);
      const onClose = (): void => rejectWith(new Error("CultNet peer closed before schema catalog response."));
      const timer = setTimeout(
        () => rejectWith(new Error(`Timed out waiting for CultNet schema catalog response ${messageId}.`)),
        timeoutMs,
      );

      this.on("message", onMessage);
      this.on("invalidMessage", onInvalidMessage);
      this.on("error", onError);
      this.on("close", onClose);
      this.sendSchemaCatalogRequest({
        schemaVersion: "cultnet.schema_catalog_request.v0",
        messageId,
        includeSchemaJson: options.includeSchemaJson,
        schemaIds: options.schemaIds !== undefined ? [...options.schemaIds] : undefined,
        kinds: options.kinds !== undefined ? [...options.kinds] : undefined,
      });
    });
  }

  async fetchSchemaDescriptors(
    options: CultNetPeerSchemaCatalogRequestOptions = {},
  ) {
    return new CultNetSchemaCatalog().applyResponse(await this.requestSchemaCatalog(options));
  }

  async syncSchemaCatalog(
    catalog: CultNetSchemaCatalog,
    options: CultNetPeerSchemaCatalogRequestOptions = {},
  ) {
    return catalog.applyResponse(await this.requestSchemaCatalog(options));
  }

  requestShardCatalog(
    options: CultNetPeerShardCatalogRequestOptions = {},
  ): Promise<CultNetShardCatalogResponseMessage> {
    const messageId = options.messageId ?? createMessageId("cultnet-ts-shard-catalog");
    const timeoutMs = options.timeoutMs ?? 4_000;

    return new Promise<CultNetShardCatalogResponseMessage>((resolve, reject) => {
      const cleanup = (): void => {
        clearTimeout(timer);
        this.off("message", onMessage);
        this.off("invalidMessage", onInvalidMessage);
        this.off("error", onError);
        this.off("close", onClose);
      };
      const rejectWith = (error: Error): void => {
        cleanup();
        reject(error);
      };
      const onMessage = (message: CultNetMessage): void => {
        if (message.schemaVersion !== "cultnet.shard_catalog_response.v0" || message.messageId !== messageId) {
          return;
        }
        cleanup();
        resolve(message);
      };
      const onInvalidMessage = (error: Error): void => rejectWith(error);
      const onError = (error: Error): void => rejectWith(error);
      const onClose = (): void => rejectWith(new Error("CultNet peer closed before shard catalog response."));
      const timer = setTimeout(
        () => rejectWith(new Error(`Timed out waiting for CultNet shard catalog response ${messageId}.`)),
        timeoutMs,
      );

      this.on("message", onMessage);
      this.on("invalidMessage", onInvalidMessage);
      this.on("error", onError);
      this.on("close", onClose);
      this.sendShardCatalogRequest({
        schemaVersion: "cultnet.shard_catalog_request.v0",
        messageId,
        schemaIds: options.schemaIds !== undefined ? [...options.schemaIds] : undefined,
        recordKeys: options.recordKeys !== undefined ? [...options.recordKeys] : undefined,
      });
    });
  }

  async fetchShardDescriptors(
    options: CultNetPeerShardCatalogRequestOptions = {},
  ) {
    return new CultNetShardCatalog().applyResponse(await this.requestShardCatalog(options));
  }

  async syncShardCatalog(
    catalog: CultNetShardCatalog,
    options: CultNetPeerShardCatalogRequestOptions = {},
  ) {
    return catalog.applyResponse(await this.requestShardCatalog(options));
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

function createMessageId(prefix: string): string {
  return `${prefix}-${Date.now().toString(36)}-${Math.random().toString(16).slice(2)}`;
}

function isCultNetTransportConnection(value: Duplex | CultNetTransportConnection): value is CultNetTransportConnection {
  return typeof (value as CultNetTransportConnection).send === "function"
    && typeof (value as CultNetTransportConnection).close === "function"
    && "profile" in value;
}

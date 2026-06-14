import { EventEmitter } from "node:events";
import type { Duplex } from "node:stream";

import type { CultNetTransportProfile } from "./contracts";
import { encodeFrame, LengthPrefixedMessageFramer } from "./framing";

export interface CultNetTransportFrame {
  channelId: string;
  payload: Uint8Array;
}

export interface CultNetTransportStats {
  bytesReceived: number;
  bytesSent: number;
  framesReceived: number;
  framesSent: number;
}

export interface CultNetTransportConnectionEvents {
  frame: (frame: CultNetTransportFrame) => void;
  close: () => void;
  error: (error: Error) => void;
}

export interface CultNetTransportConnection {
  readonly profile: CultNetTransportProfile;
  readonly stats: CultNetTransportStats;
  send(channelId: string, payload: Uint8Array): void;
  close(): void;
  on<EventName extends keyof CultNetTransportConnectionEvents>(
    eventName: EventName,
    listener: CultNetTransportConnectionEvents[EventName],
  ): this;
}

export interface TcpFramedTransportProfileOptions {
  transportId?: string;
  host?: string;
  port?: number;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
}

export function createTcpFramedTransportProfile(
  runtimeId: string,
  options: TcpFramedTransportProfileOptions = {},
): CultNetTransportProfile {
  const channel: CultNetTransportProfile["transports"][number]["channels"][number] = {
    channelId: "schema",
    delivery: "reliable",
    ordering: "ordered",
  };
  if (options.maxPayloadBytes !== undefined) {
    channel.maxPayloadBytes = options.maxPayloadBytes;
  }
  if (options.maxFragmentBytes !== undefined) {
    channel.maxFragmentBytes = options.maxFragmentBytes;
  }

  const transport: CultNetTransportProfile["transports"][number] = {
    transportId: options.transportId ?? "tcp-framed",
    protocol: "tcp_framed",
    wireContracts: ["cultnet.schema.v0"],
    channels: [channel],
  };
  if (options.host !== undefined) {
    transport.host = options.host;
  }
  if (options.port !== undefined) {
    transport.port = options.port;
  }

  return {
    schemaVersion: "cultnet.transport_profile.v0",
    runtimeId,
    transports: [transport],
  };
}

export class TcpFramedTransportConnection extends EventEmitter implements CultNetTransportConnection {
  readonly profile: CultNetTransportProfile;
  readonly #stream: Duplex;
  readonly #framer = new LengthPrefixedMessageFramer();
  readonly #stats: CultNetTransportStats = {
    bytesReceived: 0,
    bytesSent: 0,
    framesReceived: 0,
    framesSent: 0,
  };

  constructor(stream: Duplex, profile: CultNetTransportProfile) {
    super();
    this.#stream = stream;
    this.profile = profile;
    this.#stream.on("data", (chunk: Buffer) => {
      this.#stats.bytesReceived += chunk.length;
      for (const payload of this.#framer.push(chunk)) {
        this.#stats.framesReceived += 1;
        this.emit("frame", {
          channelId: "schema",
          payload,
        });
      }
    });
    this.#stream.on("close", () => this.emit("close"));
    this.#stream.on("error", (error) => this.emit("error", error instanceof Error ? error : new Error(String(error))));
  }

  get stats(): CultNetTransportStats {
    return { ...this.#stats };
  }

  send(channelId: string, payload: Uint8Array): void {
    if (channelId !== "schema") {
      throw new Error(`tcp_framed transport only supports the schema channel, got "${channelId}".`);
    }

    const frame = encodeFrame(payload);
    this.#stats.bytesSent += frame.length;
    this.#stats.framesSent += 1;
    this.#stream.write(frame);
  }

  close(): void {
    this.#stream.end();
  }
}

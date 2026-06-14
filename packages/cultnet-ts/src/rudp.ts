import type { CultNetTransportProfile } from "./contracts";

const RUDP_MAGIC = [0x43, 0x4e, 0x52, 0x30] as const; // CNR0
const RUDP_VERSION = 0;
const RUDP_FIXED_HEADER_BYTES = 36;
const MAX_CHANNEL_ID_BYTES = 255;

export type CultNetRudpPacketType =
  | "connect"
  | "accept"
  | "data"
  | "ack"
  | "ping"
  | "pong"
  | "disconnect";

export interface CultNetRudpPacket {
  packetType: CultNetRudpPacketType;
  connectionId: number;
  sequence: number;
  ack: number;
  ackMask: number;
  channelId: string;
  reliable?: boolean;
  ordered?: boolean;
  sequenced?: boolean;
  fragmentId?: number;
  fragmentIndex?: number;
  fragmentCount?: number;
  payload?: Uint8Array;
}

export interface RudpTransportProfileOptions {
  transportId?: string;
  host?: string;
  port?: number;
  maxPayloadBytes?: number;
  maxFragmentBytes?: number;
}

const packetTypeToCode: Record<CultNetRudpPacketType, number> = {
  connect: 1,
  accept: 2,
  data: 3,
  ack: 4,
  ping: 5,
  pong: 6,
  disconnect: 7,
};

const packetTypeFromCode = new Map<number, CultNetRudpPacketType>(
  Object.entries(packetTypeToCode).map(([name, code]) => [code, name as CultNetRudpPacketType]),
);

export function createRudpTransportProfile(
  runtimeId: string,
  options: RudpTransportProfileOptions = {},
): CultNetTransportProfile {
  return {
    schemaVersion: "cultnet.transport_profile.v0",
    runtimeId,
    transports: [
      {
        transportId: options.transportId ?? "rudp",
        protocol: "rudp",
        host: options.host,
        port: options.port,
        wireContracts: ["cultnet.schema.v0"],
        channels: [
          {
            channelId: "schema",
            delivery: "reliable",
            ordering: "ordered",
            maxPayloadBytes: options.maxPayloadBytes,
            maxFragmentBytes: options.maxFragmentBytes,
          },
          {
            channelId: "latest",
            delivery: "unreliable",
            ordering: "sequenced",
            maxPayloadBytes: options.maxPayloadBytes,
            maxFragmentBytes: options.maxFragmentBytes,
          },
          {
            channelId: "realtime",
            delivery: "unreliable",
            ordering: "unordered",
            maxPayloadBytes: options.maxPayloadBytes,
            maxFragmentBytes: options.maxFragmentBytes,
          },
        ],
      },
    ],
  };
}

export function encodeRudpPacket(packet: CultNetRudpPacket): Uint8Array {
  const channelId = new TextEncoder().encode(packet.channelId);
  if (channelId.length > MAX_CHANNEL_ID_BYTES) {
    throw new Error("CultNet RUDP channel id cannot exceed 255 UTF-8 bytes.");
  }

  const payload = packet.payload ?? new Uint8Array();
  const headerBytes = RUDP_FIXED_HEADER_BYTES + channelId.length;
  const wire = new Uint8Array(headerBytes + payload.length);
  const view = new DataView(wire.buffer, wire.byteOffset, wire.byteLength);
  wire.set(RUDP_MAGIC, 0);
  view.setUint8(4, RUDP_VERSION);
  view.setUint8(5, packetTypeToCode[packet.packetType]);
  view.setUint8(6, encodeFlags(packet));
  view.setUint8(7, headerBytes);
  view.setUint32(8, toUint32(packet.connectionId, "connectionId"), false);
  view.setUint32(12, toUint32(packet.sequence, "sequence"), false);
  view.setUint32(16, toUint32(packet.ack, "ack"), false);
  view.setUint32(20, toUint32(packet.ackMask, "ackMask"), false);
  view.setUint16(24, toUint16(packet.fragmentId ?? 0, "fragmentId"), false);
  view.setUint16(26, toUint16(packet.fragmentIndex ?? 0, "fragmentIndex"), false);
  view.setUint16(28, toUint16(packet.fragmentCount ?? 0, "fragmentCount"), false);
  view.setUint32(30, toUint32(payload.length, "payload length"), false);
  view.setUint8(34, channelId.length);
  view.setUint8(35, 0);
  wire.set(channelId, RUDP_FIXED_HEADER_BYTES);
  wire.set(payload, headerBytes);
  return wire;
}

export function decodeRudpPacket(wire: Uint8Array): CultNetRudpPacket {
  if (wire.length < RUDP_FIXED_HEADER_BYTES) {
    throw new Error("CultNet RUDP packet is shorter than the fixed header.");
  }

  const view = new DataView(wire.buffer, wire.byteOffset, wire.byteLength);
  for (let index = 0; index < RUDP_MAGIC.length; index += 1) {
    if (view.getUint8(index) !== RUDP_MAGIC[index]) {
      throw new Error("CultNet RUDP packet has the wrong magic.");
    }
  }

  const version = view.getUint8(4);
  if (version !== RUDP_VERSION) {
    throw new Error(`Unsupported CultNet RUDP packet version ${version}.`);
  }

  const packetType = packetTypeFromCode.get(view.getUint8(5));
  if (!packetType) {
    throw new Error(`Unsupported CultNet RUDP packet type ${view.getUint8(5)}.`);
  }

  const headerBytes = view.getUint8(7);
  const channelIdLength = view.getUint8(34);
  if (headerBytes !== RUDP_FIXED_HEADER_BYTES + channelIdLength) {
    throw new Error("CultNet RUDP packet header length does not match the channel id length.");
  }

  const payloadLength = view.getUint32(30, false);
  if (wire.length !== headerBytes + payloadLength) {
    throw new Error("CultNet RUDP packet payload length does not match the packet size.");
  }

  const flags = view.getUint8(6);
  return {
    packetType,
    connectionId: view.getUint32(8, false),
    sequence: view.getUint32(12, false),
    ack: view.getUint32(16, false),
    ackMask: view.getUint32(20, false),
    fragmentId: view.getUint16(24, false),
    fragmentIndex: view.getUint16(26, false),
    fragmentCount: view.getUint16(28, false),
    channelId: new TextDecoder().decode(wire.subarray(RUDP_FIXED_HEADER_BYTES, headerBytes)),
    reliable: (flags & 0b0000_0001) !== 0,
    ordered: (flags & 0b0000_0010) !== 0,
    sequenced: (flags & 0b0000_0100) !== 0,
    payload: wire.subarray(headerBytes),
  };
}

function encodeFlags(packet: CultNetRudpPacket): number {
  return (
    (packet.reliable ? 0b0000_0001 : 0) |
    (packet.ordered ? 0b0000_0010 : 0) |
    (packet.sequenced ? 0b0000_0100 : 0) |
    ((packet.fragmentCount ?? 0) > 0 ? 0b0000_1000 : 0)
  );
}

function toUint32(value: number, fieldName: string): number {
  if (!Number.isInteger(value) || value < 0 || value > 0xffffffff) {
    throw new Error(`CultNet RUDP ${fieldName} must fit in uint32.`);
  }

  return value;
}

function toUint16(value: number, fieldName: string): number {
  if (!Number.isInteger(value) || value < 0 || value > 0xffff) {
    throw new Error(`CultNet RUDP ${fieldName} must fit in uint16.`);
  }

  return value;
}

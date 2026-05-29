import { readFile } from "node:fs/promises";

import { decode, encode } from "@msgpack/msgpack";

import { z } from "zod";

export const INTEROP_DOCUMENT_TYPE = "cultnet.interop-note";
export const INTEROP_SCHEMA_VERSION = "cultnet.interop_note.v0";
export const INTEROP_MUTATION_INTENT_DOCUMENT_TYPE = "cultnet.interop-note-mutation-intent";
export const INTEROP_MUTATION_INTENT_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-intent.schema.json";
export const INTEROP_MUTATION_INTENT_SCHEMA_VERSION = "cultnet.interop_note_mutation_intent.v0";
export const INTEROP_MUTATION_RECEIPT_DOCUMENT_TYPE = "cultnet.interop-note-mutation-receipt";
export const INTEROP_MUTATION_RECEIPT_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-note-mutation-receipt.schema.json";
export const INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION = "cultnet.interop_note_mutation_receipt.v0";
export const INTEROP_FIRE_COMMAND_DOCUMENT_TYPE = "cultnet.interop-fire-weapon-command";
export const INTEROP_FIRE_COMMAND_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-command.schema.json";
export const INTEROP_FIRE_COMMAND_SCHEMA_VERSION = "cultnet.interop_fire_weapon_command.v0";
export const INTEROP_FIRE_RECEIPT_DOCUMENT_TYPE = "cultnet.interop-fire-weapon-receipt";
export const INTEROP_FIRE_RECEIPT_SCHEMA_ID = "https://github.com/GameCult/cultnet-ts/integration/contracts/cultnet.interop-fire-weapon-receipt.schema.json";
export const INTEROP_FIRE_RECEIPT_SCHEMA_VERSION = "cultnet.interop_fire_weapon_receipt.v0";
export const INTEROP_WIRE_CONTRACT = "cultnet.schema.v0" as const;
export const DISCOVERY_PROBE_SCHEMA_VERSION = "cultnet.discovery_probe.v0";
export const DISCOVERY_ANNOUNCE_SCHEMA_VERSION = "cultnet.discovery_announce.v0";

export const interopNoteSchema = z.object({
  schemaVersion: z.literal(INTEROP_SCHEMA_VERSION),
  documentId: z.string().min(1),
  authorRuntimeId: z.string().min(1),
  title: z.string().min(1),
  body: z.string().min(1),
  tags: z.array(z.string().min(1)),
});

export type InteropNote = z.infer<typeof interopNoteSchema>;

export const interopMutationIntentSchema = z.object({
  schemaVersion: z.literal(INTEROP_MUTATION_INTENT_SCHEMA_VERSION),
  intentId: z.string().min(1),
  targetDocumentId: z.string().min(1),
  appendBody: z.string().min(1),
  appendTag: z.string().min(1),
});

export type InteropMutationIntent = z.infer<typeof interopMutationIntentSchema>;

export const interopMutationReceiptSchema = z.object({
  schemaVersion: z.literal(INTEROP_MUTATION_RECEIPT_SCHEMA_VERSION),
  intentId: z.string().min(1),
  accepted: z.boolean(),
  documentId: z.string().min(1),
  body: z.string(),
  tags: z.array(z.string().min(1)),
  error: z.string().optional(),
});

export type InteropMutationReceipt = z.infer<typeof interopMutationReceiptSchema>;

export const interopFireCommandSchema = z.object({
  schemaVersion: z.literal(INTEROP_FIRE_COMMAND_SCHEMA_VERSION),
  commandId: z.string().min(1),
  characterId: z.string().min(1),
  weaponId: z.string().min(1),
});

export type InteropFireCommand = z.infer<typeof interopFireCommandSchema>;

export const interopFireReceiptSchema = z.object({
  schemaVersion: z.literal(INTEROP_FIRE_RECEIPT_SCHEMA_VERSION),
  commandId: z.string().min(1),
  accepted: z.boolean(),
  characterId: z.string().min(1),
  weaponId: z.string().min(1),
  shotsFired: z.number().int().nonnegative(),
  ammoRemaining: z.number().int().nonnegative(),
  error: z.string().optional(),
});

export type InteropFireReceipt = z.infer<typeof interopFireReceiptSchema>;

export interface SlotFormatter<T> {
  encode(value: T): Uint8Array;
  decode(payload: Uint8Array): T;
}

export function createInteropFormatter(): SlotFormatter<InteropNote> {
  return slotFormatter<InteropNote>("Interop note", 5, (value) => [
    value.schemaVersion,
    value.documentId,
    value.authorRuntimeId,
    value.title,
    value.body,
    value.tags,
  ], ([schemaVersion, documentId, authorRuntimeId, title, body, tags]) => interopNoteSchema.parse({
    schemaVersion,
    documentId,
    authorRuntimeId,
    title,
    body,
    tags: Array.isArray(tags) ? tags : [],
  }));
}

export function createInteropMutationIntentFormatter(): SlotFormatter<InteropMutationIntent> {
  return slotFormatter<InteropMutationIntent>("Interop mutation intent", 5, (value) => [
    value.schemaVersion,
    value.intentId,
    value.targetDocumentId,
    value.appendBody,
    value.appendTag,
  ], ([schemaVersion, intentId, targetDocumentId, appendBody, appendTag]) => interopMutationIntentSchema.parse({
    schemaVersion,
    intentId,
    targetDocumentId,
    appendBody,
    appendTag,
  }));
}

export function createInteropMutationReceiptFormatter(): SlotFormatter<InteropMutationReceipt> {
  return slotFormatter<InteropMutationReceipt>("Interop mutation receipt", 6, (value) => [
    value.schemaVersion,
    value.intentId,
    value.accepted,
    value.documentId,
    value.body,
    value.tags,
    value.error,
  ], ([schemaVersion, intentId, accepted, documentId, body, tags, error]) => interopMutationReceiptSchema.parse({
    schemaVersion,
    intentId,
    accepted,
    documentId,
    body,
    tags: Array.isArray(tags) ? tags : [],
    error: typeof error === "string" ? error : undefined,
  }));
}

export function createInteropFireCommandFormatter(): SlotFormatter<InteropFireCommand> {
  return slotFormatter<InteropFireCommand>("Interop fire command", 4, (value) => [
    value.schemaVersion,
    value.commandId,
    value.characterId,
    value.weaponId,
  ], ([schemaVersion, commandId, characterId, weaponId]) => interopFireCommandSchema.parse({
    schemaVersion,
    commandId,
    characterId,
    weaponId,
  }));
}

export function createInteropFireReceiptFormatter(): SlotFormatter<InteropFireReceipt> {
  return slotFormatter<InteropFireReceipt>("Interop fire receipt", 6, (value) => [
    value.schemaVersion,
    value.commandId,
    value.accepted,
    value.characterId,
    value.weaponId,
    value.shotsFired,
    value.ammoRemaining,
    value.error,
  ], ([schemaVersion, commandId, accepted, characterId, weaponId, shotsFired, ammoRemaining, error]) => interopFireReceiptSchema.parse({
    schemaVersion,
    commandId,
    accepted,
    characterId,
    weaponId,
    shotsFired,
    ammoRemaining,
    error: typeof error === "string" ? error : undefined,
  }));
}

function slotFormatter<T>(
  name: string,
  minimumSlots: number,
  encodeSlots: (value: T) => unknown[],
  decodeSlots: (slots: unknown[]) => T,
): SlotFormatter<T> {
  return {
    encode(value: T): Uint8Array {
      return encode(encodeSlots(value));
    },
    decode(payload: Uint8Array): T {
      const decoded = decode(payload);
      if (!Array.isArray(decoded) || decoded.length < minimumSlots) {
        throw new Error(`${name} payload must be a MessagePack array with at least ${minimumSlots} slots.`);
      }

      return decodeSlots(decoded);
    },
  };
}

export function createLegacyInteropNoteFormatter(): SlotFormatter<InteropNote> {
  return {
    encode(value: InteropNote): Uint8Array {
      return encode([
        value.schemaVersion,
        value.documentId,
        value.authorRuntimeId,
        value.title,
        value.body,
      ]);
    },
    decode: createInteropFormatter().decode,
  };
}

export function createMismatchedInteropNoteFormatter(): SlotFormatter<InteropNote> {
  return {
    encode(value: InteropNote): Uint8Array {
      return encode([
        value.schemaVersion,
        value.documentId,
        42,
        value.title,
        value.body,
        value.tags,
      ]);
    },
    decode: createInteropFormatter().decode,
  };
}

export const discoveryProbeSchema = z.object({
  schemaVersion: z.literal(DISCOVERY_PROBE_SCHEMA_VERSION),
  messageId: z.string().min(1),
  requesterRuntimeId: z.string().min(1),
});

export type DiscoveryProbe = z.infer<typeof discoveryProbeSchema>;

export const discoveryAnnounceSchema = z.object({
  schemaVersion: z.literal(DISCOVERY_ANNOUNCE_SCHEMA_VERSION),
  messageId: z.string().min(1),
  runtimeId: z.string().min(1),
  runtimeKind: z.string().min(1),
  displayName: z.string().min(1),
  agentId: z.string().min(1).optional(),
  tcpHost: z.string().min(1),
  tcpPort: z.number().int().positive().max(65535),
  wireContract: z.literal(INTEROP_WIRE_CONTRACT),
  supportedDocumentTypes: z.array(z.string().min(1)),
  supportsSchemaCatalog: z.boolean(),
});

export type DiscoveryAnnounce = z.infer<typeof discoveryAnnounceSchema>;

export interface LoadedSchemaRegistration {
  schemaId: string;
  title?: string;
  schemaJson: string;
  schema: object;
}

export async function loadInteropSchemaRegistration(schemaPath: string): Promise<LoadedSchemaRegistration> {
  const schemaJson = await readFile(schemaPath, "utf8");
  const parsed = JSON.parse(schemaJson) as {
    $id?: string;
    title?: string;
  };

  if (!parsed.$id) {
    throw new Error(`Interop schema at ${schemaPath} is missing $id.`);
  }

  return {
    schemaId: parsed.$id,
    title: parsed.title,
    schemaJson,
    schema: JSON.parse(schemaJson) as object,
  };
}

export function buildInteropNote(runtimeId: string, displayName: string): InteropNote {
  return {
    schemaVersion: INTEROP_SCHEMA_VERSION,
    documentId: `note:${runtimeId}`,
    authorRuntimeId: runtimeId,
    title: `${displayName} keeps a little note`,
    body: `${runtimeId} can move CultNet state without begging the gods for translation.`,
    tags: [runtimeId, "interop", "cultnet"],
  };
}

export function parseArgs(argv: readonly string[]): Map<string, string> {
  const args = new Map<string, string>();
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith("--")) {
      continue;
    }

    const name = token.slice(2);
    const value = argv[index + 1];
    if (!value || value.startsWith("--")) {
      throw new Error(`Missing value for --${name}`);
    }

    args.set(name, value);
    index += 1;
  }

  return args;
}

export function requireArg(args: Map<string, string>, name: string): string {
  const value = args.get(name);
  if (!value) {
    throw new Error(`Missing required argument --${name}`);
  }

  return value;
}

export function optionalIntArg(args: Map<string, string>, name: string, fallback?: number): number | undefined {
  const raw = args.get(name);
  if (!raw) {
    return fallback;
  }

  const parsed = Number.parseInt(raw, 10);
  if (!Number.isInteger(parsed)) {
    throw new Error(`Argument --${name} must be an integer.`);
  }

  return parsed;
}

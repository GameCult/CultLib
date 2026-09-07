// Runtime presence health: the dual-proved statement one launched incarnation
// makes about itself.
//
// This is the TypeScript half of `gamecult.runtime_presence_health.v2`, the
// contract `cultnet-rs` defines in `runtime_authority_contracts.rs`. Odin admits
// a document as runtime presence only when it carries this schema id and its
// record key equals the signed target; anything else it files away as an
// ordinary document, and no topology correlation is ever produced for it.
//
// Two proofs cover one payload. The stable provider key says "this service made
// this statement"; the activation key Idunn hands the process at launch says
// "and it is the incarnation Idunn started". Neither establishes admission
// alone, which is why both signature fields are empty in the signed bytes: it
// avoids a circular signature while binding every authority-bearing field and
// both signer identities.
//
// The wire shape is a positional tuple indexed by the same field slots the Rust
// derive emits. Field N here is field N there; that correspondence is the whole
// contract, and the constants below name the slots so it stays checkable.

import { encode } from "@msgpack/msgpack";

export const GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA = "gamecult.runtime_presence_health.v2";

/** Purpose for the stable provider-health signature over the proof payload. */
export const GAMECULT_RUNTIME_PRESENCE_HEALTH_SIGNING_PURPOSE =
  "gamecult.runtime_presence_health.v2";

/** Purpose for the activation-scoped proof over the same payload. */
export const GAMECULT_RUNTIME_ACTIVATION_PROOF_SIGNING_PURPOSE =
  "gamecult.runtime_presence.activation-proof.v1";

/** systemd file-descriptor name Idunn uses for the activation signing key. */
export const IDUNN_RUNTIME_ACTIVATION_CREDENTIAL_NAME = "gamecult-idunn-runtime-activation-key";

/** systemd file-descriptor name Idunn uses for the provider-health identity. */
export const GAMECULT_RUNTIME_PRESENCE_IDENTITY_NAME = "gamecult-runtime-presence-identity";

const PROVIDER_HEALTH_SIGNATURE_DOMAIN = Buffer.from(
  "gamecult.provider-health.signature.v1\0",
  "utf8",
);

const RUNTIME_ACTIVATION_SIGNATURE_DOMAIN = Buffer.from(
  "idunn.runtime-activation.signature.v1\0",
  "utf8",
);

/**
 * Positional slots, mirroring the `#[cultcache(key = N)]` attributes on
 * `GameCultRuntimePresenceHealthRecord`. The tuple is always this long: the
 * Rust derive serialises `max_slot + 1` elements regardless of which are set.
 */
export const RUNTIME_PRESENCE_SLOT = {
  schemaVersion: 0,
  target: 1,
  expectedProjectionSha256: 2,
  planId: 3,
  incarnationId: 4,
  sealedReleaseId: 5,
  activationWitnessSha256: 6,
  stateSchemaGeneration: 7,
  stateContractSha256: 8,
  runtimeId: 9,
  runtimeInstanceId: 10,
  boundEndpoint: 11,
  capabilities: 12,
  healthContract: 13,
  state: 14,
  detail: 15,
  writeLeaseSha256: 16,
  signerIdentityId: 17,
  publisherSequence: 18,
  observedAtUnixMillis: 19,
  signatureAlgorithm: 20,
  signature: 21,
  activationSignerIdentityId: 22,
  activationSignature: 23,
} as const;

export const RUNTIME_PRESENCE_FIELD_COUNT = 24;

/** A capability claim. Nested as its own positional tuple, not a map. */
export interface GameCultRuntimeCapability {
  capability: string;
  schema: string;
  compatibility: string;
  capacity: number;
}

/** The states the contract admits. */
export type RuntimePresenceState = "warming" | "active" | "degraded" | "failed";

export interface RuntimePresenceHealth {
  target: string;
  expectedProjectionSha256: string;
  planId: string;
  incarnationId: string;
  sealedReleaseId: string;
  activationWitnessSha256: string;
  stateSchemaGeneration: string | null;
  stateContractSha256: string | null;
  runtimeId: string;
  runtimeInstanceId: string;
  boundEndpoint: string | null;
  capabilities: GameCultRuntimeCapability[];
  healthContract: string;
  state: RuntimePresenceState;
  detail: string;
  writeLeaseSha256: string | null;
  signerIdentityId: string;
  publisherSequence: number;
  observedAtUnixMillis: number;
  activationSignerIdentityId: string;
}

function encodeCapability(capability: GameCultRuntimeCapability): unknown[] {
  // Plain serde struct on the Rust side, and the record is encoded with
  // rmp_serde's compact form, so a capability is a positional tuple too.
  return [capability.capability, capability.schema, capability.compatibility, capability.capacity];
}

/**
 * Capabilities are canonical only when strictly increasing by
 * `(capability, schema, compatibility)`. Sorting here rather than trusting the
 * caller keeps a correct statement from being rejected over field order.
 */
function canonicalCapabilities(capabilities: GameCultRuntimeCapability[]): unknown[][] {
  const sorted = [...capabilities].sort((left, right) => {
    const leftKey = [left.capability, left.schema, left.compatibility];
    const rightKey = [right.capability, right.schema, right.compatibility];
    for (let index = 0; index < leftKey.length; index += 1) {
      if (leftKey[index]! < rightKey[index]!) return -1;
      if (leftKey[index]! > rightKey[index]!) return 1;
    }
    return 0;
  });
  return sorted.map(encodeCapability);
}

function presenceTuple(
  presence: RuntimePresenceHealth,
  signature: Uint8Array,
  activationSignature: Uint8Array,
): unknown[] {
  const fields = new Array<unknown>(RUNTIME_PRESENCE_FIELD_COUNT);
  fields[RUNTIME_PRESENCE_SLOT.schemaVersion] = GAMECULT_RUNTIME_PRESENCE_HEALTH_SCHEMA;
  fields[RUNTIME_PRESENCE_SLOT.target] = presence.target;
  fields[RUNTIME_PRESENCE_SLOT.expectedProjectionSha256] = presence.expectedProjectionSha256;
  fields[RUNTIME_PRESENCE_SLOT.planId] = presence.planId;
  fields[RUNTIME_PRESENCE_SLOT.incarnationId] = presence.incarnationId;
  fields[RUNTIME_PRESENCE_SLOT.sealedReleaseId] = presence.sealedReleaseId;
  fields[RUNTIME_PRESENCE_SLOT.activationWitnessSha256] = presence.activationWitnessSha256;
  fields[RUNTIME_PRESENCE_SLOT.stateSchemaGeneration] = presence.stateSchemaGeneration;
  fields[RUNTIME_PRESENCE_SLOT.stateContractSha256] = presence.stateContractSha256;
  fields[RUNTIME_PRESENCE_SLOT.runtimeId] = presence.runtimeId;
  fields[RUNTIME_PRESENCE_SLOT.runtimeInstanceId] = presence.runtimeInstanceId;
  fields[RUNTIME_PRESENCE_SLOT.boundEndpoint] = presence.boundEndpoint;
  fields[RUNTIME_PRESENCE_SLOT.capabilities] = canonicalCapabilities(presence.capabilities);
  fields[RUNTIME_PRESENCE_SLOT.healthContract] = presence.healthContract;
  fields[RUNTIME_PRESENCE_SLOT.state] = presence.state;
  fields[RUNTIME_PRESENCE_SLOT.detail] = presence.detail;
  fields[RUNTIME_PRESENCE_SLOT.writeLeaseSha256] = presence.writeLeaseSha256;
  fields[RUNTIME_PRESENCE_SLOT.signerIdentityId] = presence.signerIdentityId;
  fields[RUNTIME_PRESENCE_SLOT.publisherSequence] = presence.publisherSequence;
  fields[RUNTIME_PRESENCE_SLOT.observedAtUnixMillis] = presence.observedAtUnixMillis;
  fields[RUNTIME_PRESENCE_SLOT.signatureAlgorithm] = "ed25519";
  fields[RUNTIME_PRESENCE_SLOT.signature] = signature;
  fields[RUNTIME_PRESENCE_SLOT.activationSignerIdentityId] = presence.activationSignerIdentityId;
  fields[RUNTIME_PRESENCE_SLOT.activationSignature] = activationSignature;
  return fields;
}

/**
 * Reject statements the contract forbids, before they are signed.
 *
 * These are the same conditions `validate_shape` enforces on the Rust side. A
 * statement that fails here would be refused after a round trip and a signature;
 * failing early names the actual problem instead.
 */
export function validateRuntimePresence(presence: RuntimePresenceHealth): void {
  if (presence.publisherSequence <= 0 || !Number.isSafeInteger(presence.publisherSequence)) {
    throw new Error("runtime presence publisher sequence must be a positive integer");
  }
  if (presence.observedAtUnixMillis <= 0 || !Number.isSafeInteger(presence.observedAtUnixMillis)) {
    throw new Error("runtime presence observation time must be a positive integer");
  }
  if (presence.detail.length > 512 || /\p{Cc}/u.test(presence.detail)) {
    throw new Error("runtime presence detail is too long or carries control characters");
  }
  if (presence.state === "warming" && presence.writeLeaseSha256 !== null) {
    throw new Error("warming runtime presence cannot claim a process write lease");
  }
  if (presence.stateSchemaGeneration === null && presence.writeLeaseSha256 !== null) {
    throw new Error("stateless runtime presence cannot claim a process write lease");
  }
  const generationPresent = presence.stateSchemaGeneration !== null;
  const contractPresent = presence.stateContractSha256 !== null;
  if (generationPresent !== contractPresent) {
    throw new Error("runtime presence state lineage is partial");
  }
}

/**
 * The one payload both proofs cover: the complete record with both signature
 * fields empty.
 */
export function runtimePresenceProofPayload(presence: RuntimePresenceHealth): Uint8Array {
  validateRuntimePresence(presence);
  return encode(presenceTuple(presence, new Uint8Array(), new Uint8Array()));
}

/** The complete signed record, as put on the wire. */
export function encodeRuntimePresenceHealth(
  presence: RuntimePresenceHealth,
  signature: Uint8Array,
  activationSignature: Uint8Array,
): Uint8Array {
  if (signature.length !== 64 || activationSignature.length !== 64) {
    throw new Error("runtime presence proofs must both be 64-byte ed25519 signatures");
  }
  validateRuntimePresence(presence);
  return encode(presenceTuple(presence, signature, activationSignature));
}

function domainSeparated(domain: Buffer, purpose: string, payload: Uint8Array): Buffer {
  const purposeBytes = Buffer.from(purpose, "utf8");
  const lengths = Buffer.alloc(16);
  lengths.writeBigUInt64BE(BigInt(purposeBytes.length), 0);
  lengths.writeBigUInt64BE(BigInt(payload.length), 8);
  return Buffer.concat([
    domain,
    lengths.subarray(0, 8),
    purposeBytes,
    lengths.subarray(8, 16),
    Buffer.from(payload),
  ]);
}

/** Message the stable provider-health key signs. */
export function runtimePresenceProviderSigningMessage(payload: Uint8Array): Buffer {
  return domainSeparated(
    PROVIDER_HEALTH_SIGNATURE_DOMAIN,
    GAMECULT_RUNTIME_PRESENCE_HEALTH_SIGNING_PURPOSE,
    payload,
  );
}

/** Message the activation key Idunn passed at launch signs. */
export function runtimePresenceActivationSigningMessage(payload: Uint8Array): Buffer {
  return domainSeparated(
    RUNTIME_ACTIVATION_SIGNATURE_DOMAIN,
    GAMECULT_RUNTIME_ACTIVATION_PROOF_SIGNING_PURPOSE,
    payload,
  );
}

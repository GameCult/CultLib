import Ajv2020, { type ValidateFunction } from "ajv/dist/2020";

import documentRecordSchema from "../contracts/cultnet.document-record.schema.json";
import helloSchema from "../contracts/cultnet.hello.schema.json";
import loginSchema from "../contracts/cultnet.login.schema.json";
import registerSchema from "../contracts/cultnet.register.schema.json";
import verifySchema from "../contracts/cultnet.verify.schema.json";
import loginSuccessSchema from "../contracts/cultnet.login-success.schema.json";
import errorSchema from "../contracts/cultnet.error.schema.json";
import sampleChangeNameSchema from "../contracts/cultnet.sample-change-name.schema.json";
import sampleChatSchema from "../contracts/cultnet.sample-chat.schema.json";
import documentPutSchema from "../contracts/cultnet.document-put.schema.json";
import documentDeleteSchema from "../contracts/cultnet.document-delete.schema.json";
import rawDocumentRecordSchema from "../contracts/cultnet.raw-document-record.schema.json";
import documentPutRawSchema from "../contracts/cultnet.document-put-raw.schema.json";
import documentMutationContractSchema from "../contracts/cultnet.document-mutation-contract.schema.json";
import snapshotRequestSchema from "../contracts/cultnet.snapshot-request.schema.json";
import snapshotResponseSchema from "../contracts/cultnet.snapshot-response.schema.json";
import snapshotResponseRawSchema from "../contracts/cultnet.snapshot-response-raw.schema.json";
import schemaDescriptorSchema from "../contracts/cultnet.schema-descriptor.schema.json";
import schemaCatalogRequestSchema from "../contracts/cultnet.schema-catalog-request.schema.json";
import schemaCatalogResponseSchema from "../contracts/cultnet.schema-catalog-response.schema.json";
import ghostlightAgentStateSchema from "../contracts/ghostlight.agent-state.schema.json";

export type CultNetWireContract = "cultnet.schema.v0" | "gamecult.networking.v0";

export type CultNetSchemaVersion =
  | "cultnet.hello.v0"
  | "cultnet.login.v0"
  | "cultnet.register.v0"
  | "cultnet.verify.v0"
  | "cultnet.login_success.v0"
  | "cultnet.error.v0"
  | "cultnet.sample.change_name.v0"
  | "cultnet.sample.chat.v0"
  | "cultnet.document_put.v0"
  | "cultnet.document_delete.v0"
  | "cultnet.document_put_raw.v0"
  | "cultnet.snapshot_request.v0"
  | "cultnet.snapshot_response.v0"
  | "cultnet.snapshot_response_raw.v0"
  | "cultnet.schema_catalog_request.v0"
  | "cultnet.schema_catalog_response.v0";

export type CultNetSchemaKind = "wire_message" | "document_payload" | "shared_contract";
export type CultNetRawPayloadEncoding = "messagepack";

export interface CultNetDocumentMutationContract {
  documentType: string;
  payloadSchemaVersion?: string;
  operations: string[];
  authority: "readOnly" | "localUser" | "coordinator" | "runtime" | "denied";
  intentDocumentTypes?: string[];
  receiptDocumentTypes?: string[];
  notes?: string[];
}

export interface CultNetDocumentRecord<TPayload = unknown> {
  schemaId: string;
  recordKey: string;
  storedAt: string;
  payload: TPayload;
  sourceRuntimeId?: string;
  sourceAgentId?: string;
  sourceRole?: string;
  tags?: string[];
}

export interface CultNetHelloMessage {
  schemaVersion: "cultnet.hello.v0";
  runtimeId: string;
  runtimeKind: string;
  agentId?: string;
  role?: string;
  displayName?: string;
  supportedDocumentTypes?: string[];
  supportedMutationContracts?: CultNetDocumentMutationContract[];
  supportedMessageVersions?: string[];
  supportsSchemaCatalog?: boolean;
}

export interface CultNetLoginMessage {
  schemaVersion: "cultnet.login.v0";
  nonce: string;
  auth: string;
  password: string;
}

export interface CultNetRegisterMessage {
  schemaVersion: "cultnet.register.v0";
  nonce: string;
  email: string;
  password: string;
  name: string;
}

export interface CultNetVerifyMessage {
  schemaVersion: "cultnet.verify.v0";
  nonce: string;
  session: string;
}

export interface CultNetLoginSuccessMessage {
  schemaVersion: "cultnet.login_success.v0";
  nonce: string;
  session: string;
}

export interface CultNetErrorMessage {
  schemaVersion: "cultnet.error.v0";
  error: string;
}

export interface CultNetSampleChangeNameMessage {
  schemaVersion: "cultnet.sample.change_name.v0";
  name: string;
}

export interface CultNetSampleChatMessage {
  schemaVersion: "cultnet.sample.chat.v0";
  text: string;
}

export interface CultNetDocumentPutMessage<TPayload = unknown> {
  schemaVersion: "cultnet.document_put.v0";
  messageId: string;
  document: CultNetDocumentRecord<TPayload>;
}

export interface CultNetDocumentDeleteMessage {
  schemaVersion: "cultnet.document_delete.v0";
  messageId: string;
  schemaId: string;
  recordKey: string;
}

export interface CultNetRawDocumentRecord {
  schemaId: string;
  recordKey: string;
  storedAt: string;
  payloadEncoding: CultNetRawPayloadEncoding;
  payload: Uint8Array;
  sourceRuntimeId?: string;
  sourceAgentId?: string;
  sourceRole?: string;
  tags?: string[];
}

export interface CultNetDocumentPutRawMessage {
  schemaVersion: "cultnet.document_put_raw.v0";
  messageId: string;
  document: CultNetRawDocumentRecord;
}

export interface CultNetSnapshotRequestMessage {
  schemaVersion: "cultnet.snapshot_request.v0";
  messageId: string;
  schemaIds?: string[];
  recordKeys?: string[];
}

export interface CultNetSnapshotResponseMessage<TPayload = unknown> {
  schemaVersion: "cultnet.snapshot_response.v0";
  messageId: string;
  documents: CultNetDocumentRecord<TPayload>[];
}

export interface CultNetSnapshotResponseRawMessage {
  schemaVersion: "cultnet.snapshot_response_raw.v0";
  messageId: string;
  documents: CultNetRawDocumentRecord[];
}

export interface CultNetSchemaDescriptor {
  schemaId: string;
  kind: CultNetSchemaKind;
  schemaVersion?: string;
  documentType?: string;
  title?: string;
  wireContracts: CultNetWireContract[];
  contentHash: string;
  schemaJson?: string;
}

export interface CultNetSchemaCatalogRequestMessage {
  schemaVersion: "cultnet.schema_catalog_request.v0";
  messageId: string;
  includeSchemaJson?: boolean;
  schemaIds?: string[];
  kinds?: CultNetSchemaKind[];
}

export interface CultNetSchemaCatalogResponseMessage {
  schemaVersion: "cultnet.schema_catalog_response.v0";
  messageId: string;
  schemas: CultNetSchemaDescriptor[];
}

export interface GhostlightPressure {
  pressure_id: string;
  label: string;
  intensity: number;
}

export interface GhostlightWorldTime {
  label: string;
  [key: string]: unknown;
}

export interface GhostlightWorldState {
  world_id: string;
  setting: string;
  time: GhostlightWorldTime;
  canon_context: string[];
  ambient_pressures?: GhostlightPressure[];
}

export interface GhostlightIdentity {
  name: string;
  roles: string[];
  origin: string;
  public_description: string;
  private_notes?: string[];
}

export interface GhostlightStateVariable {
  mean: number;
  plasticity: number;
  current_activation: number;
  evidence?: string[];
}

export type GhostlightVariableMap = Record<string, GhostlightStateVariable>;

export interface GhostlightInferredStateVariable {
  observed_activation: number;
  attributed_disposition: number;
  confidence: number;
  evidence?: string[];
}

export type GhostlightInferredVariableMap = Record<string, GhostlightInferredStateVariable>;

export interface GhostlightValueCommitment {
  value_id: string;
  label: string;
  priority: number;
  unforgivable_if_betrayed: boolean;
}

export type GhostlightGoalScope = "immediate" | "scene" | "case" | "arc" | "life";
export type GhostlightGoalStatus = "active" | "blocked" | "dormant" | "resolved" | "abandoned";

export interface GhostlightGoal {
  goal_id: string;
  description: string;
  scope: GhostlightGoalScope;
  priority: number;
  emotional_stake: string;
  blockers?: string[];
  status: GhostlightGoalStatus;
}

export interface GhostlightMemory {
  memory_id: string;
  summary: string;
  salience: number;
  confidence: number;
  linked_event_ids?: string[];
  linked_relationship_id?: string;
}

export interface GhostlightMemoryBundle {
  episodic: GhostlightMemory[];
  semantic: GhostlightMemory[];
  relationship_summaries: GhostlightMemory[];
}

export interface GhostlightCanonicalState {
  underlying_organization: GhostlightVariableMap;
  stable_dispositions: GhostlightVariableMap;
  behavioral_dimensions: GhostlightVariableMap;
  presentation_strategy: GhostlightVariableMap;
  voice_style: GhostlightVariableMap;
  situational_state: GhostlightVariableMap;
  values: GhostlightValueCommitment[];
}

export type GhostlightBeliefClaimType =
  | "fact"
  | "identity_inference"
  | "motive_inference"
  | "prediction"
  | "norm"
  | "value_read"
  | "relationship_read";

export type GhostlightBeliefVisibility = "private" | "shared" | "public" | "rumor";

export interface GhostlightBelief {
  belief_id: string;
  claim: string;
  confidence: number;
  subject_id?: string;
  claim_type?: GhostlightBeliefClaimType;
  evidence_event_ids?: string[];
  visibility?: GhostlightBeliefVisibility;
  emotional_charge?: number;
}

export type GhostlightPerceivedDistortion =
  | "hostile_attribution"
  | "false_reassurance"
  | "overread_warmth"
  | "underread_threat"
  | "false_mask_detection"
  | "missed_mask_detection"
  | "status_injury"
  | "threat_amplification"
  | "institutional_suspicion_transfer"
  | "care_underread"
  | "mask_overfocus";

export interface GhostlightPerceivedStateOverlay {
  observer_agent_id: string;
  target_agent_id: string;
  perceived_dimensions: GhostlightInferredVariableMap;
  beliefs: GhostlightBelief[];
  distortions: GhostlightPerceivedDistortion[];
}

export interface GhostlightAgent {
  agent_id: string;
  identity: GhostlightIdentity;
  canonical_state: GhostlightCanonicalState;
  goals: GhostlightGoal[];
  memories: GhostlightMemoryBundle;
  perceived_state_overlays: GhostlightPerceivedStateOverlay[];
}

export interface GhostlightRelationship {
  relationship_id: string;
  source_id: string;
  target_id: string;
  stance: GhostlightVariableMap;
  summary: string;
  unresolved_incident_ids?: string[];
}

export type GhostlightEventKind =
  | "dialogue"
  | "action"
  | "memory"
  | "relationship_shift"
  | "scene_outcome"
  | "author_note";

export interface GhostlightEventExchangeTurn {
  speaker_id: string;
  utterance_summary: string;
  dialogue_function: string;
  observable_cues?: string[];
}

export interface GhostlightEventPrivateInterpretation {
  agent_id: string;
  interpretation: string;
  attributed_motive?: string;
  appraisal_tags?: string[];
  confidence: number;
}

export interface GhostlightStateDelta {
  path: string;
  delta: number;
}

export interface GhostlightEventEffect {
  agent_id: string;
  summary: string;
  state_deltas?: GhostlightStateDelta[];
  new_belief_ids?: string[];
  memory_update_ids?: string[];
}

export interface GhostlightEvent {
  event_id: string;
  kind: GhostlightEventKind;
  summary: string;
  participants: string[];
  pressure_tags: string[];
  observed_exchange?: GhostlightEventExchangeTurn[];
  private_interpretations?: GhostlightEventPrivateInterpretation[];
  event_effects?: GhostlightEventEffect[];
}

export interface GhostlightDialogueContextPack {
  speaker_agent_id: string;
  listener_ids: string[];
  speaker_local_truth: string[];
  speaker_beliefs: string[];
  active_memories: string[];
  active_goals: string[];
  presentation_constraints: string[];
}

export interface GhostlightScene {
  scene_id: string;
  location: string;
  participants: string[];
  public_stakes: string[];
  hidden_stakes: string[];
  dialogue_context_packs: GhostlightDialogueContextPack[];
}

export interface GhostlightAgentStateDocument {
  schema_version: "ghostlight.agent_state.v0";
  world: GhostlightWorldState;
  agents: GhostlightAgent[];
  relationships: GhostlightRelationship[];
  events: GhostlightEvent[];
  scenes: GhostlightScene[];
}

export type CultNetMessage =
  | CultNetHelloMessage
  | CultNetLoginMessage
  | CultNetRegisterMessage
  | CultNetVerifyMessage
  | CultNetLoginSuccessMessage
  | CultNetErrorMessage
  | CultNetSampleChangeNameMessage
  | CultNetSampleChatMessage
  | CultNetDocumentPutMessage
  | CultNetDocumentDeleteMessage
  | CultNetDocumentPutRawMessage
  | CultNetSnapshotRequestMessage
  | CultNetSnapshotResponseMessage
  | CultNetSnapshotResponseRawMessage
  | CultNetSchemaCatalogRequestMessage
  | CultNetSchemaCatalogResponseMessage;

const ajv = new Ajv2020({
  allErrors: true,
  strict: false,
});

const CULTNET_MESSAGE_SCHEMAS = [
  helloSchema,
  loginSchema,
  registerSchema,
  verifySchema,
  loginSuccessSchema,
  errorSchema,
  sampleChangeNameSchema,
  sampleChatSchema,
  documentPutSchema,
  documentDeleteSchema,
  documentPutRawSchema,
  snapshotRequestSchema,
  snapshotResponseSchema,
  snapshotResponseRawSchema,
  schemaCatalogRequestSchema,
  schemaCatalogResponseSchema,
] as const;

const cultNetValidators = new Map<CultNetSchemaVersion, ValidateFunction>();
for (const schema of [
  documentRecordSchema,
  rawDocumentRecordSchema,
  documentMutationContractSchema,
  schemaDescriptorSchema,
  ...CULTNET_MESSAGE_SCHEMAS,
]) {
  ajv.addSchema(schema);
}

for (const schema of CULTNET_MESSAGE_SCHEMAS) {
  const schemaVersion = schema.properties.schemaVersion.const as CultNetSchemaVersion;
  cultNetValidators.set(schemaVersion, ajv.compile(schema));
}

const ghostlightAgentStateValidator = ajv.compile(ghostlightAgentStateSchema);

export function parseCultNetMessage(input: unknown, wireContract: CultNetWireContract = "cultnet.schema.v0"): CultNetMessage {
  if (wireContract === "gamecult.networking.v0") {
    return parseGameCultNetworkingMessage(input);
  }

  if (!input || typeof input !== "object") {
    throw new Error("CultNet message must be an object.");
  }

  const schemaVersion = (input as { schemaVersion?: string }).schemaVersion as CultNetSchemaVersion | undefined;
  if (!schemaVersion) {
    throw new Error("CultNet message is missing schemaVersion.");
  }

  normalizeCultNetOptionalNulls(input as Record<string, unknown>, schemaVersion);

  const validator = cultNetValidators.get(schemaVersion);
  if (!validator) {
    throw new Error(`Unsupported CultNet schemaVersion "${schemaVersion}".`);
  }

  if (schemaVersion === "cultnet.document_put_raw.v0") {
    validateDocumentPutRawMessage(input);
    return input as CultNetDocumentPutRawMessage;
  }

  if (schemaVersion === "cultnet.snapshot_response_raw.v0") {
    validateSnapshotResponseRawMessage(input);
    return input as CultNetSnapshotResponseRawMessage;
  }

  if (!validator(input)) {
    throw new Error(renderValidationErrors(schemaVersion, validator));
  }

  return input as CultNetMessage;
}

export function encodeCultNetMessageForWire(
  message: CultNetMessage,
  wireContract: CultNetWireContract = "cultnet.schema.v0",
): unknown {
  if (wireContract === "gamecult.networking.v0") {
    return encodeGameCultNetworkingMessage(message);
  }

  return message;
}

export function validateGhostlightAgentState(input: unknown): GhostlightAgentStateDocument {
  if (!ghostlightAgentStateValidator(input)) {
    throw new Error(renderValidationErrors("ghostlight.agent_state.v0", ghostlightAgentStateValidator));
  }

  return input as unknown as GhostlightAgentStateDocument;
}

function renderValidationErrors(schemaVersion: string, validator: ValidateFunction): string {
  const details = validator.errors?.map((error) => {
    const location = error.instancePath.length > 0 ? error.instancePath : "/";
    return `${location}: ${error.message}`;
  }) ?? ["unknown validation failure"];
  return `Validation failed for ${schemaVersion}: ${details.join("; ")}`;
}

function parseGameCultNetworkingMessage(input: unknown): CultNetMessage {
  if (!Array.isArray(input) || input.length !== 2) {
    throw new Error("gamecult.networking.v0 messages must be a 2-element union array.");
  }

  const [unionTag, payload] = input;
  if (!Number.isInteger(unionTag)) {
    throw new Error("gamecult.networking.v0 message tag must be an integer.");
  }
  if (!Array.isArray(payload)) {
    throw new Error("gamecult.networking.v0 message payload must be an array.");
  }

  switch (unionTag) {
    case 0:
      return {
        schemaVersion: "cultnet.login.v0",
        nonce: requireLegacyBytes(payload[0], "LoginMessage.Nonce"),
        auth: requireLegacyBytes(payload[1], "LoginMessage.Auth"),
        password: requireLegacyBytes(payload[2], "LoginMessage.Password"),
      };
    case 1:
      return {
        schemaVersion: "cultnet.register.v0",
        nonce: requireLegacyBytes(payload[0], "RegisterMessage.Nonce"),
        email: requireLegacyBytes(payload[1], "RegisterMessage.Email"),
        password: requireLegacyBytes(payload[2], "RegisterMessage.Password"),
        name: requireLegacyBytes(payload[3], "RegisterMessage.Name"),
      };
    case 2:
      return {
        schemaVersion: "cultnet.verify.v0",
        nonce: requireLegacyBytes(payload[0], "VerifyMessage.Nonce"),
        session: requireLegacyBytes(payload[1], "VerifyMessage.Session"),
      };
    case 3:
      return {
        schemaVersion: "cultnet.login_success.v0",
        nonce: requireLegacyBytes(payload[0], "LoginSuccessMessage.Nonce"),
        session: requireLegacyBytes(payload[1], "LoginSuccessMessage.Session"),
      };
    case 4:
      return {
        schemaVersion: "cultnet.error.v0",
        error: requireLegacyString(payload[0], "ErrorMessage.Error"),
      };
    case 5:
      return {
        schemaVersion: "cultnet.sample.change_name.v0",
        name: requireLegacyString(payload[0], "ChangeNameMessage.Name"),
      };
    case 6:
      return {
        schemaVersion: "cultnet.sample.chat.v0",
        text: requireLegacyString(payload[0], "ChatMessage.Text"),
      };
    case 7:
      return {
        schemaVersion: "cultnet.schema_catalog_request.v0",
        messageId: requireLegacyString(payload[0], "SchemaCatalogRequestMessage.MessageId"),
        includeSchemaJson: requireLegacyBoolean(payload[1], "SchemaCatalogRequestMessage.IncludeSchemaJson"),
        schemaIds: requireLegacyOptionalStringArray(payload[2], "SchemaCatalogRequestMessage.SchemaIds"),
        kinds: requireLegacyOptionalSchemaKindArray(payload[3], "SchemaCatalogRequestMessage.Kinds"),
      };
    case 8:
      return {
        schemaVersion: "cultnet.schema_catalog_response.v0",
        messageId: requireLegacyString(payload[0], "SchemaCatalogResponseMessage.MessageId"),
        schemas: requireLegacySchemaDescriptorArray(payload[1], "SchemaCatalogResponseMessage.Schemas"),
      };
    default:
      throw new Error(`Unsupported gamecult.networking.v0 union tag "${unionTag}".`);
  }
}

function encodeGameCultNetworkingMessage(message: CultNetMessage): [number, unknown[]] {
  switch (message.schemaVersion) {
    case "cultnet.login.v0":
      return [0, [
        decodeBase64Url(message.nonce, "LoginMessage.Nonce"),
        decodeBase64Url(message.auth, "LoginMessage.Auth"),
        decodeBase64Url(message.password, "LoginMessage.Password"),
      ]];
    case "cultnet.register.v0":
      return [1, [
        decodeBase64Url(message.nonce, "RegisterMessage.Nonce"),
        decodeBase64Url(message.email, "RegisterMessage.Email"),
        decodeBase64Url(message.password, "RegisterMessage.Password"),
        decodeBase64Url(message.name, "RegisterMessage.Name"),
      ]];
    case "cultnet.verify.v0":
      return [2, [
        decodeBase64Url(message.nonce, "VerifyMessage.Nonce"),
        decodeBase64Url(message.session, "VerifyMessage.Session"),
      ]];
    case "cultnet.login_success.v0":
      return [3, [
        decodeBase64Url(message.nonce, "LoginSuccessMessage.Nonce"),
        decodeBase64Url(message.session, "LoginSuccessMessage.Session"),
      ]];
    case "cultnet.error.v0":
      return [4, [message.error]];
    case "cultnet.sample.change_name.v0":
      return [5, [message.name]];
    case "cultnet.sample.chat.v0":
      return [6, [message.text]];
    case "cultnet.schema_catalog_request.v0":
      return [7, [
        message.messageId,
        message.includeSchemaJson ?? false,
        message.schemaIds ?? null,
        message.kinds ?? null,
      ]];
    case "cultnet.schema_catalog_response.v0":
      return [8, [
        message.messageId,
        message.schemas.map((schema) => encodeLegacySchemaDescriptor(schema)),
      ]];
    case "cultnet.document_put_raw.v0":
    case "cultnet.snapshot_response_raw.v0":
      throw new Error(
        `Message "${message.schemaVersion}" is not defined in the gamecult.networking.v0 contract.`,
      );
    default:
      throw new Error(
        `Message "${message.schemaVersion}" is not defined in the gamecult.networking.v0 contract.`,
      );
  }
}

function requireLegacyBytes(input: unknown, fieldName: string): string {
  if (!(input instanceof Uint8Array)) {
    throw new Error(`${fieldName} must be binary data in gamecult.networking.v0.`);
  }

  return encodeBase64Url(input);
}

function requireLegacyString(input: unknown, fieldName: string): string {
  if (typeof input !== "string") {
    throw new Error(`${fieldName} must be a string in gamecult.networking.v0.`);
  }

  return input;
}

function requireLegacyBoolean(input: unknown, fieldName: string): boolean {
  if (typeof input !== "boolean") {
    throw new Error(`${fieldName} must be a boolean in gamecult.networking.v0.`);
  }

  return input;
}

function requireLegacyOptionalStringArray(input: unknown, fieldName: string): string[] | undefined {
  if (input === null || typeof input === "undefined") {
    return undefined;
  }

  if (!Array.isArray(input) || input.some((item) => typeof item !== "string")) {
    throw new Error(`${fieldName} must be an array of strings in gamecult.networking.v0.`);
  }

  return input;
}

function requireLegacyOptionalSchemaKindArray(input: unknown, fieldName: string): CultNetSchemaKind[] | undefined {
  const values = requireLegacyOptionalStringArray(input, fieldName);

  if (!values) {
    return undefined;
  }

  for (const value of values) {
    if (value !== "wire_message" && value !== "document_payload" && value !== "shared_contract") {
      throw new Error(`${fieldName} has unsupported schema kind "${value}".`);
    }
  }

  return values as CultNetSchemaKind[];
}

function requireLegacySchemaDescriptorArray(input: unknown, fieldName: string): CultNetSchemaDescriptor[] {
  if (!Array.isArray(input)) {
    throw new Error(`${fieldName} must be an array in gamecult.networking.v0.`);
  }

  return input.map((value, index) => requireLegacySchemaDescriptor(value, `${fieldName}[${index}]`));
}

function requireLegacySchemaDescriptor(input: unknown, fieldName: string): CultNetSchemaDescriptor {
  if (!input || typeof input !== "object") {
    throw new Error(`${fieldName} must be an object in gamecult.networking.v0.`);
  }

  const candidate = input as Record<string, unknown>;
  const schemaId = requireLegacyString(candidate.schemaId, `${fieldName}.SchemaId`);
  const kind = requireLegacyString(candidate.kind, `${fieldName}.Kind`);
  const wireContracts = requireLegacyOptionalStringArray(candidate.wireContracts, `${fieldName}.WireContracts`) ?? [];
  const contentHash = requireLegacyString(candidate.contentHash, `${fieldName}.ContentHash`);
  const schemaVersion = requireLegacyOptionalString(candidate.schemaVersion, `${fieldName}.SchemaVersion`);
  const documentType = requireLegacyOptionalString(candidate.documentType, `${fieldName}.DocumentType`);
  const title = requireLegacyOptionalString(candidate.title, `${fieldName}.Title`);
  const schemaJson = requireLegacyOptionalString(candidate.schemaJson, `${fieldName}.SchemaJson`);

  if (kind !== "wire_message" && kind !== "document_payload" && kind !== "shared_contract") {
    throw new Error(`${fieldName}.Kind has unsupported schema kind "${kind}".`);
  }

  const normalizedWireContracts = wireContracts.map((value) => {
    if (value !== "cultnet.schema.v0" && value !== "gamecult.networking.v0") {
      throw new Error(`${fieldName}.WireContracts contains unsupported wire contract "${value}".`);
    }

    return value;
  });

  return {
    schemaId,
    kind,
    schemaVersion,
    documentType,
    title,
    wireContracts: normalizedWireContracts as CultNetWireContract[],
    contentHash,
    schemaJson,
  };
}

function requireLegacyOptionalString(input: unknown, fieldName: string): string | undefined {
  if (input === null || typeof input === "undefined") {
    return undefined;
  }

  return requireLegacyString(input, fieldName);
}

function encodeLegacySchemaDescriptor(schema: CultNetSchemaDescriptor): Record<string, unknown> {
  return {
    schemaId: schema.schemaId,
    kind: schema.kind,
    schemaVersion: schema.schemaVersion ?? null,
    documentType: schema.documentType ?? null,
    title: schema.title ?? null,
    wireContracts: schema.wireContracts,
    contentHash: schema.contentHash,
    schemaJson: schema.schemaJson ?? null,
  };
}

function encodeBase64Url(input: Uint8Array): string {
  return Buffer.from(input)
    .toString("base64")
    .replace(/\+/g, "-")
    .replace(/\//g, "_")
    .replace(/=+$/u, "");
}

function decodeBase64Url(input: string, fieldName: string): Uint8Array {
  if (!input || input.trim().length === 0) {
    throw new Error(`${fieldName} must be a non-empty base64url string.`);
  }

  const normalized = input
    .replace(/-/g, "+")
    .replace(/_/g, "/");
  const remainder = normalized.length % 4;
  const padded = remainder === 0
    ? normalized
    : normalized + "=".repeat(4 - remainder);

  return Buffer.from(padded, "base64");
}

function validateDocumentPutRawMessage(input: unknown): asserts input is CultNetDocumentPutRawMessage {
  const candidate = validateBinaryMessageRoot(input, "cultnet.document_put_raw.v0");
  if (!("document" in candidate)) {
    throw new Error("Validation failed for cultnet.document_put_raw.v0: /document: is required");
  }

  validateRawDocumentRecord(candidate.document, "/document");
}

function validateSnapshotResponseRawMessage(
  input: unknown,
): asserts input is CultNetSnapshotResponseRawMessage {
  const candidate = validateBinaryMessageRoot(input, "cultnet.snapshot_response_raw.v0");
  if (!("documents" in candidate) || !Array.isArray(candidate.documents)) {
    throw new Error("Validation failed for cultnet.snapshot_response_raw.v0: /documents: must be an array");
  }

  candidate.documents.forEach((document, index) => {
    validateRawDocumentRecord(document, `/documents/${index}`);
  });
}

function validateBinaryMessageRoot(
  input: unknown,
  schemaVersion: "cultnet.document_put_raw.v0" | "cultnet.snapshot_response_raw.v0",
): Record<string, unknown> {
  if (!input || typeof input !== "object") {
    throw new Error(`Validation failed for ${schemaVersion}: /: must be an object`);
  }

  const candidate = input as Record<string, unknown>;
  if (candidate.schemaVersion !== schemaVersion) {
    throw new Error(`Validation failed for ${schemaVersion}: /schemaVersion: must equal ${schemaVersion}`);
  }
  if (typeof candidate.messageId !== "string" || candidate.messageId.trim().length === 0) {
    throw new Error(`Validation failed for ${schemaVersion}: /messageId: must be a non-empty string`);
  }

  return candidate;
}

function validateRawDocumentRecord(
  input: unknown,
  instancePath: string,
): asserts input is CultNetRawDocumentRecord {
  if (!input || typeof input !== "object") {
    throw new Error(`Validation failed for raw CultNet document record: ${instancePath}: must be an object`);
  }

  const candidate = input as Record<string, unknown>;
  requireNonEmptyString(candidate.schemaId, `${instancePath}/schemaId`);
  requireNonEmptyString(candidate.recordKey, `${instancePath}/recordKey`);
  requireNonEmptyString(candidate.storedAt, `${instancePath}/storedAt`);
  if (candidate.payloadEncoding !== "messagepack") {
    throw new Error(
      `Validation failed for raw CultNet document record: ${instancePath}/payloadEncoding: must equal messagepack`,
    );
  }
  if (!(candidate.payload instanceof Uint8Array) || candidate.payload.length === 0) {
    throw new Error(
      `Validation failed for raw CultNet document record: ${instancePath}/payload: must be a non-empty Uint8Array`,
    );
  }
  requireOptionalNonEmptyString(candidate.sourceRuntimeId, `${instancePath}/sourceRuntimeId`);
  requireOptionalNonEmptyString(candidate.sourceAgentId, `${instancePath}/sourceAgentId`);
  requireOptionalNonEmptyString(candidate.sourceRole, `${instancePath}/sourceRole`);
  requireOptionalStringArray(candidate.tags, `${instancePath}/tags`);
}

function requireNonEmptyString(input: unknown, instancePath: string): void {
  if (typeof input !== "string" || input.trim().length === 0) {
    throw new Error(`Validation failed for raw CultNet document record: ${instancePath}: must be a non-empty string`);
  }
}

function requireOptionalNonEmptyString(input: unknown, instancePath: string): void {
  if (typeof input === "undefined" || input === null) {
    return;
  }
  requireNonEmptyString(input, instancePath);
}

function requireOptionalStringArray(input: unknown, instancePath: string): void {
  if (typeof input === "undefined" || input === null) {
    return;
  }
  if (!Array.isArray(input) || input.some((item) => typeof item !== "string" || item.trim().length === 0)) {
    throw new Error(`Validation failed for raw CultNet document record: ${instancePath}: must be an array of non-empty strings`);
  }
}

function normalizeCultNetOptionalNulls(
  candidate: Record<string, unknown>,
  schemaVersion: CultNetSchemaVersion,
): void {
  switch (schemaVersion) {
    case "cultnet.hello.v0":
      stripNullProperties(candidate, [
        "agentId",
        "role",
        "displayName",
        "supportedDocumentTypes",
        "supportedMutationContracts",
        "supportedMessageVersions",
      ]);
      return;
    case "cultnet.snapshot_request.v0":
      stripNullProperties(candidate, ["schemaIds", "recordKeys"]);
      return;
    case "cultnet.schema_catalog_request.v0":
      stripNullProperties(candidate, ["schemaIds", "kinds"]);
      return;
    default:
      return;
  }
}

function stripNullProperties(candidate: Record<string, unknown>, keys: readonly string[]): void {
  for (const key of keys) {
    if (candidate[key] === null) {
      delete candidate[key];
    }
  }
}

export const cultNetSchemas = {
  helloSchema,
  loginSchema,
  registerSchema,
  verifySchema,
  loginSuccessSchema,
  errorSchema,
  sampleChangeNameSchema,
  sampleChatSchema,
  documentPutSchema,
  documentDeleteSchema,
  rawDocumentRecordSchema,
  documentPutRawSchema,
  documentMutationContractSchema,
  snapshotRequestSchema,
  snapshotResponseSchema,
  snapshotResponseRawSchema,
  schemaDescriptorSchema,
  schemaCatalogRequestSchema,
  schemaCatalogResponseSchema,
  ghostlightAgentStateSchema,
} as const;

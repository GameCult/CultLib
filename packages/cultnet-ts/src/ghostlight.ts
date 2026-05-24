import type { CultNetDocumentBinding } from "./replication";
import { validateGhostlightAgentState, type GhostlightAgentStateDocument } from "./contracts";

export function validateGhostlightAgentStateDocument(
  value: unknown,
): GhostlightAgentStateDocument {
  return validateGhostlightAgentState(value);
}

export function resolveGhostlightAgentIds(
  document: GhostlightAgentStateDocument,
): string[] {
  return document.agents
    .map((agent) => agent.agent_id)
    .filter((value): value is string => typeof value === "string" && value.length > 0);
}

export function createGhostlightPayloadSchemaVersionResolver(): (
  value: GhostlightAgentStateDocument,
) => string {
  return (value) => validateGhostlightAgentState(value).schema_version;
}

export function isGhostlightAgentStateBinding(
  binding: CultNetDocumentBinding,
): boolean {
  return binding.payloadSchemaVersion === "ghostlight.agent_state.v0";
}

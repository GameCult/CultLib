// cultnet.selection.v1 payload shapes (docs/cultnet-selection-cut.md, section 2), the carriers' common
// vocabulary: one typed selection, the header/edge rows a page answers with, and the door refusal.
// The messages that carry them live in contracts.ts; the nil-keyed wire encoding, null normalization and
// response validation live in selection-carriers.ts.

export type CultNetSelectionOperator = "any_of" | "lt" | "le" | "ge" | "gt";
export type CultNetSelectionProjection = "header" | "document";

/** One predicate over a single declared index alias. `values` iff any_of; `number` (canonical decimal string) iff a comparison. */
export interface CultNetFieldPredicate {
  index: string;
  op: CultNetSelectionOperator;
  values?: string[];
  number?: string;
}

/** Identifies one row on the wire by schema and record key. */
export interface CultNetRecordRef {
  schemaId: string;
  recordKey: string;
}

/** Selects rows whose declared reference names `target`; an absent `role` matches any declared reference. */
export interface CultNetCitation {
  target: CultNetRecordRef;
  role?: string;
}

/** Selects rows some other row does (`exists`) or does not name in `role`. */
export interface CultNetIncoming {
  role: string;
  exists: boolean;
}

/** Absent `schemas`/`keys` mean every schema/key; a present empty list is refused at the door. */
export interface CultNetSelection {
  schemas?: string[];
  keys?: string[];
  fields?: CultNetFieldPredicate[];
  cites?: CultNetCitation;
  cited?: CultNetIncoming;
  /** Default "header". */
  projection?: CultNetSelectionProjection;
  descending?: boolean;
  /** Clamped to 1..=200 by the answering server. */
  limit?: number;
  /** Opaque; minted by the answering server. */
  cursor?: string;
}

/** A raw document record minus its payload and payloadEncoding. */
export interface CultNetRawDocumentHeader {
  schemaId: string;
  schemaName?: string;
  schemaVersion?: string;
  schemaContentHash?: string;
  recordKey: string;
  storedAt: string;
  sourceRuntimeId?: string;
  sourceAgentId?: string;
  sourceRole?: string;
  tags?: string[];
}

/** One edge a hop traversed. `payload` is present under document projection only. */
export interface CultNetEdge {
  from: CultNetRecordRef;
  role: string;
  to: CultNetRecordRef;
  payloadEncoding?: "messagepack";
  payload?: Uint8Array;
}

/**
 * Refused typed at the door: a selection the declared shape cannot support. Carried back as
 * `cultnet.error.v0 { code: "selection_invalid", details: { field, value } }`.
 */
export class CultNetSelectionInvalidError extends Error {
  constructor(
    readonly field: string,
    readonly value: string | null,
    message: string,
  ) {
    super(message);
    this.name = "CultNetSelectionInvalidError";
  }
}

/**
 * The declaration-independent half of the door, matching the reference's `ValidateShape`: an empty or
 * blank `schemas`/`keys` entry or list, and an unrecognised `projection`. Checks that need the
 * declared schemas (index aliases, reference roles, cites target) belong to the selection engine.
 */
export function validateSelectionShape(selection: CultNetSelection): void {
  const list = (field: "schemas" | "keys") => {
    const entries = selection[field];
    if (entries === undefined || entries === null) return;
    if (entries.length === 0) {
      throw new CultNetSelectionInvalidError(
        field, null, `selection.${field} is present and empty; omit it to reach every ${field === "schemas" ? "schema" : "key"}.`);
    }
    for (const entry of entries) {
      if (typeof entry !== "string" || entry.trim().length === 0) {
        throw new CultNetSelectionInvalidError(field, entry, `selection.${field} carries an empty or whitespace entry.`);
      }
    }
  };
  list("schemas");
  list("keys");
  const projection = selection.projection ?? "header";
  if (projection !== "header" && projection !== "document") {
    throw new CultNetSelectionInvalidError(
      "projection", String(projection), `selection.projection "${String(projection)}" is neither "header" nor "document".`);
  }
}

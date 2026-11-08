// The client half of the write receipt (contracts/cultnet/interop/write-receipt.md, "Client rules"): a pure state
// machine with an abstract clock. It owns exactly one decision, how each sent write settles. Transport, timers and
// frame decoding belong to the caller; the replay runner (tools/replay-write-receipt-vectors.mjs) drives this same
// interface from write-receipt-vectors.json.

import type { CultNetErrorMessage, CultNetErrorV1Message, CultNetMessage } from "./contracts";

/** The result window against a server whose hello lists both receipt frames. */
export const RECEIPT_RESULT_WINDOW_MS = 5000;
/** The result window against a server whose hello does not list them (or that sent none). */
export const LEGACY_RESULT_WINDOW_MS = 500;

export function resultWindowMs(serverAdvertisesReceipts: boolean): number {
  return serverAdvertisesReceipts ? RECEIPT_RESULT_WINDOW_MS : LEGACY_RESULT_WINDOW_MS;
}

/**
 * Why nothing settled a write: its window ran out, the session ended first, or the client's own send of it failed (it may or
 * may not have left the machine, and is never a refusal).
 */
export type WriteUnconfirmedReason = "window_elapsed" | "session_ended" | "local_send_failed";
/** What a late answer is recorded as on a write that already settled unconfirmed. */
export type WriteRecorded = "admitted_late" | "refused_late";

/** What the caller was told. Settled once; never changed by a later frame. */
export type WriteSettlement =
  | { outcome: "admitted" }
  | { outcome: "refused"; code: string | null; error: string; response: CultNetErrorMessage | CultNetErrorV1Message }
  | { outcome: "unconfirmed"; reason: WriteUnconfirmedReason };

/** A write's state: unanswered, or its settlement plus (after unconfirmed only) the late answer. */
export type WriteState =
  | { outcome: "pending"; recorded?: undefined }
  | (WriteSettlement & { recorded?: WriteRecorded });

export interface WriteTrackerOptions {
  /** The result window, from the send. Use `resultWindowMs(serverAdvertisesReceipts)`. */
  windowMs: number;
  /** Called once per write, at the moment it settles. */
  onSettle?: (messageId: string, settlement: WriteSettlement) => void;
}

interface Entry {
  sentAtMs: number;
  state: WriteState;
}

export class WriteTracker {
  readonly #windowMs: number;
  readonly #onSettle?: (messageId: string, settlement: WriteSettlement) => void;
  readonly #writes = new Map<string, Entry>();

  constructor(options: WriteTrackerOptions) {
    this.#windowMs = options.windowMs;
    this.#onSettle = options.onSettle;
  }

  /** Registers a write sent at `atMs`. A messageId is minted once per session: re-sending a known id is the same write. */
  send(messageId: string, atMs: number): void {
    this.tick(atMs);
    if (!this.#writes.has(messageId)) this.#writes.set(messageId, { sentAtMs: atMs, state: { outcome: "pending" } });
  }

  /** Feeds one decoded frame received at `atMs`. Frames that are not answers to a write are ignored. */
  frame(message: CultNetMessage, atMs: number): void {
    this.tick(atMs);
    switch (message.schemaVersion) {
      case "cultnet.write_admitted.v0":
        this.#answer(message.messageId, { outcome: "admitted" }, "admitted_late");
        return;
      case "cultnet.error.v1":
        if (message.messageId !== null) {
          this.#answer(message.messageId, refusalOf(message), "refused_late");
          return;
        }
        break;
      case "cultnet.error.v0":
        break;
      default:
        return;
    }
    // An error that names no write is attributable only when exactly one write is unanswered.
    const unanswered = [...this.#writes].filter(([, entry]) => entry.state.outcome === "pending");
    if (unanswered.length === 1) this.#settle(unanswered[0][0], unanswered[0][1], refusalOf(message));
  }

  /** Advances the clock: every unanswered write whose window, counted from its send, has run settles unconfirmed. */
  tick(atMs: number): void {
    for (const [messageId, entry] of this.#writes) {
      if (entry.state.outcome === "pending" && atMs - entry.sentAtMs >= this.#windowMs) {
        this.#settle(messageId, entry, { outcome: "unconfirmed", reason: "window_elapsed" });
      }
    }
  }

  /** The session ended: every unanswered write settles unconfirmed. */
  sessionEnd(atMs: number): void {
    this.tick(atMs);
    for (const [messageId, entry] of this.#writes) {
      if (entry.state.outcome === "pending") this.#settle(messageId, entry, { outcome: "unconfirmed", reason: "session_ended" });
    }
  }

  /** This client's own send of `messageId` failed: that write settles unconfirmed; the others are untouched. */
  sendFailed(messageId: string, atMs: number): void {
    const entry = this.#writes.get(messageId);
    if (entry?.state.outcome === "pending") this.#settle(messageId, entry, { outcome: "unconfirmed", reason: "local_send_failed" });
    this.tick(atMs);
  }

  state(messageId: string): WriteState | undefined {
    return this.#writes.get(messageId)?.state;
  }

  /** Milliseconds from `atMs` until the next unanswered write's window runs out; undefined when none is unanswered. */
  nextDeadlineMs(atMs: number): number | undefined {
    let next: number | undefined;
    for (const entry of this.#writes.values()) {
      if (entry.state.outcome !== "pending") continue;
      const remaining = Math.max(0, entry.sentAtMs + this.#windowMs - atMs);
      if (next === undefined || remaining < next) next = remaining;
    }
    return next;
  }

  #answer(messageId: string, settlement: WriteSettlement, late: WriteRecorded): void {
    const entry = this.#writes.get(messageId);
    if (!entry) return; // names a write that is not in flight: settles nothing
    if (entry.state.outcome === "pending") {
      this.#settle(messageId, entry, settlement);
    } else if (entry.state.outcome === "unconfirmed" && entry.state.recorded === undefined) {
      entry.state = { ...entry.state, recorded: late };
    }
  }

  #settle(messageId: string, entry: Entry, settlement: WriteSettlement): void {
    entry.state = settlement;
    this.#onSettle?.(messageId, settlement);
  }
}

function refusalOf(message: CultNetErrorMessage | CultNetErrorV1Message): WriteSettlement {
  return { outcome: "refused", code: message.code, error: message.error, response: message };
}

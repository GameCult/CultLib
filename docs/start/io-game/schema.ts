import { defineDocumentType } from "@gamecult/cultcache-ts/document";

export interface Player { x: number; y: number }
export interface ArenaState { players: Record<string, Player> }

// The one shared document: every player's position. The server owns it; clients lease it.
export const arenaDocument = defineDocumentType({
  type: "io.arena_state",
  schemaId: "io.arena_state.v1",
  schema: { parse: (value: unknown) => value as ArenaState },
});

export const arenaKey = "arena:main";
export const verseId = "io.arena";
export const authorityRuntimeId = "io.arena-daemon";
export const arenaSize = 100;

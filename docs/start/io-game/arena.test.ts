import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { joinArena } from "./client.ts";
import { startArena } from "./server.ts";
import { arenaSize, type ArenaState } from "./schema.ts";

const until = async (cond: () => boolean) => {
  for (let i = 0; i < 200 && !cond(); i++) await new Promise(r => setTimeout(r, 10));
  assert.ok(cond(), "condition not reached in 2s");
};

test("two clients join one arena, see each other, and see each other move", async () => {
  const arena = await startArena();
  const seenByA: ArenaState[] = [];
  const seenByB: ArenaState[] = [];
  const a = await joinArena(arena.odinUrl, "client-a", s => seenByA.push(structuredClone(s)));
  const b = await joinArena(arena.odinUrl, "client-b", s => seenByB.push(structuredClone(s)));
  try {
    assert.notEqual(a.id, b.id);
    await until(() => Object.keys(a.state()?.players ?? {}).length === 2);
    await until(() => Object.keys(b.state()?.players ?? {}).length === 2);

    const before = structuredClone(b.state()!.players[a.id]!);
    // Moves clamp at the arena edge, so pick a direction that always has room to move.
    const dx = before.x > 0 ? -1 : 1;
    await a.move(dx, 0);
    await until(() => b.state()!.players[a.id]!.x !== before.x);
    assert.equal(b.state()!.players[a.id]!.x, before.x + dx);
    assert.deepEqual(a.state(), arena.state());
    assert.deepEqual(b.state(), arena.state());

    await b.leave();
    await until(() => !(a.id !== b.id && b.id in a.state()!.players));
    assert.deepEqual(Object.keys(a.state()!.players), [a.id]);
  } finally {
    await a.leave().catch(() => undefined);
    await arena.close();
  }
});

test("a player cannot walk out of the arena", async () => {
  const arena = await startArena();
  const a = await joinArena(arena.odinUrl, "client-a", () => undefined);
  try {
    for (let i = 0; i <= arenaSize; i++) await a.move(-1, 0);
    assert.equal(arena.state().players[a.id]!.x, 0);
  } finally {
    await a.leave();
    await arena.close();
  }
});

test("serve.ts bundles the browser client and serves the page", async () => {
  const { serveArena } = await import("./serve.ts");
  const site = await serveArena(0);
  try {
    const html = await (await fetch(site.url)).text();
    assert.match(html, /<canvas/);
    const js = await (await fetch(`${site.url}/browser.js`)).text();
    assert.match(js, /CultMeshBrowserClient/);
  } finally {
    await site.close();
  }
});

test("the recipe page shows the source files verbatim", () => {
  const page = readFileSync(new URL("../io-game.md", import.meta.url), "utf8").replace(/\r\n/g, "\n");
  for (const file of ["schema.ts", "server.ts", "client.ts"]) {
    const text = readFileSync(new URL(`./${file}`, import.meta.url), "utf8").replace(/\r\n/g, "\n");
    assert.ok(page.includes(text), `docs/start/io-game.md no longer shows ${file} verbatim`);
  }
});

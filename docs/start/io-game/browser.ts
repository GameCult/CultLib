import { joinArena } from "./client.ts";
import { arenaSize, type ArenaState } from "./schema.ts";

const canvas = document.querySelector("canvas")!;
const ctx = canvas.getContext("2d")!;
const scale = canvas.width / arenaSize;

const game = await joinArena(`ws://${location.host}/odin`, `browser-${crypto.randomUUID()}`, (state: ArenaState, me: string) => {
  ctx.clearRect(0, 0, canvas.width, canvas.height);
  for (const [id, p] of Object.entries(state.players)) {
    ctx.fillStyle = id === me ? "#f5c542" : "#6aa9ff";
    ctx.fillRect(p.x * scale, p.y * scale, scale * 2, scale * 2);
  }
});
const keys: Record<string, [number, number]> = { ArrowLeft: [-1, 0], ArrowRight: [1, 0], ArrowUp: [0, -1], ArrowDown: [0, 1] };
addEventListener("keydown", e => { const d = keys[e.key]; if (d) void game.move(...d); });
addEventListener("pagehide", () => void game.leave());

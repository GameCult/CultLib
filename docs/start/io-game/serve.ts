import { readFileSync } from "node:fs";
import { build } from "esbuild";
import { startArena } from "./server.ts";

// Serves the page, the bundled browser client, and the arena's WebSocket on one port.
export async function serveArena(port = 8080, host = "127.0.0.1") {
  const bundle = await build({ entryPoints: [new URL("./browser.ts", import.meta.url).pathname], bundle: true, format: "esm", target: "es2022", write: false });
  const html = readFileSync(new URL("./index.html", import.meta.url));
  const arena = await startArena(port, host, (req, res) => {
    if (req.url === "/") res.writeHead(200, { "content-type": "text/html" }).end(html);
    else if (req.url === "/browser.js") res.writeHead(200, { "content-type": "text/javascript" }).end(bundle.outputFiles[0]!.contents);
    else res.writeHead(404).end();
  });
  return { url: arena.odinUrl.replace("ws:", "http:").replace("/odin", ""), close: arena.close };
}

if (import.meta.main) console.log(`arena at ${(await serveArena()).url}`);

// The TypeScript content provider in its own process, for content-https.test.ts. Argv: <store dir> <corrupt store dir> <key pem> <cert pem> <escape record key>.
// Protocol, stdout JSON lines only: ready, one line per request ({phase, mount, url}), one per server fault ({phase, mount, fault}).
// Stdin: "phase <name>" sets the phase and is answered {ack}; stdin ending closes the server and exits, so no orchestrator crash orphans it.
import { readFileSync } from "node:fs";
import { createServer } from "node:https";
import type { AddressInfo } from "node:net";
import { createInterface } from "node:readline";
import { chunkRecordKey } from "cultnet-ts/content";
import { createContentChunkHandler, createDirectoryChunkStore, toNodeListener, type ContentChunkAnswer, type ContentChunkRequest } from "cultmesh-ts/content";

const [storeDir, corruptDir, keyPem, certPem, escapeKey] = process.argv.slice(2) as [string, string, string, string, string];
const say = (line: object): void => { process.stdout.write(`${JSON.stringify(line)}\n`); };
let phase = "start";

const handlerFor = (mount: string, dir: string) =>
  createContentChunkHandler(createDirectoryChunkStore(dir), { onServerFault: fault => say({ phase, mount, fault: fault.kind }) });

const honest = handlerFor("honest", storeDir);
// In flight, after the provider verified the bytes: a 200 with a body comes back with byte 0 flipped and the headers unchanged.
const tampered = async (request: ContentChunkRequest): Promise<ContentChunkAnswer> => {
  const answer = await honest(request);
  if (answer.status !== 200 || !answer.body?.length) return answer;
  const body = answer.body.slice();
  body[0]! ^= 1;
  return { ...answer, body };
};
// The one non-canonical record key the fixtures carry (escapeKey) is answered as its canonical key, so the real handler serves the chunk
// and the request line above, logged before this runs, is what each consumer actually sent. Every other recordKey reaches the handler untouched.
const honestEscape = async (request: ContentChunkRequest): Promise<ContentChunkAnswer> => {
  const url = new URL(request.url, "http://provider.invalid");
  if (url.searchParams.get("recordKey") !== escapeKey) return honest(request);
  url.searchParams.set("recordKey", chunkRecordKey(url.searchParams.get("chunkHash")));
  return honest({ ...request, url: `${url.pathname}${url.search}` });
};
const mounts = { honest: toNodeListener(honestEscape), tampered: toNodeListener(tampered), corrupt: toNodeListener(handlerFor("corrupt", corruptDir)) };

const server = createServer({ key: readFileSync(keyPem), cert: readFileSync(certPem) }, (req, res) => {
  const mount = /^\/(honest|tampered|corrupt)\//.exec(req.url ?? "")?.[1] as keyof typeof mounts | undefined;
  if (!mount) { res.writeHead(404).end(); return; }
  say({ phase, mount, url: req.url });
  mounts[mount](req, res);
});

await new Promise<void>(resolve => server.listen(0, "127.0.0.1", resolve));
const port = (server.address() as AddressInfo).port;
say({ status: "ready", endpoints: Object.fromEntries(Object.keys(mounts).map(mount => [mount, `https://127.0.0.1:${port}/${mount}/content`])) });

createInterface({ input: process.stdin }).on("line", line => {
  const name = /^phase (.+)$/.exec(line)?.[1];
  if (name) { phase = name; say({ ack: name }); }
}).on("close", () => { server.closeAllConnections(); server.close(() => process.exit(0)); });

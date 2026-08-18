import assert from "node:assert/strict";
import { spawn } from "node:child_process";
import { existsSync } from "node:fs";
import { createServer as createHttpServer } from "node:http";
import { mkdtemp, readFile, rm } from "node:fs/promises";
import { createServer as createNetServer } from "node:net";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { build } from "esbuild";
import { chromium } from "playwright-core";

const repoRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const eveFlag = process.argv.indexOf("--eve-root");
const eveRoot = resolve(eveFlag >= 0 ? process.argv[eveFlag + 1] : join(repoRoot, "..", "Eve"));
const sampleRoot = join(repoRoot, "samples", "eve-browser-network");
const workRoot = await mkdtemp(join(tmpdir(), "cultmesh-browser-network-"));
const statePath = join(workRoot, "counter.cc");
const bundlePath = join(workRoot, "bundle.js");
const token = "sample-session";
let provider;
let replacementProvider;
let odin;
let headless;
let httpServer;
let browser;

try {
  const tsc = join(repoRoot, "node_modules", "typescript", "bin", "tsc");
  await run(process.execPath, [tsc, "-p", join(repoRoot, "packages", "cultnet-ts", "tsconfig.json"), "--pretty", "false"]);
  await run(process.execPath, [tsc, "-p", join(repoRoot, "packages", "cultmesh-browser", "tsconfig.json"), "--pretty", "false"]);
  await build({
    entryPoints: [join(sampleRoot, "browser", "main.ts")],
    outfile: bundlePath,
    bundle: true,
    format: "esm",
    platform: "browser",
    target: "es2022",
    sourcemap: true,
    alias: {
      "@gamecult/eve-contracts": join(eveRoot, "packages", "eve-contracts", "src", "index.ts"),
      "@gamecult/eve-browser-lowering": join(eveRoot, "packages", "eve-browser-lowering", "src", "index.ts"),
    },
    logLevel: "warning",
  });
  const bundle = await readFile(bundlePath, "utf8");
  assert.doesNotMatch(bundle, /(?:from\s*["']node:|require\(["']node:)/u, "browser bundle contains a Node builtin import");

  await run("dotnet", [
    "build",
    join(sampleRoot, "EveBrowserNetworkSample.csproj"),
    "-m:1",
    "--verbosity", "quiet",
    "-p:NoWarn=1591%3BCS8632",
    `-p:EveRoot=${eveRoot}`,
  ]);
  const providerPort = await freePort();
  const replacementProviderPort = await freePort();
  const odinPort = await freePort();
  const httpPort = await freePort();
  provider = await startProvider(providerPort);
  const endpoint = await provider.waitFor("PROVIDER_READY ");
  odin = await startOdin(odinPort, endpoint);
  const odinEndpoint = await odin.waitFor("ODIN_READY ");
  headless = startDotnet([
    "headless", "--odin", odinEndpoint, "--verse-id", "sample.counter",
    "--expected-count", "2", "--token", token,
  ]);
  await headless.waitFor("HEADLESS_READY ");
  httpServer = await serve(httpPort, sampleRoot, bundlePath);

  browser = await chromium.launch({ executablePath: resolveChromiumExecutable(), headless: true });
  let page = await browser.newPage();
  const url = `http://127.0.0.1:${httpPort}/?odin=${encodeURIComponent(odinEndpoint)}&token=${token}`;
  await page.goto(url);
  await page.waitForFunction(() => window.__sampleReady || window.__sampleError);
  assert.equal(await page.evaluate(() => window.__sampleError), undefined);
  assert.equal(await page.evaluate(() => window.__sampleCount), 0);
  await page.locator("button").click();
  await page.waitForFunction(() => window.__sampleReceipt?.count === 1 && window.__sampleCount === 1);
  const firstReceipt = await page.evaluate(() => window.__sampleReceipt?.receiptId);
  const firstHeadlessUpdate = JSON.parse(await headless.waitFor("HEADLESS_UPDATE_1 "));
  assert.equal(firstHeadlessUpdate.count, 1);
  assert.ok(firstHeadlessUpdate.receiptIds.includes(firstReceipt));
  await page.locator("button").click();
  await page.waitForFunction(receipt => window.__sampleReceipt?.receiptId === receipt, firstReceipt);
  assert.equal(await page.evaluate(() => window.__sampleCount), 1, "duplicate idempotency key changed canonical state");

  replacementProvider = await startProvider(replacementProviderPort);
  const restartedEndpoint = await replacementProvider.waitFor("PROVIDER_READY ");
  assert.notEqual(restartedEndpoint, endpoint);
  await stop(odin.process);
  odin = await startOdin(odinPort, restartedEndpoint);
  assert.equal(await odin.waitFor("ODIN_READY "), odinEndpoint);
  await stop(provider.process);
  provider = replacementProvider;
  replacementProvider = undefined;
  await page.waitForFunction(() =>
    window.__sampleConnectionStates?.includes("reconnecting")
    && window.__sampleConnectionStates.at(-1) === "connected");
  headless.sendLine("INVOKE headless-command-2");
  const headlessReceipt = JSON.parse(await headless.waitFor("HEADLESS_RECEIPT "));
  assert.equal(headlessReceipt.status, "accepted");
  assert.equal(headlessReceipt.count, 2);
  await page.waitForFunction(() => window.__sampleCount === 2);
  const secondReceipt = headlessReceipt.receiptId;
  const secondHeadlessUpdate = JSON.parse(await headless.waitFor("HEADLESS_UPDATE_2 "));
  assert.equal(secondHeadlessUpdate.count, 2);
  assert.ok(secondHeadlessUpdate.receiptIds.includes(firstReceipt));
  assert.ok(secondHeadlessUpdate.receiptIds.includes(secondReceipt));
  const networkBenchmark = JSON.parse(await headless.waitFor("HEADLESS_NETWORK_BENCHMARK ", 60_000));
  assert.equal(networkBenchmark.operations, 10_000);
  assert.ok(networkBenchmark.p99Milliseconds < 250);
  assert.ok(networkBenchmark.managedHeapGrowth <= 8 * 1024 * 1024);
  const connectionStates = await page.evaluate(() => window.__sampleConnectionStates);
  assert.equal(connectionStates[0], "connected");
  assert.equal(connectionStates.at(-1), "connected");
  assert.ok(connectionStates.includes("reconnecting"));
  assert.ok(!connectionStates.includes("disposed"));

  console.log(JSON.stringify({
    providerEndpoint: endpoint,
    replacementProviderEndpoint: restartedEndpoint,
    odinEndpoint,
    browserRuntime: "chromium",
    headlessRuntime: "csharp",
    receiptId: firstReceipt,
    secondReceiptId: secondReceipt,
    count: 2,
    routeRotationCount: 1,
    retainedHeadlessLease: true,
    networkBenchmark,
  }));
} finally {
  if (browser) await browser.close().catch(() => undefined);
  if (headless) await stop(headless.process);
  if (provider) await stop(provider.process);
  if (replacementProvider) await stop(replacementProvider.process);
  if (odin) await stop(odin.process);
  if (httpServer) await new Promise(resolve => httpServer.close(resolve));
  await rm(workRoot, { recursive: true, force: true });
}

async function startProvider(port) {
  return startDotnet(["provider", "--port", String(port), "--state", statePath, "--token", token]);
}

async function startOdin(port, providerEndpoint) {
  return startDotnet([
    "odin",
    "--port", String(port),
    "--provider-endpoint", providerEndpoint,
    "--token", token,
  ]);
}

function startDotnet(arguments_) {
  const process = spawn("dotnet", [
    "run", "--no-build", "--project", join(sampleRoot, "EveBrowserNetworkSample.csproj"), "--", ...arguments_,
  ], { cwd: repoRoot, stdio: ["pipe", "pipe", "pipe"] });
  let output = "";
  let error = "";
  const waiters = [];
  process.stdout.setEncoding("utf8");
  process.stderr.setEncoding("utf8");
  process.stdout.on("data", chunk => { output += chunk; settle(); });
  process.stderr.on("data", chunk => { error += chunk; });
  process.on("exit", code => {
    for (const waiter of waiters.splice(0)) {
      clearTimeout(waiter.timer);
      waiter.reject(new Error(`dotnet sample exited ${code}\n${output}\n${error}`));
    }
  });
  function settle() {
    for (let index = waiters.length - 1; index >= 0; index--) {
      const waiter = waiters[index];
      const line = output.split(/\r?\n/u).find(value => value.startsWith(waiter.prefix));
      if (!line) continue;
      waiters.splice(index, 1);
      clearTimeout(waiter.timer);
      waiter.resolve(line.slice(waiter.prefix.length));
    }
  }
  return {
    process,
    sendLine(line) {
      process.stdin.write(`${line}\n`);
    },
    waitFor(prefix, timeoutMs = 30_000) {
      const existing = output.split(/\r?\n/u).find(value => value.startsWith(prefix));
      if (existing) return Promise.resolve(existing.slice(prefix.length));
      return new Promise((resolve, reject) => {
        const waiter = {
          prefix,
          resolve,
          reject,
          timer: setTimeout(() => {
            const index = waiters.indexOf(waiter);
            if (index >= 0) waiters.splice(index, 1);
            reject(new Error(`Timed out waiting for '${prefix}'.\n${output}\n${error}`));
          }, timeoutMs),
        };
        waiters.push(waiter);
      });
    },
  };
}

function run(command, arguments_) {
  return new Promise((resolvePromise, reject) => {
    const process = spawn(command, arguments_, { cwd: repoRoot, stdio: "inherit" });
    process.on("error", reject);
    process.on("exit", code => code === 0 ? resolvePromise() : reject(new Error(`${command} exited ${code}`)));
  });
}

async function serve(port, root, builtBundle) {
  const index = await readFile(join(root, "browser", "index.html"));
  const server = createHttpServer(async (request, response) => {
    if (request.url?.startsWith("/bundle.js")) {
      response.setHeader("Content-Type", "text/javascript; charset=utf-8");
      response.end(await readFile(builtBundle));
      return;
    }
    response.setHeader("Content-Type", "text/html; charset=utf-8");
    response.end(index);
  });
  await new Promise((resolvePromise, reject) => {
    server.once("error", reject);
    server.listen(port, "127.0.0.1", resolvePromise);
  });
  return server;
}

async function freePort() {
  const server = createNetServer();
  await new Promise((resolvePromise, reject) => {
    server.once("error", reject);
    server.listen(0, "127.0.0.1", resolvePromise);
  });
  const address = server.address();
  const port = typeof address === "object" && address ? address.port : 0;
  await new Promise(resolvePromise => server.close(resolvePromise));
  return port;
}

async function stop(process) {
  if (!process || process.exitCode !== null) return;
  process.kill();
  await Promise.race([
    new Promise(resolvePromise => process.once("exit", resolvePromise)),
    new Promise(resolvePromise => setTimeout(resolvePromise, 2_000)),
  ]);
  if (process.exitCode === null) process.kill("SIGKILL");
}

function resolveChromiumExecutable() {
  const candidates = [process.env.CHROME_PATH];
  if (process.platform === "win32") {
    for (const root of [process.env.ProgramFiles, process.env["ProgramFiles(x86)"], process.env.LOCALAPPDATA]) {
      if (!root) continue;
      candidates.push(
        join(root, "Google", "Chrome", "Application", "chrome.exe"),
        join(root, "Microsoft", "Edge", "Application", "msedge.exe"),
      );
    }
  } else if (process.platform === "darwin") {
    candidates.push(
      "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
      "/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge",
    );
  } else {
    candidates.push(
      "/usr/bin/google-chrome",
      "/usr/bin/google-chrome-stable",
      "/usr/bin/chromium",
      "/usr/bin/chromium-browser",
      "/opt/google/chrome/chrome",
    );
  }
  candidates.push(chromium.executablePath());
  const executablePath = candidates.find(candidate => candidate && existsSync(candidate));
  if (executablePath) return executablePath;
  throw new Error(
    "No Chromium-family browser was found. Set CHROME_PATH or run "
    + "'node node_modules/playwright-core/cli.js install chromium'.",
  );
}

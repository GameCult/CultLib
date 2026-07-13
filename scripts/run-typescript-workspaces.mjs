import { execFileSync } from "node:child_process";
import { dirname, join } from "node:path";

const command = process.argv[2];
if (!command) throw new Error("Usage: node scripts/run-typescript-workspaces.mjs <script>");

const npmCli = process.env.npm_execpath ??
  join(dirname(process.execPath), "node_modules", "npm", "bin", "npm-cli.js");
const workspaces = ["cultcache-ts", "cultnet-ts", "cultmesh-ts"];
const childEnv = process.platform === "win32"
  ? {
      ...process.env,
      PATH: [
        dirname(process.execPath),
        `${process.env.SystemRoot}\\System32`,
        process.env.SystemRoot,
        `${process.env.USERPROFILE}\\AppData\\Local\\Microsoft\\WindowsApps`,
        `${process.env.USERPROFILE}\\.cargo\\bin`,
        "C:\\Program Files\\dotnet",
        "C:\\Program Files\\Git\\cmd",
      ].join(";"),
    }
  : process.env;

for (const workspace of workspaces) {
  execFileSync(process.execPath, [npmCli, "--workspace", workspace, "run", command], {
    env: childEnv,
    stdio: "inherit",
  });
}

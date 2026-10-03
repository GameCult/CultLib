// How publish-packages.yml releases a package: the release-checks suite runs first, and the release
// action runs only if the suite passed, both from this one process. No workflow condition can then
// release what the suite did not pass. docs/semver-policy.md, "The suite gates every release".
//
//   node scripts/release-after-suite.mjs npm-publish <package dir>    npm publish --access public in <dir>
//   node scripts/release-after-suite.mjs python-build <package dir>   python -m build <dir>, for the PyPI step
import { spawnSync } from "node:child_process";
import { fileURLToPath, pathToFileURL } from "node:url";

export const suite = fileURLToPath(new URL("./check-changelog-semver.test.mjs", import.meta.url));

// Scoped npm packages default to restricted, and CultLib is MIT, so the publish says public.
export const actions = {
  "npm-publish": (dir) => ["npm", ["publish", "--access", "public"], dir],
  "python-build": (dir) => ["python", ["-m", "build", dir], undefined],
};

const spawn = (command, args, cwd) => spawnSync(command, args, { cwd, stdio: "inherit" }).status ?? 1;

// Returns the exit code. `run(command, args, cwd)` returns a command's exit code; tests pass their own.
export function releaseAfterSuite(argv, run = spawn) {
  const [action, dir, ...rest] = argv;
  if (!Object.hasOwn(actions, action ?? "") || !dir || rest.length > 0) {
    console.error(`usage: node scripts/release-after-suite.mjs <${Object.keys(actions).join("|")}> <package dir>`);
    return 2;
  }
  if (run(process.execPath, ["--test", suite], undefined) !== 0) {
    console.error("The release check suite failed (scripts/check-changelog-semver.test.mjs); not releasing.");
    return 1;
  }
  return run(...actions[action](dir));
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = releaseAfterSuite(process.argv.slice(2));
}

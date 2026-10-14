// How publish-packages.yml releases a package. From this one process, in order: the release tag must name
// the package's declared prefix and its manifest version, the release-checks suite must pass, the semver
// policy check must pass for that version, the package's own tests must pass, and only then the release
// action runs. No workflow condition can then release what any of those checks did not pass.
// docs/semver-policy.md, "The suite gates every release".
//
//   node scripts/release-after-suite.mjs npm-publish <package dir> <git ref>    npm publish --access public in <dir>
//   node scripts/release-after-suite.mjs python-build <package dir> <git ref>   python -m build <dir>, for the PyPI step
//
// <git ref> is the run's GITHUB_REF. A tag ref is a release and gets the tag and semver checks; npm-publish
// refuses any other ref. python-build on a branch ref builds without them, for the manual runs that publish
// nothing.
import { spawnSync } from "node:child_process";
import { readFileSync } from "node:fs";
import { join } from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import releasePackages from "./release-packages.mjs";

export const suite = fileURLToPath(new URL("./check-changelog-semver.test.mjs", import.meta.url));
export const checker = fileURLToPath(new URL("./check-changelog-semver.mjs", import.meta.url));
const repoRoot = fileURLToPath(new URL("..", import.meta.url));

// The checks run without the variables that can make `node --test` run nothing and exit 0
// (NODE_OPTIONS test filters, NODE_TEST_CONTEXT, any NODE_TEST_*), and without the publish token,
// which only the release action receives.
const UNCHECKED = /^(NODE_OPTIONS|NODE_TEST_.*|NODE_AUTH_TOKEN)$/i;
export const checkEnv = (env) => Object.fromEntries(Object.entries(env).filter(([key]) => !UNCHECKED.test(key)));

const pyprojectField = (text, field) =>
  text.split(/^\[/m).find((section) => section.startsWith("project]"))?.match(new RegExp(`^${field} = "([^"]+)"$`, "m"))?.[1];

// Scoped npm packages default to restricted, and CultLib is MIT, so the publish says public.
export const actions = {
  "npm-publish": {
    manifest: (dir) => JSON.parse(readFileSync(join(dir, "package.json"), "utf8")),
    test: (dir) => ["npm", ["test"], dir],
    release: (dir) => ["npm", ["publish", "--access", "public"], dir],
    tagOnly: true,
  },
  "python-build": {
    manifest: (dir) => {
      const text = readFileSync(join(dir, "pyproject.toml"), "utf8");
      return { name: pyprojectField(text, "name"), version: pyprojectField(text, "version") };
    },
    test: (dir) => ["python", ["-m", "unittest", "discover", "-s", join(dir, "tests")], undefined],
    release: (dir) => ["python", ["-m", "build", dir], undefined],
    tagOnly: false,
  },
};

const spawn = (command, args, cwd, env) => spawnSync(command, args, { cwd, env, stdio: "inherit" }).status ?? 1;

const refuse = (message) => {
  console.error(`${message}; not releasing.`);
  return 1;
};

// Returns the exit code. `run(command, args, cwd, env)` returns a command's exit code; tests pass their own.
export function releaseAfterSuite(argv, run = spawn, env = process.env) {
  const [name, dir, ref, ...rest] = argv;
  if (!Object.hasOwn(actions, name ?? "") || !dir || !ref || rest.length > 0) {
    console.error(`usage: node scripts/release-after-suite.mjs <${Object.keys(actions).join("|")}> <package dir> <git ref>`);
    return 2;
  }
  const action = actions[name];
  let manifest;
  try {
    manifest = action.manifest(dir);
  } catch {
    return refuse(`${dir} has no readable manifest`);
  }
  const entry = Object.hasOwn(releasePackages, manifest.name ?? "") ? releasePackages[manifest.name] : null;
  if (entry === null) return refuse(`${dir} is ${manifest.name}, which scripts/release-packages.mjs does not declare`);
  const isTag = ref.startsWith("refs/tags/");
  const tag = `refs/tags/${entry.tagPrefix}-v${manifest.version}`;
  if (isTag && ref !== tag) return refuse(`${ref} is not ${tag}, the tag of ${manifest.name}'s manifest version`);
  if (!isTag && action.tagOnly) return refuse(`${name} releases only from a release tag, and ${ref} is not one`);

  const clean = checkEnv(env);
  if (run(process.execPath, ["--test", suite], undefined, clean) !== 0) {
    return refuse("The release check suite failed (scripts/check-changelog-semver.test.mjs)");
  }
  if (isTag && run(process.execPath, [checker, "--package", manifest.name, "--version", manifest.version, "--cwd", repoRoot], undefined, clean) !== 0) {
    return refuse(`${manifest.name} ${manifest.version} fails the semver policy (docs/semver-policy.md)`);
  }
  if (run(...action.test(dir), clean) !== 0) return refuse(`${manifest.name}'s own tests failed`);
  return run(...action.release(dir), env);
}

if (process.argv[1] && import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.exitCode = releaseAfterSuite(process.argv.slice(2));
}

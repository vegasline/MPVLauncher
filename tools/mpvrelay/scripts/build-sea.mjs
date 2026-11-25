// Builds mpvrelay.exe: the whole helper bundled into one CommonJS file, then
// injected into a copy of node.exe with Node's single-executable support.
//
// The result is one file with no runtime beside it - the same shape the torrent
// helper ships in - so a machine without Node can still run the relay.
import { build } from "esbuild";
import { execFileSync } from "node:child_process";
import fs from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

const root = path.dirname(path.dirname(fileURLToPath(import.meta.url)));
const outDir = path.join(root, "build");
fs.mkdirSync(outDir, { recursive: true });

const bundlePath = path.join(outDir, "relay.cjs");
await build({
  entryPoints: [path.join(root, "src", "index.js")],
  bundle: true,
  platform: "node",
  target: "node22",
  format: "cjs",
  outfile: bundlePath,
  banner: { js: "// MPVLauncher browser relay. MIT licensed." },
});

const blobPath = path.join(outDir, "sea-prep.blob");
const seaConfigPath = path.join(outDir, "sea-config.json");
fs.writeFileSync(seaConfigPath, JSON.stringify({
  main: bundlePath,
  output: blobPath,
  disableExperimentalSEAWarning: true,
  useCodeCache: false,
  useSnapshot: false,
}, null, 2));

execFileSync(process.execPath, ["--experimental-sea-config", seaConfigPath], { stdio: "inherit" });

const exePath = path.join(outDir, "mpvrelay.exe");
fs.copyFileSync(process.execPath, exePath);

const postject = path.join(root, "node_modules", "postject", "dist", "cli.js");
execFileSync(process.execPath, [
  postject, exePath, "NODE_SEA_BLOB", blobPath,
  "--sentinel-fuse", "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2",
], { stdio: "inherit" });

console.log("built " + exePath);

import fs from "node:fs/promises";
import path from "node:path";
import { fileURLToPath } from "node:url";
import { execFileSync } from "node:child_process";
import { createHash } from "node:crypto";
import { build } from "esbuild";
import { inject } from "postject";

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
if (process.platform !== "win32" || process.arch !== "x64" || Number(process.versions.node.split('.')[0]) < 22) {
  throw new Error("Build on Windows x64 with Node.js 22 or later. The runtime binary must match the SEA blob.");
}
const pkg = JSON.parse(await fs.readFile(path.join(root, "package.json"), "utf8"));
const work = path.join(root, ".packaging");
const output = path.resolve(process.env.CAB_RELEASE_DIR ?? path.join(root, "release"));
await fs.mkdir(work, { recursive: true });
await fs.mkdir(output, { recursive: true });
const bundle = path.join(work, "bridge.cjs");
await build({
  entryPoints: [path.join(root, "src/packaging/entry.ts")], outfile: bundle,
  bundle: true, platform: "node", format: "cjs", target: "node22", sourcemap: false,
  external: ["bufferutil", "utf-8-validate"],
  define: { CAB_BUILD_VERSION: JSON.stringify(pkg.version) },
  logLevel: "info",
});
const blob = path.join(work, "bridge.blob");
const config = path.join(work, "sea-config.json");
await fs.writeFile(config, JSON.stringify({ main: bundle, output: blob, disableExperimentalSEAWarning: true, useSnapshot: false, useCodeCache: false }));
execFileSync(process.execPath, ["--experimental-sea-config", config], { stdio: "inherit" });
const exe = path.join(output, "ChatAgentBridge.exe");
await fs.copyFile(process.execPath, exe);
await inject(exe, "NODE_SEA_BLOB", await fs.readFile(blob), { sentinelFuse: "NODE_SEA_FUSE_fce680ab2cc467b6e072b8b5df1996b2" });
const hash = createHash("sha256").update(await fs.readFile(exe)).digest("hex");
await fs.writeFile(path.join(output, "SHA256SUMS.txt"), `${hash}  ChatAgentBridge.exe\n`);
await fs.copyFile(path.join(root, "docs/executable.ja.md"), path.join(output, "README.ja.md"));
await fs.writeFile(path.join(output, "build-info.json"), JSON.stringify({ version: pkg.version, node: process.version, platform: process.platform, arch: process.arch, builtAt: new Date().toISOString(), sha256: hash }, null, 2));
await fs.writeFile(path.join(output, "THIRD-PARTY-NOTICES.txt"),
  "This executable includes the Node.js runtime and the ws library.\n\n--- Node.js licenses ---\n" +
  (await fs.readFile(path.join(path.dirname(process.execPath), "LICENSE"), "utf8")) +
  "\n\n--- ws license ---\n" + (await fs.readFile(path.join(root, "node_modules/ws/LICENSE"), "utf8")));
console.log(`\nCreated: ${exe}\nSHA256: ${hash}`);

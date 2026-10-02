import fs from "node:fs";
import path from "node:path";
import { main, reportStartupFailure } from "../app.js";
import { prepareExecutableEnvironment, restartExecutable, waitForPreviousProcess } from "./runtime.js";

declare const CAB_BUILD_VERSION: string;

async function launch(): Promise<void> {
  const args = process.argv.slice(1);
  if (args.includes("--help")) {
    console.log("Chat Agent Bridge " + CAB_BUILD_VERSION + "\n\nDouble-click to start. Open the displayed control center URL in your browser.\nThe browser is not launched automatically by default.\nOptions: --open-browser, --no-browser, --data-dir <directory>, --version, --help\nStop: Ctrl+C or close the console window.\nData: %LOCALAPPDATA%\\ChatAgentBridge (or CAB_DATA_DIR).\nAgent CLIs must be installed and signed in separately.");
    return;
  }
  if (args.includes("--version")) { console.log(CAB_BUILD_VERSION); return; }
  const option = (name: string) => {
    const index = args.indexOf(name);
    if (index < 0) return undefined;
    const value = args[index + 1];
    if (!value || value.startsWith("--")) throw new Error(`Missing value for ${name}`);
    return value;
  };
  const previous = option("--wait-for-pid");
  if (previous) {
    const pid = Number(previous);
    if (!Number.isSafeInteger(pid) || pid < 1 || pid === process.pid) throw new Error("Invalid previous process id.");
    await waitForPreviousProcess(pid);
  }
  if (args.includes("--open-browser")) process.env.CAB_OPEN_ADMIN = "1";
  // Explicitly disabling the browser wins over inherited env and other flags.
  if (args.includes("--no-browser")) process.env.CAB_OPEN_ADMIN = "0";
  const dataRoot = prepareExecutableEnvironment(process.env, option("--data-dir"));
  console.log(`Chat Agent Bridge ${CAB_BUILD_VERSION}\nData: ${dataRoot}\nStop: Ctrl+C or close this window.`);
  let app: Awaited<ReturnType<typeof main>>;
  app = await main({ launchDetachedRestart: (input) => restartExecutable(() => app.stop(), input) });
}

void launch().catch((error: unknown) => {
  reportStartupFailure(error);
  // Preserve the error for double-click launches, whose console may close immediately.
  try {
    const root = process.env.CAB_DATA_DIR ?? process.env.LOCALAPPDATA;
    if (root) fs.appendFileSync(path.join(root, "startup-error.log"), `${new Date().toISOString()} ${String(error)}\n`);
  } catch { /* Original error remains on stderr. */ }
});

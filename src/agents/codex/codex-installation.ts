import fs from "node:fs";
import path from "node:path";

/** Find the newest Codex desktop CLI installed by the Windows desktop app. */
export function findInstalledCodexExecutable(localAppData: string | undefined): string | undefined {
  if (!localAppData) return undefined;

  const versionsDirectory = path.join(localAppData, "OpenAI", "Codex", "bin");
  let versionEntries: fs.Dirent[];
  try {
    versionEntries = fs.readdirSync(versionsDirectory, { withFileTypes: true });
  } catch {
    return undefined;
  }

  const candidates: Array<{ executable: string; modifiedAt: number }> = [];
  for (const entry of versionEntries) {
    if (!entry.isDirectory()) continue;
    const executable = path.join(versionsDirectory, entry.name, "codex.exe");
    try {
      const stat = fs.statSync(executable);
      if (stat.isFile()) candidates.push({ executable, modifiedAt: stat.mtimeMs });
    } catch {
      // An incomplete version directory is not an installed executable.
    }
  }

  candidates.sort((left, right) => right.modifiedAt - left.modifiedAt || left.executable.localeCompare(right.executable));
  return candidates[0]?.executable;
}

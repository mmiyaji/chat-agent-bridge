import fs from "node:fs";
import path from "node:path";
import { randomUUID } from "node:crypto";

/**
 * Persisted admin/runtime settings are a flat JSON object of scalar values.
 * Keys not recognized by a given writer are preserved on read-modify-write so
 * that the admin UI and chat commands can each own a subset of keys without
 * clobbering the other's.
 */
export type PersistedSettings = Record<string, string | number | boolean>;

function readSettingsObject(settingsFile: string): Record<string, unknown> {
  if (!fs.existsSync(settingsFile)) {
    return {};
  }

  try {
    const parsed = JSON.parse(fs.readFileSync(settingsFile, "utf8"));
    if (!parsed || typeof parsed !== "object" || Array.isArray(parsed)) {
      return {};
    }
    return parsed as Record<string, unknown>;
  } catch {
    return {};
  }
}

/**
 * Read-modify-write the settings JSON file, merging `updates` over the existing
 * contents. Unknown keys already on disk are preserved. Keys whose update value
 * is `undefined` are removed. Both the admin server and the bridge service use
 * this so a write from one path never drops keys owned by the other.
 */
export function updatePersistedSettings(
  settingsFile: string,
  updates: Record<string, string | number | boolean | undefined>,
): void {
  const current = readSettingsObject(settingsFile);
  const next: Record<string, unknown> = { ...current };
  for (const [key, value] of Object.entries(updates)) {
    if (value === undefined) {
      delete next[key];
    } else {
      next[key] = value;
    }
  }

  fs.mkdirSync(path.dirname(settingsFile), { recursive: true });
  const temporary = `${settingsFile}.${randomUUID()}.tmp`;
  try {
    fs.writeFileSync(temporary, JSON.stringify(next, null, 2), { encoding: "utf8", flag: "wx", mode: 0o600 });
    fs.renameSync(temporary, settingsFile);
  } finally {
    fs.rmSync(temporary, { force: true });
  }
}

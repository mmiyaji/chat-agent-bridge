import { execFile } from "node:child_process";
import { promisify } from "node:util";
import fs from "node:fs";
import { CodexAppServerAgent } from "../agents/codex/app-server-agent.js";
import { extractExecutable, resolveCommand } from "../core/command-resolution.js";
import type { AppConfig } from "../core/types.js";

export type ModelOption = { id: string; label: string };

export function parseCursorModels(output: string): ModelOption[] {
  const options = new Map<string, ModelOption>();
  for (const line of output.replace(/\u001b\[[0-9;]*m/g, "").split(/\r?\n/)) {
    const match = line.trim().match(/^(?:[-*•]\s*)?([a-zA-Z0-9][a-zA-Z0-9._:/-]*)\s+(?:[-–—:]\s+|\s+)(.+)$/);
    if (match?.[1] && match[2]) options.set(match[1], { id: match[1], label: match[2].trim() });
  }
  return [...options.values()];
}

export async function listAgentModels(config: AppConfig, agent: string): Promise<ModelOption[]> {
  if (agent === "codex") return new CodexAppServerAgent(config).listModels();
  if (agent !== "cursor") throw new Error("このエージェントのモデル一覧取得には未対応です。モデルIDを手入力してください。");
  const executable = extractExecutable(config.cursorCommand ?? "cursor-agent");
  const resolved = resolveCommand(executable, process.cwd());
  if (resolved.status !== "resolved") throw new Error("Cursor CLIが見つかりません。");
  const command = resolved.resolvedPath ?? executable;
  let executablePath = command;
  let args = ["--list-models"];
  const script = command.replace(/\.cmd$/i, ".ps1");
  if (/\.ps1$/i.test(command) || (/\.cmd$/i.test(command) && fs.existsSync(script))) {
    executablePath = "powershell.exe";
    args = ["-NoProfile", "-NonInteractive", "-File", script, "--list-models"];
  } else if (/\.(cmd|bat)$/i.test(command)) throw new Error("一覧取得にはCursor CLIの実行ファイルを指定してください。");
  const { stdout } = await promisify(execFile)(executablePath, args, { cwd: process.cwd(), windowsHide: true, timeout: 15_000, maxBuffer: 1024 * 1024, env: { ...process.env, NO_COLOR: "1" } });
  const models = parseCursorModels(stdout);
  if (!models.length) throw new Error("Cursor CLIのモデル一覧を読み取れませんでした。CLIの認証・バージョンを確認してください。");
  return models;
}

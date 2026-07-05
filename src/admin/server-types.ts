import { resolveCommand } from "../core/command-resolution.js";
import { AppConfig } from "../core/types.js";

// Shared with src/admin/onboarding-page.ts so the wizard module does not need
// to import from server.ts (which would create a circular import).
export interface RuntimeSettingsPayload {
  agentKind: AppConfig["agentKind"];
  defaultApprovalPolicy: AppConfig["defaultApprovalPolicy"];
  networkAccess: AppConfig["networkAccess"];
  chatProvider: AppConfig["chatProvider"];
  slackAllowedUserIds: string;
  mattermostAllowedUserIds: string;
  slackMode: AppConfig["slackMode"];
  mattermostMode: AppConfig["mattermostMode"];
  mattermostActorMode: NonNullable<AppConfig["mattermostActorMode"]>;
  host: AppConfig["host"];
  port: AppConfig["port"];
  adminHost: NonNullable<AppConfig["adminHost"]>;
  adminPort: NonNullable<AppConfig["adminPort"]>;
  defaultWorkingDirectory: AppConfig["defaultWorkingDirectory"];
  /** Allowed working-directory roots, one absolute path per line. */
  allowedDirectories: string;
  mattermostServerUrl?: NonNullable<AppConfig["mattermostServerUrl"]>;
  mattermostActionBaseUrl?: NonNullable<AppConfig["mattermostActionBaseUrl"]>;
  mattermostDmOnly: boolean;
  hasSlackBotToken: boolean;
  hasSlackAppToken: boolean;
  hasSlackSigningSecret: boolean;
  hasMattermostBotToken: boolean;
  hasMattermostWebhookToken: boolean;
  slackBotToken?: string;
  slackAppToken?: string;
  slackSigningSecret?: string;
  mattermostBotToken?: string;
  mattermostWebhookToken?: string;
  clearSlackBotToken?: boolean;
  clearSlackAppToken?: boolean;
  clearSlackSigningSecret?: boolean;
  clearMattermostBotToken?: boolean;
  clearMattermostWebhookToken?: boolean;
  codexModel: AppConfig["codexModel"];
  codexMaxTurns: AppConfig["codexMaxTurns"];
  codexCommandTimeoutMs: AppConfig["codexCommandTimeoutMs"];
  codexMaxRuntimeMs: AppConfig["codexMaxRuntimeMs"];
  codexMaxOutputLength: AppConfig["codexMaxOutputLength"];
  cursorCommand: NonNullable<AppConfig["cursorCommand"]>;
  cursorModel: NonNullable<AppConfig["cursorModel"]>;
  cursorCommandTimeoutMs: NonNullable<AppConfig["cursorCommandTimeoutMs"]>;
  cursorMaxRuntimeMs: NonNullable<AppConfig["cursorMaxRuntimeMs"]>;
  geminiCommand: NonNullable<AppConfig["geminiCommand"]>;
  geminiModel: AppConfig["geminiModel"];
  geminiCommandTimeoutMs: NonNullable<AppConfig["geminiCommandTimeoutMs"]>;
  geminiMaxRuntimeMs: NonNullable<AppConfig["geminiMaxRuntimeMs"]>;
  claudeModel: AppConfig["claudeModel"];
  claudeMaxTurns: AppConfig["claudeMaxTurns"];
  claudeCommandTimeoutMs: AppConfig["claudeCommandTimeoutMs"];
  claudeMaxRuntimeMs: AppConfig["claudeMaxRuntimeMs"];
  agentCommands: {
    codex: ReturnType<typeof resolveCommand>;
    "claude-code": ReturnType<typeof resolveCommand>;
    cursor: ReturnType<typeof resolveCommand>;
    gemini: ReturnType<typeof resolveCommand>;
  };
  autoSplitThreadUserMessageCount: AppConfig["autoSplitThreadUserMessageCount"];
  settingsFile: string;
  settingsSavedAt?: string;
}

import fs from "node:fs/promises";
import path from "node:path";

type HttpMethod = "GET" | "POST";

interface MattermostUser {
  id: string;
  username: string;
}

interface MattermostTeam {
  id: string;
  name: string;
}

interface MattermostChannel {
  id: string;
  name: string;
}

interface BootstrapState {
  serverUrl: string;
  team: {
    id: string;
    name: string;
  };
  channel: {
    id: string;
    name: string;
  };
  bridgeUser: {
    id: string;
    username: string;
    password: string;
    token: string;
  };
  memberUser: {
    id: string;
    username: string;
    password: string;
    token: string;
  };
}

const serverUrl = (process.env.MATTERMOST_E2E_SERVER_URL ?? "http://127.0.0.1:8065").replace(/\/$/, "");
const stateFile = path.resolve(
  process.env.CAB_MATTERMOST_E2E_STATE_FILE ?? "./data/runtime/mattermost-e2e.json",
);
const bridgeUsername = process.env.MATTERMOST_E2E_BRIDGE_USERNAME ?? "cab-bridge";
const bridgePassword = process.env.MATTERMOST_E2E_BRIDGE_PASSWORD ?? "ChatAgentBridge!234";
const bridgeEmail = process.env.MATTERMOST_E2E_BRIDGE_EMAIL ?? "cab-bridge@example.test";
const memberUsername = process.env.MATTERMOST_E2E_MEMBER_USERNAME ?? "cab-member";
const memberPassword = process.env.MATTERMOST_E2E_MEMBER_PASSWORD ?? "ChatAgentBridge!235";
const memberEmail = process.env.MATTERMOST_E2E_MEMBER_EMAIL ?? "cab-member@example.test";
const teamName = process.env.MATTERMOST_E2E_TEAM_NAME ?? "chat-agent-bridge";
const teamDisplayName = process.env.MATTERMOST_E2E_TEAM_DISPLAY_NAME ?? "Chat Agent Bridge";
const channelName = process.env.MATTERMOST_E2E_CHANNEL_NAME ?? "bridge-e2e";
const channelDisplayName = process.env.MATTERMOST_E2E_CHANNEL_DISPLAY_NAME ?? "Bridge E2E";

async function sleep(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

async function waitForMattermost(): Promise<void> {
  const deadline = Date.now() + 180_000;
  while (Date.now() < deadline) {
    try {
      const response = await fetch(`${serverUrl}/api/v4/system/ping`);
      if (response.ok) {
        return;
      }
    } catch {
      // retry
    }
    await sleep(2_000);
  }

  throw new Error(`Mattermost was not ready at ${serverUrl} within 180 seconds.`);
}

async function apiRequest<T>(
  method: HttpMethod,
  requestPath: string,
  options?: {
    token?: string;
    body?: unknown;
    allowStatuses?: number[];
  },
): Promise<{ status: number; data: T | undefined }> {
  const response = await fetch(`${serverUrl}${requestPath}`, {
    method,
    headers: {
      ...(options?.token ? { Authorization: `Bearer ${options.token}` } : {}),
      ...(options?.body ? { "Content-Type": "application/json" } : {}),
    },
    body: options?.body ? JSON.stringify(options.body) : undefined,
  });

  if (!response.ok && !(options?.allowStatuses ?? []).includes(response.status)) {
    const text = await response.text();
    throw new Error(`${method} ${requestPath} failed with ${response.status}: ${text}`);
  }

  if (response.status === 204) {
    return { status: response.status, data: undefined };
  }

  const text = await response.text();
  return {
    status: response.status,
    data: text ? (JSON.parse(text) as T) : undefined,
  };
}

async function login(username: string, password: string): Promise<string | undefined> {
  const response = await fetch(`${serverUrl}/api/v4/users/login`, {
    method: "POST",
    headers: {
      "Content-Type": "application/json",
    },
    body: JSON.stringify({
      login_id: username,
      password,
    }),
  });

  if (!response.ok) {
    return undefined;
  }

  return response.headers.get("token") ?? undefined;
}

async function ensureUser(input: {
  username: string;
  password: string;
  email: string;
}): Promise<{ user: MattermostUser; token: string }> {
  let token = await login(input.username, input.password);
  if (!token) {
    await apiRequest<MattermostUser>("POST", "/api/v4/users", {
      allowStatuses: [400],
      body: {
        username: input.username,
        email: input.email,
        password: input.password,
      },
    });
    token = await login(input.username, input.password);
  }

  if (!token) {
    throw new Error(`Unable to create or log in Mattermost user ${input.username}.`);
  }

  const me = await apiRequest<MattermostUser>("GET", "/api/v4/users/me", {
    token,
  });
  if (!me.data?.id) {
    throw new Error(`Unable to resolve current user for ${input.username}.`);
  }

  return {
    user: me.data,
    token,
  };
}

async function ensureTeam(adminToken: string): Promise<MattermostTeam> {
  const existing = await apiRequest<MattermostTeam>("GET", `/api/v4/teams/name/${teamName}`, {
    token: adminToken,
    allowStatuses: [404],
  });
  if (existing.status === 200 && existing.data?.id) {
    return existing.data;
  }

  const created = await apiRequest<MattermostTeam>("POST", "/api/v4/teams", {
    token: adminToken,
    body: {
      name: teamName,
      display_name: teamDisplayName,
      type: "O",
    },
  });
  if (!created.data?.id) {
    throw new Error("Failed to create Mattermost team.");
  }

  return created.data;
}

async function ensureTeamMembership(
  adminToken: string,
  teamId: string,
  userId: string,
): Promise<void> {
  await apiRequest("POST", `/api/v4/teams/${teamId}/members`, {
    token: adminToken,
    allowStatuses: [400],
    body: {
      team_id: teamId,
      user_id: userId,
    },
  });
}

async function ensureChannel(adminToken: string, teamId: string): Promise<MattermostChannel> {
  const existing = await apiRequest<MattermostChannel>(
    "GET",
    `/api/v4/teams/${teamId}/channels/name/${channelName}`,
    {
      token: adminToken,
      allowStatuses: [404],
    },
  );
  if (existing.status === 200 && existing.data?.id) {
    return existing.data;
  }

  const created = await apiRequest<MattermostChannel>("POST", "/api/v4/channels", {
    token: adminToken,
    body: {
      team_id: teamId,
      name: channelName,
      display_name: channelDisplayName,
      type: "O",
    },
  });
  if (!created.data?.id) {
    throw new Error("Failed to create Mattermost channel.");
  }

  return created.data;
}

async function ensureChannelMembership(
  adminToken: string,
  channelId: string,
  userId: string,
): Promise<void> {
  await apiRequest("POST", `/api/v4/channels/${channelId}/members`, {
    token: adminToken,
    allowStatuses: [400],
    body: {
      channel_id: channelId,
      user_id: userId,
    },
  });
}

async function main(): Promise<void> {
  await waitForMattermost();

  const bridge = await ensureUser({
    username: bridgeUsername,
    password: bridgePassword,
    email: bridgeEmail,
  });

  const team = await ensureTeam(bridge.token);
  await ensureTeamMembership(bridge.token, team.id, bridge.user.id);

  const channel = await ensureChannel(bridge.token, team.id);
  await ensureChannelMembership(bridge.token, channel.id, bridge.user.id);

  const member = await ensureUser({
    username: memberUsername,
    password: memberPassword,
    email: memberEmail,
  });
  await ensureTeamMembership(bridge.token, team.id, member.user.id);
  await ensureChannelMembership(bridge.token, channel.id, member.user.id);

  const state: BootstrapState = {
    serverUrl,
    team: {
      id: team.id,
      name: team.name,
    },
    channel: {
      id: channel.id,
      name: channel.name,
    },
    bridgeUser: {
      id: bridge.user.id,
      username: bridge.user.username,
      password: bridgePassword,
      token: bridge.token,
    },
    memberUser: {
      id: member.user.id,
      username: member.user.username,
      password: memberPassword,
      token: member.token,
    },
  };

  await fs.mkdir(path.dirname(stateFile), { recursive: true });
  await fs.writeFile(stateFile, JSON.stringify(state, null, 2), "utf8");
  console.log(`Mattermost E2E bootstrap complete: ${stateFile}`);
}

await main();

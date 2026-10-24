// Loopback-only Discord REST/Gateway fixture for native desktop UI checks.
// It uses synthetic identities and data and never contacts Discord or an AI service.
const http = require('node:http');
const fs = require('node:fs');
const { WebSocketServer, WebSocket } = require('ws');

const portFile = process.argv[2];
if (!portFile) throw new Error('Provide an output file for the ephemeral loopback port.');
const TOKEN = 'offline-discord-fixture';
const BOT_ID = '723456789012345678';
const GUILD_ID = '223456789012345678';
const CHANNEL_ID = '323456789012345678';
const THREAD_IDS = ['523456789012345678', '623456789012345678', '723456789012345679', '823456789012345678'];
const messages = new Map();
const threads = new Map();
const incoming = new Map();
const cdnFiles = new Map();
const interactions = [];
const inboundRequests = [];
const uploads = [];
const restRequests = [];
const fileDownloads = [];
const sockets = new Set();
let nextThread = 0;
let nextMessage = 0;
let nextInteraction = 0;
let nextFile = 0;
let sequence = 0;

const json = (res, status, value) => {
  res.writeHead(status, { 'Content-Type': 'application/json' });
  res.end(JSON.stringify(value));
};
const readBody = async req => {
  const chunks = [];
  let length = 0;
  for await (const chunk of req) {
    length += chunk.length;
    if (length > 32 * 1024 * 1024) throw new Error('request too large');
    chunks.push(chunk);
  }
  return Buffer.concat(chunks);
};
function gatewayEvent(type, data) {
  const event = JSON.stringify({ op: 0, s: ++sequence, t: type, d: data });
  for (const socket of sockets) if (socket.readyState === WebSocket.OPEN) socket.send(event);
}
function fileFor(attachment) {
  const id = String(attachment.id || `inbound-${++nextFile}`);
  const name = String(attachment.name || attachment.filename || 'fixture.txt');
  const data = Buffer.from(String(attachment.content || 'offline Discord attachment fixture'), 'utf8');
  const urlPath = `/attachments/${id}/${encodeURIComponent(name)}`;
  const metadata = { id, filename: name, size: data.length, content_type: attachment.contentType || 'text/plain',
    url: `https://cdn.discordapp.com${urlPath}`, proxy_url: `https://cdn.discordapp.com${urlPath}` };
  incoming.set(id, data);
  cdnFiles.set(`/cdn${urlPath}`, { data, contentType: metadata.content_type });
  return metadata;
}
function threadFor(parentId, messageId) {
  const id = THREAD_IDS[nextThread++ % THREAD_IDS.length];
  threads.set(id, { id, guild_id: GUILD_ID, parent_id: parentId, type: 11, starter_message_id: messageId });
  return threads.get(id);
}
function addMessage(channelId, content, components = [], extra = {}) {
  const message = { id: `923456789012345${String(++nextMessage).padStart(3, '0')}`,
    channel_id: channelId, content: content || '', components: components || [], attachments: [], ...extra };
  messages.set(message.id, message);
  return message;
}

const server = http.createServer(async (req, res) => {
  let raw;
  try { raw = await readBody(req); }
  catch { res.writeHead(413).end('{}'); return; }
  const url = new URL(req.url, 'http://127.0.0.1');
  const path = url.pathname;
  restRequests.push({ method: req.method, path, hasAuthorization: Boolean(req.headers.authorization) });
  let body = {};
  if (raw.length && !(req.headers['content-type'] || '').startsWith('multipart/form-data')) {
    try { body = JSON.parse(raw.toString('utf8')); }
    catch { res.writeHead(400).end('{}'); return; }
  }

  if (path === '/test/request' && req.method === 'POST') {
    const id = String(body.id || `message-${Date.now()}`);
    const channelId = String(body.threadId || body.channel || CHANNEL_ID);
    const parentId = body.threadId ? String(body.parent || CHANNEL_ID) : null;
    const userId = String(body.user || '123456789012345678');
    const guildId = body.guild === null || body.guild === 'dm' ? null : String(body.guild || GUILD_ID);
    const attachments = Array.isArray(body.attachments) ? body.attachments.map(fileFor) : [];
    const content = String(body.text || '');
    const message = { id, channel_id: channelId, guild_id: guildId, thread_id: body.resolveParent ? null : parentId,
      author: { id: userId, username: 'fixture-user', bot: false }, content, type: 0, attachments };
    incoming.set(`${channelId}:${id}`, message);
    inboundRequests.push({ id, channel_id: channelId, parent_id: parentId, user_id: userId, guild_id: guildId, text: content });
    gatewayEvent('MESSAGE_CREATE', message);
    json(res, 200, { delivered: sockets.size, id, channel_id: channelId, guild_id: guildId });
    return;
  }
  if (path === '/test/click' && req.method === 'POST') {
    const message = messages.get(String(body.messageId));
    const channelId = String(body.channel || message?.channel_id || '');
    const userId = String(body.user || '123456789012345678');
    const customId = String(body.customId || '');
    const interactionId = `103456789012345${String(++nextInteraction).padStart(3, '0')}`;
    const interactionToken = `fixture-interaction-${nextInteraction}`;
    gatewayEvent('INTERACTION_CREATE', {
      id: interactionId, token: interactionToken, type: 3, guild_id: body.guild === null ? null : String(body.guild || GUILD_ID),
      channel_id: channelId, user: { id: userId }, message: { id: String(body.messageId) }, data: { custom_id: customId }
    });
    json(res, 200, { delivered: sockets.size, interactionId, customId });
    return;
  }
  if (path === '/test/results') {
    json(res, 200, { connected: sockets.size, inboundRequests, messages: [...messages.values()],
      threads: [...threads.values()], interactions, uploads: uploads.map(item => ({ ...item, data: undefined,
        base64: item.data.toString('base64') })), restRequests, fileDownloads, gatewayEvents: sequence });
    return;
  }

  if (path.startsWith('/cdn/')) {
    const file = cdnFiles.get(path);
    if (!file) { res.writeHead(404).end(); return; }
    fileDownloads.push({ path, bytes: file.data.length });
    res.writeHead(200, { 'Content-Type': file.contentType, 'Content-Length': String(file.data.length) });
    res.end(file.data);
    return;
  }

  if (path.startsWith('/api/v10/interactions/') && path.endsWith('/callback') && req.method === 'POST') {
    interactions.push({ path, body: raw.toString('utf8') });
    res.writeHead(204).end();
    return;
  }
  if (req.headers.authorization !== `Bot ${TOKEN}`) { json(res, 401, {}); return; }
  if (path === '/api/v10/gateway/bot' && req.method === 'GET') {
    json(res, 200, { url: `ws://127.0.0.1:${server.address().port}/` });
    return;
  }
  const channelMatch = /^\/api\/v10\/channels\/([^/]+)$/.exec(path);
  if (channelMatch && req.method === 'GET') {
    const thread = threads.get(decodeURIComponent(channelMatch[1]));
    // Discord's channel metadata commonly omits guild_id. The transport must
    // retain guild scope from the original gateway event instead of relying on it.
    const channelId = decodeURIComponent(channelMatch[1]);
    json(res, 200, thread || { id: channelId, guild_id: channelId === CHANNEL_ID ? GUILD_ID : null, type: 0, parent_id: null });
    return;
  }
  const sourceMessageMatch = /^\/api\/v10\/channels\/([^/]+)\/messages\/([^/]+)$/.exec(path);
  if (sourceMessageMatch && req.method === 'GET') {
    const channel = decodeURIComponent(sourceMessageMatch[1]);
    const id = decodeURIComponent(sourceMessageMatch[2]);
    const found = incoming.get(`${channel}:${id}`);
    if (!found) { json(res, 404, {}); return; }
    // Message retrieval is intentionally realistic: Discord's message object does not
    // carry guild_id, so the transport must rely on the original gateway event.
    json(res, 200, { id: found.id, channel_id: found.channel_id,
      author: found.author, attachments: found.attachments || [] });
    return;
  }
  const createThreadMatch = /^\/api\/v10\/channels\/([^/]+)\/messages\/([^/]+)\/threads$/.exec(path);
  if (createThreadMatch && req.method === 'POST') {
    const parentId = decodeURIComponent(createThreadMatch[1]);
    const starterId = decodeURIComponent(createThreadMatch[2]);
    const thread = threadFor(parentId, starterId);
    json(res, 200, thread);
    return;
  }
  const messageCollection = /^\/api\/v10\/channels\/([^/]+)\/messages$/.exec(path);
  if (messageCollection && req.method === 'POST') {
    const channelId = decodeURIComponent(messageCollection[1]);
    if ((req.headers['content-type'] || '').startsWith('multipart/form-data')) {
      const boundaryMatch = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(req.headers['content-type']);
      const boundary = boundaryMatch?.[1] || boundaryMatch?.[2]?.trim();
      if (!boundary) { json(res, 400, {}); return; }
      const marker = Buffer.from(`--${boundary}`);
      const parts = [];
      let cursor = 0;
      while ((cursor = raw.indexOf(marker, cursor)) >= 0) {
        const headerStart = cursor + marker.length + 2;
        const dataStart = raw.indexOf(Buffer.from('\r\n\r\n'), headerStart);
        if (dataStart < 0) break;
        const header = raw.subarray(headerStart, dataStart).toString('latin1');
        const end = raw.indexOf(marker, dataStart + 4);
        if (end < 0) break;
        const bytes = raw.subarray(dataStart + 4, Math.max(dataStart + 4, end - 2));
        if (/name="files\[\d+\]"/.test(header)) {
          const filename = /filename="([^"]+)"/.exec(header)?.[1] || `upload-${parts.length + 1}.bin`;
          parts.push({ filename, bytes: Buffer.from(bytes) });
        }
        cursor = end;
      }
      if (parts.length === 0) { json(res, 400, {}); return; }
      const attachments = parts.map(part => {
        const id = `upload-${++nextFile}`;
        const path = `/attachments/${id}/${encodeURIComponent(part.filename)}`;
        cdnFiles.set(`/cdn${path}`, { data: part.bytes, contentType: 'application/octet-stream' });
        uploads.push({ id, filename: part.filename, data: part.bytes, channel_id: channelId });
        return { id, filename: part.filename, size: part.bytes.length,
          url: `https://cdn.discordapp.com${path}`, proxy_url: `https://cdn.discordapp.com${path}` };
      });
      const message = addMessage(channelId, '', [], { attachments });
      json(res, 200, message);
      return;
    }
    const content = typeof body.content === 'string' ? body.content : '';
    const message = addMessage(channelId, content, body.components || []);
    json(res, 200, message);
    return;
  }
  const editMatch = /^\/api\/v10\/channels\/([^/]+)\/messages\/([^/]+)$/.exec(path);
  if (editMatch && req.method === 'PATCH') {
    const message = messages.get(decodeURIComponent(editMatch[2]));
    if (!message || message.channel_id !== decodeURIComponent(editMatch[1])) { json(res, 404, {}); return; }
    if (typeof body.content === 'string') message.content = body.content;
    if (Array.isArray(body.components)) message.components = body.components;
    json(res, 200, message);
    return;
  }
  json(res, 404, {});
});

const wss = new WebSocketServer({ server, path: '/' });
wss.on('connection', socket => {
  socket.send(JSON.stringify({ op: 10, d: { heartbeat_interval: 30000 } }));
  socket.on('message', raw => {
    let packet;
    try { packet = JSON.parse(raw.toString()); } catch { socket.close(); return; }
    if (packet.op === 1) socket.send(JSON.stringify({ op: 11, d: null }));
    if (packet.op === 2) {
      if (packet.d?.token !== TOKEN) { socket.close(4004, 'fixture auth failed'); return; }
      sockets.add(socket);
      socket.send(JSON.stringify({ op: 0, s: ++sequence, t: 'READY', d: {
        session_id: 'discord-fixture-session', resume_gateway_url: `ws://127.0.0.1:${server.address().port}/`,
        user: { id: BOT_ID, username: 'fixture-bot', bot: true }
      } }));
    }
  });
  socket.on('close', () => sockets.delete(socket));
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port)));

// Loopback-only Mattermost protocol fixture. No real credentials or external traffic.
const http = require('node:http');
const fs = require('node:fs');
const zlib = require('node:zlib');
const { WebSocketServer, WebSocket } = require('ws');
const portFile = process.argv[2];
if (!portFile) throw new Error('Provide an output file for the ephemeral loopback port.');
const replies = [];
const updates = [];
const reactions = [];
const reactionAdds = [];
const reactionRemoves = [];
const incomingFiles = new Map();
const fileInfoRequests = [];
const fileDownloads = [];
const incomingPosts = new Map();
const incomingPostRequests = [];
const galleryAvatarFixture = process.env.CAB_GALLERY_AVATAR_FIXTURE === '1';
let profileImageRequests = 0;
let failProgressEdits = false;
let offlineUntil = 0;
let totalConnections = 0;
const sockets = new Set();

function crc32(buffer) {
  let crc = 0xffffffff;
  for (const byte of buffer) {
    crc ^= byte;
    for (let bit = 0; bit < 8; bit++) crc = (crc >>> 1) ^ ((crc & 1) ? 0xedb88320 : 0);
  }
  return (crc ^ 0xffffffff) >>> 0;
}

function pngChunk(name, data) {
  const type = Buffer.from(name, 'ascii');
  const payload = Buffer.concat([type, data]);
  const chunk = Buffer.alloc(12 + data.length);
  chunk.writeUInt32BE(data.length, 0);
  payload.copy(chunk, 4);
  chunk.writeUInt32BE(crc32(payload), 8 + data.length);
  return chunk;
}

function createGalleryAvatarPng() {
  const header = Buffer.alloc(13);
  header.writeUInt32BE(2, 0);
  header.writeUInt32BE(2, 4);
  header[8] = 8; // bit depth
  header[9] = 6; // RGBA
  const pixels = Buffer.from([
    0, 44, 122, 190, 255, 44, 122, 190, 255,
    0, 255, 186, 73, 255, 255, 186, 73, 255
  ]);
  return Buffer.concat([
    Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]),
    pngChunk('IHDR', header),
    pngChunk('IDAT', zlib.deflateSync(pixels)),
    pngChunk('IEND', Buffer.alloc(0))
  ]);
}

const galleryAvatarPng = createGalleryAvatarPng();
const server = http.createServer(async (req, res) => {
  let raw = '';
  for await (const chunk of req) {
    raw += chunk;
    if (raw.length > 65536) { res.writeHead(413).end(); return; }
  }
  let body;
  try { body = raw ? JSON.parse(raw) : {}; }
  catch { res.writeHead(400).end(); return; }
  res.setHeader('Content-Type', 'application/json');
  if (req.url === '/test/request' && req.method === 'POST') {
    const files = Array.isArray(body.files) ? body.files : [];
    const fileIds = files.map((file, index) => String(file.id || `file-${body.id}-${index + 1}`));
    files.forEach((file, index) => {
      const id = fileIds[index];
      const data = Buffer.from(file.content || 'offline attachment fixture', 'utf8');
      incomingFiles.set(id, {
        id, name: file.name || 'fixture.txt', size: data.length,
        mime_type: file.mimeType || 'text/plain', post_id: body.id,
        channel_id: 'ui-channel', data
      });
    });
    const post = {
      id: body.id, root_id: body.thread, channel_id: 'ui-channel', user_id: body.user || 'alice',
      message: body.plain ? body.text : `${body.mention ? '@cc-agent-bot' : '!cab'} ${body.text}`,
      file_ids: body.invalidFileIds ? 'invalid-file-ids' : fileIds
    };
    incomingPosts.set(post.id, post);
    for (const ws of sockets) if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ event: 'posted', data: { post: JSON.stringify(post) } }));
    res.end(JSON.stringify({ delivered: sockets.size }));
  } else if (req.url === '/test/reaction' && req.method === 'POST') {
    const reaction = {
      user_id: body.user_id || body.user || '',
      post_id: body.post_id || '',
      emoji_name: body.emoji_name || 'octagonal_sign'
    };
    if (!reactions.some(item => item.user_id === reaction.user_id && item.post_id === reaction.post_id && item.emoji_name === reaction.emoji_name)) reactions.push(reaction);
    const event = {
      event: 'reaction_added',
      data: { reaction: JSON.stringify(reaction) },
      broadcast: { channel_id: body.channel_id || 'ui-channel' }
    };
    for (const ws of sockets) if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(event));
    res.end(JSON.stringify({ delivered: sockets.size }));
  } else if (req.url === '/test/results') {
    const results = { replies, updates, reactions, reactionAdds, reactionRemoves,
      fileInfoRequests, incomingPostRequests, fileDownloads: fileDownloads.map(item => ({ id: item.id, post_id: item.post_id, bytes: item.bytes })),
      connections: sockets.size, totalConnections };
    if (galleryAvatarFixture) results.profileImageRequests = profileImageRequests;
    res.end(JSON.stringify(results));
  } else if (req.url === '/test/progress-errors' && req.method === 'POST') {
    failProgressEdits = body.enabled === true;
    res.end('{}');
  } else if (req.url === '/test/disconnect' && req.method === 'POST') {
    offlineUntil = Date.now() + Math.min(10000, Math.max(0, Number(body.offlineMilliseconds) || 0));
    for (const ws of sockets) ws.close(1013, 'temporary test outage');
    res.end('{}');
  } else if (req.headers.authorization !== 'Bearer offline-ui-fixture') {
    res.writeHead(401).end('{}');
  } else if (req.method === 'GET' && /^\/api\/v4\/users\/[^/]+\/image$/.test(req.url)) {
    const userId = decodeURIComponent(req.url.split('/')[4]);
    if (galleryAvatarFixture) profileImageRequests++;
    if (!galleryAvatarFixture || userId !== '0123456789abcdefghijklmnop') { res.writeHead(404).end('{}'); return; }
    res.setHeader('Content-Type', 'image/png');
    res.setHeader('Content-Length', String(galleryAvatarPng.length));
    res.end(galleryAvatarPng);
  } else if (req.method === 'GET' && /^\/api\/v4\/files\/[^/]+\/info$/.test(req.url)) {
    const id = decodeURIComponent(req.url.split('/')[4]);
    const file = incomingFiles.get(id);
    fileInfoRequests.push({ id, found: Boolean(file) });
    if (!file) { res.writeHead(404).end('{}'); return; }
    res.end(JSON.stringify({ id: file.id, name: file.name, size: file.size,
      mime_type: file.mime_type, post_id: file.post_id }));
  } else if (req.method === 'GET' && /^\/api\/v4\/posts\/[^/]+$/.test(req.url)) {
    const id = decodeURIComponent(req.url.split('/')[4]);
    const post = incomingPosts.get(id);
    incomingPostRequests.push({ id, found: Boolean(post) });
    if (!post) { res.writeHead(404).end('{}'); return; }
    res.end(JSON.stringify({ id: post.id, channel_id: post.channel_id, user_id: post.user_id,
      root_id: post.root_id, file_ids: post.file_ids }));
  } else if (req.method === 'GET' && /^\/api\/v4\/files\/[^/]+$/.test(req.url)) {
    const id = decodeURIComponent(req.url.split('/')[4]);
    const file = incomingFiles.get(id);
    if (!file) { res.writeHead(404).end('{}'); return; }
    fileDownloads.push({ id, post_id: file.post_id, bytes: file.data.length });
    res.setHeader('Content-Type', file.mime_type);
    res.setHeader('Content-Length', String(file.data.length));
    res.end(file.data);
  } else if (req.url === '/api/v4/users/me') {
    res.end(JSON.stringify({ id: 'ui-bridge', username: 'cc-agent-bot', is_bot: true }));
  } else if (req.url === '/api/v4/reactions' && req.method === 'POST') {
    if (body.user_id !== 'ui-bridge' || body.emoji_name !== 'octagonal_sign' || !replies.some(post => post.id === body.post_id)) { res.writeHead(400).end('{}'); return; }
    const reaction = { user_id: body.user_id, post_id: body.post_id, emoji_name: body.emoji_name };
    if (!reactions.some(item => item.user_id === reaction.user_id && item.post_id === reaction.post_id && item.emoji_name === reaction.emoji_name)) reactions.push(reaction);
    reactionAdds.push(reaction);
    // Mirror Mattermost's own-reaction echo; it must never cancel the request.
    for (const ws of sockets) if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ event: 'reaction_added', data: { reaction: JSON.stringify(reaction) }, broadcast: { channel_id: 'ui-channel' } }));
    res.end(JSON.stringify(reaction));
  } else if (req.url.startsWith('/api/v4/users/ui-bridge/posts/') && req.url.endsWith('/reactions/octagonal_sign') && req.method === 'DELETE') {
    const postId = req.url.split('/')[6];
    const index = reactions.findIndex(item => item.user_id === 'ui-bridge' && item.post_id === postId && item.emoji_name === 'octagonal_sign');
    if (index >= 0) reactions.splice(index, 1);
    reactionRemoves.push({ user_id: 'ui-bridge', post_id: postId, emoji_name: 'octagonal_sign' });
    res.end('{}');
  } else if (req.url.startsWith('/api/v4/users/')) {
    res.end(JSON.stringify({ id: req.url.split('/').at(-1), is_bot: false }));
  } else if (req.url === '/api/v4/posts' && req.method === 'POST') {
    const post = { ...body, id: `reply-${replies.length + 1}`, user_id: 'ui-bridge' };
    replies.push(post);
    res.end(JSON.stringify(post));
  } else if (req.url.startsWith('/api/v4/posts/') && req.url.endsWith('/patch') && req.method === 'PUT') {
    if (failProgressEdits) { res.writeHead(503).end('{}'); return; }
    const id = req.url.split('/')[4];
    const post = replies.find(item => item.id === id);
    if (!post || typeof body.message !== 'string') { res.writeHead(404).end('{}'); return; }
    post.message = body.message;
    updates.push({ id, message: body.message, channel_id: post.channel_id, root_id: post.root_id });
    res.end(JSON.stringify(post));
  } else { res.writeHead(404).end('{}'); }
});
const wss = new WebSocketServer({ server, path: '/api/v4/websocket' });
wss.on('connection', ws => {
  if (Date.now() < offlineUntil) { ws.close(1013, 'temporary test outage'); return; }
  ws.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.action !== 'authentication_challenge' || message.data?.token !== 'offline-ui-fixture') { ws.close(); return; }
    sockets.add(ws);
    totalConnections++;
    ws.send(JSON.stringify({ seq_reply: 1, status: 'OK' }));
  });
  ws.on('close', () => sockets.delete(ws));
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port)));

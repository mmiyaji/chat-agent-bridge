// Loopback-only Mattermost fixture for file attachment UI checks. No real credentials or external traffic.
const http = require('node:http');
const fs = require('node:fs');
const { WebSocketServer, WebSocket } = require('ws');

const portFile = process.argv[2];
if (!portFile) throw new Error('Provide an output file for the ephemeral loopback port.');
const replies = [];
const uploads = [];
const sockets = new Set();
let nextFileId = 1;

const server = http.createServer(async (req, res) => {
  const chunks = [];
  let length = 0;
  for await (const chunk of req) {
    length += chunk.length;
    if (length > 32 * 1024 * 1024) { res.writeHead(413).end(); return; }
    chunks.push(chunk);
  }
  const raw = Buffer.concat(chunks);
  const bodyText = raw.toString('utf8');
  let body = {};
  try { body = bodyText ? JSON.parse(bodyText) : {}; }
  catch { if (req.url !== '/api/v4/files') { res.writeHead(400).end(); return; } }

  res.setHeader('Content-Type', 'application/json');
  if (req.url === '/test/request' && req.method === 'POST') {
    const post = { id: body.id, root_id: body.thread, channel_id: 'ui-channel', user_id: body.user,
      message: body.plain ? body.text : `${body.mention ? '@cc-agent-bot' : '!cab'} ${body.text}` };
    for (const ws of sockets) if (ws.readyState === WebSocket.OPEN)
      ws.send(JSON.stringify({ event: 'posted', data: { post: JSON.stringify(post) } }));
    res.end(JSON.stringify({ delivered: sockets.size }));
  } else if (req.url === '/test/results') {
    res.end(JSON.stringify({ replies, uploads, connections: sockets.size }));
  } else if (req.headers.authorization !== 'Bearer offline-ui-fixture') {
    res.writeHead(401).end('{}');
  } else if (req.url === '/api/v4/users/me') {
    res.end(JSON.stringify({ id: 'ui-bridge', username: 'cc-agent-bot', is_bot: true }));
  } else if (req.url.startsWith('/api/v4/users/')) {
    res.end(JSON.stringify({ id: req.url.split('/').at(-1), is_bot: false }));
  } else if (req.url === '/api/v4/files' && req.method === 'POST') {
    const boundary = /boundary=(?:"([^"]+)"|([^;]+))/i.exec(req.headers['content-type'] || '');
    if (!boundary) { res.writeHead(400).end('{}'); return; }
    const marker = Buffer.from(`--${(boundary[1] || boundary[2]).trim()}`);
    const parts = [];
    let offset = 0;
    while ((offset = raw.indexOf(marker, offset)) >= 0) {
      const start = raw.indexOf(Buffer.from('\r\n\r\n'), offset);
      if (start < 0) break;
      const header = raw.subarray(offset, start).toString('latin1');
      const end = raw.indexOf(marker, start + 4);
      if (end < 0) break;
      if (/name=(?:"files"|files)(?:;|$)/i.test(header)) {
        const filename = /filename=(?:"([^"]*)"|([^;\r\n]*))/i.exec(header);
        const name = (filename?.[1] ?? filename?.[2]?.trim()) || 'unknown.bin';
        const dataStart = start + 4;
        const dataEnd = Math.max(dataStart, end - 2);
        const data = raw.subarray(dataStart, dataEnd);
        const file = { id: `fixture-file-${nextFileId++}`, name, size: data.length, channel_id: 'ui-channel' };
        uploads.push({ ...file, bytes: data.toString('base64') });
        parts.push(file);
      }
      offset = end;
    }
    if (parts.length === 0) { res.writeHead(400).end('{}'); return; }
    res.end(JSON.stringify({ file_infos: parts }));
  } else if (req.url === '/api/v4/posts' && req.method === 'POST') {
    const post = { ...body, id: `reply-${replies.length + 1}`, user_id: 'ui-bridge' };
    replies.push(post);
    res.end(JSON.stringify(post));
  } else {
    res.writeHead(404).end('{}');
  }
});

const wss = new WebSocketServer({ server, path: '/api/v4/websocket' });
wss.on('connection', ws => {
  ws.on('message', raw => {
    let message;
    try { message = JSON.parse(raw.toString()); } catch { ws.close(); return; }
    if (message.action !== 'authentication_challenge' || message.data?.token !== 'offline-ui-fixture') { ws.close(); return; }
    sockets.add(ws);
    ws.send(JSON.stringify({ seq_reply: 1, status: 'OK' }));
  });
  ws.on('close', () => sockets.delete(ws));
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port)));

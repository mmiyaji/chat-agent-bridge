// Loopback-only Mattermost protocol fixture. No real credentials or external traffic.
const http = require('node:http');
const fs = require('node:fs');
const { WebSocketServer, WebSocket } = require('ws');
const portFile = process.argv[2];
if (!portFile) throw new Error('Provide an output file for the ephemeral loopback port.');
const replies = [];
const sockets = new Set();
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
    const post = { id: body.id, root_id: body.thread, channel_id: 'ui-channel', user_id: body.user, message: body.plain ? body.text : `${body.mention ? '@cc-agent-bot' : '!cab'} ${body.text}` };
    for (const ws of sockets) if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify({ event: 'posted', data: { post: JSON.stringify(post) } }));
    res.end(JSON.stringify({ delivered: sockets.size }));
  } else if (req.url === '/test/results') {
    res.end(JSON.stringify({ replies, connections: sockets.size }));
  } else if (req.headers.authorization !== 'Bearer offline-ui-fixture') {
    res.writeHead(401).end('{}');
  } else if (req.url === '/api/v4/users/me') {
    res.end(JSON.stringify({ id: 'ui-bridge', username: 'cc-agent-bot', is_bot: true }));
  } else if (req.url.startsWith('/api/v4/users/')) {
    res.end(JSON.stringify({ id: req.url.split('/').at(-1), is_bot: false }));
  } else if (req.url === '/api/v4/posts' && req.method === 'POST') {
    replies.push(body);
    res.end(JSON.stringify({ id: `reply-${replies.length}` }));
  } else { res.writeHead(404).end('{}'); }
});
const wss = new WebSocketServer({ server, path: '/api/v4/websocket' });
wss.on('connection', ws => {
  ws.on('message', raw => {
    const message = JSON.parse(raw.toString());
    if (message.action !== 'authentication_challenge' || message.data?.token !== 'offline-ui-fixture') { ws.close(); return; }
    sockets.add(ws);
    ws.send(JSON.stringify({ seq_reply: 1, status: 'OK' }));
  });
  ws.on('close', () => sockets.delete(ws));
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port)));

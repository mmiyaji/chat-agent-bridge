// Loopback-only notification E2E peer. Never connects to a chat service or model.
import http from 'node:http';
import { createRequire } from 'node:module';
import { writeFile } from 'node:fs/promises';
const { WebSocketServer } = createRequire(import.meta.url)('ws');
const infoFile = process.argv[2];
if (!infoFile) throw new Error('An isolated info file is required');
const replies = [];
const sockets = new WebSocketServer({ noServer: true });
let sequence = 0;
const server = http.createServer(async (req, res) => {
  res.setHeader('content-type', 'application/json');
  if (req.url === '/test/post' && req.method === 'POST') {
    const chunks = []; for await (const chunk of req) chunks.push(chunk);
    const input = JSON.parse(Buffer.concat(chunks).toString());
    const post = { id: `notification-${++sequence}`, user_id: input.user ?? 'test-member',
      channel_id: 'test-channel', root_id: '', message: `!cab ${input.text ?? 'notification offline test'}`, type: '' };
    for (const ws of sockets.clients) if (ws.readyState === ws.OPEN)
      ws.send(JSON.stringify({ event: 'posted', data: { post: JSON.stringify(post) } }));
    res.end(JSON.stringify({ id: post.id })); return;
  }
  if (req.url === '/test/replies') { res.end(JSON.stringify(replies)); return; }
  if (req.headers.authorization !== 'Bearer notification-fixture-token') { res.writeHead(401); res.end('{}'); return; }
  if (req.url === '/api/v4/users/me') { res.end('{"id":"test-bridge","is_bot":false}'); return; }
  if (req.url?.startsWith('/api/v4/users/')) { res.end('{"id":"test-member","is_bot":false}'); return; }
  if (req.url === '/api/v4/posts' && req.method === 'POST') {
    const chunks = []; for await (const chunk of req) chunks.push(chunk);
    replies.push(JSON.parse(Buffer.concat(chunks).toString()));
    res.end('{"id":"reply"}'); return;
  }
  res.writeHead(404); res.end('{}');
});
server.on('upgrade', (req, socket, head) => {
  if (req.url !== '/api/v4/websocket') { socket.destroy(); return; }
  sockets.handleUpgrade(req, socket, head, ws => sockets.emit('connection', ws));
});
sockets.on('connection', ws => ws.on('message', raw => {
  const m = JSON.parse(raw.toString());
  ws.send(JSON.stringify({ status: m.data?.token === 'notification-fixture-token' ? 'OK' : 'FAIL', seq_reply: m.seq }));
}));
server.listen(0, '127.0.0.1', async () => {
  await writeFile(infoFile, JSON.stringify({ url: `http://127.0.0.1:${server.address().port}`, pid: process.pid }));
});

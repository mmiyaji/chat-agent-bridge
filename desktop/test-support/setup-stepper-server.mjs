import http from 'node:http';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

const require = createRequire(import.meta.url);
const { WebSocketServer } = require('ws');
const args = new Map();
for (let i = 2; i < process.argv.length; i += 2) args.set(process.argv[i], process.argv[i + 1]);
const infoFile = args.get('--info-file');
const authDelayMs = Math.max(0, Number.parseInt(args.get('--auth-delay-ms') ?? '0', 10) || 0);
if (!infoFile) throw new Error('--info-file is required');

const server = http.createServer((request, response) => {
  if (request.method === 'GET' && request.url === '/api/v4/users/me') {
    if (request.headers.authorization !== 'Bearer stepper-fake-token') {
      response.writeHead(401, { 'content-type': 'application/json' });
      response.end('{"message":"test token rejected"}');
      return;
    }
    response.writeHead(200, { 'content-type': 'application/json' });
    response.end('{"id":"stepper-test-user","is_bot":false}');
    return;
  }
  response.writeHead(404);
  response.end();
});

const sockets = new WebSocketServer({ noServer: true });
server.on('upgrade', (request, socket, head) => {
  if (request.url !== '/api/v4/websocket') {
    socket.destroy();
    return;
  }
  sockets.handleUpgrade(request, socket, head, ws => sockets.emit('connection', ws, request));
});
sockets.on('connection', ws => {
  ws.on('message', async raw => {
    let message;
    try { message = JSON.parse(raw.toString()); } catch { ws.close(1002, 'invalid JSON'); return; }
    if (message.action !== 'authentication_challenge' || message.seq !== 1 || message.data?.token !== 'stepper-fake-token') {
      ws.send(JSON.stringify({ status: 'FAIL', seq_reply: message.seq ?? 0 }));
      return;
    }
    if (authDelayMs) await new Promise(resolve => setTimeout(resolve, authDelayMs));
    if (ws.readyState === ws.OPEN) ws.send(JSON.stringify({ status: 'OK', seq_reply: 1 }));
  });
});

server.listen(0, '127.0.0.1', async () => {
  const { writeFile } = await import('node:fs/promises');
  const { port } = server.address();
  await writeFile(infoFile, JSON.stringify({ url: `http://127.0.0.1:${port}`, pid: process.pid }));
});

async function stop() {
  for (const socket of sockets.clients) socket.close(1000, 'test complete');
  sockets.close();
  server.close(() => process.exit(0));
  setTimeout(() => process.exit(0), 1000).unref();
}
process.on('SIGINT', stop);
process.on('SIGTERM', stop);

if (import.meta.url === pathToFileURL(process.argv[1]).href) {
  process.on('uncaughtException', error => { console.error(error); process.exit(1); });
}

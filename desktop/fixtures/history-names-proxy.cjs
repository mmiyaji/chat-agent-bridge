// Loopback-only, read-only Mattermost API recorder for HistoryNamesUiE2E.ps1.
// Authorization headers and response bodies are forwarded but never recorded.
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');

const upstream = 'http://127.0.0.1:8070';
const allowed = [
  /^\/api\/v4\/users\/[A-Za-z0-9_-]+$/,
  /^\/api\/v4\/channels\/[A-Za-z0-9_-]+$/,
  /^\/api\/v4\/config\/client\?format=old$/,
];
let online = true;
let requests = [];

const server = http.createServer((req, res) => {
  if (req.url === '/__control/state' && req.method === 'GET') {
    res.writeHead(200, { 'content-type': 'application/json' });
    return res.end(JSON.stringify({ online, requests }));
  }
  if (req.url === '/__control/offline' && req.method === 'POST') {
    online = false; res.writeHead(204); return res.end();
  }
  if (req.url === '/__control/online' && req.method === 'POST') {
    online = true; res.writeHead(204); return res.end();
  }
  if (req.url === '/__control/clear' && req.method === 'POST') {
    requests = []; res.writeHead(204); return res.end();
  }
  if (req.method !== 'GET' || !allowed.some(pattern => pattern.test(req.url))) {
    res.writeHead(403, { 'content-type': 'text/plain' });
    return res.end('forbidden');
  }
  if (!online) {
    requests.push({ method: 'GET', path: req.url, status: 503 });
    res.writeHead(503, { 'content-type': 'text/plain' });
    return res.end('offline');
  }

  let recorded = false;
  const record = status => {
    if (recorded) return;
    recorded = true;
    requests.push({ method: 'GET', path: req.url, status });
  };
  const upstreamRequest = http.request(new URL(req.url, upstream), {
    method: 'GET',
    headers: {
      authorization: req.headers.authorization || '',
      accept: req.headers.accept || 'application/json',
    },
  }, upstreamResponse => {
    const chunks = [];
    upstreamResponse.on('data', chunk => chunks.push(chunk));
    upstreamResponse.on('end', () => {
      record(upstreamResponse.statusCode || 502);
      res.writeHead(upstreamResponse.statusCode || 502, {
        'content-type': upstreamResponse.headers['content-type'] || 'application/json',
      });
      res.end(Buffer.concat(chunks));
    });
  });
  upstreamRequest.setTimeout(5000, () => {
    record(504);
    upstreamRequest.destroy();
    if (!res.headersSent) { res.writeHead(504); res.end(); }
  });
  upstreamRequest.on('error', () => {
    record(502);
    if (!res.headersSent) { res.writeHead(502); res.end(); }
  });
  upstreamRequest.end();
});

server.listen(0, '127.0.0.1', () => {
  const address = server.address();
  const portFile = process.argv[2];
  fs.writeFileSync(portFile, String(address.port), { encoding: 'utf8', flag: 'wx' });
});

function stop() { server.close(() => process.exit(0)); }
process.on('SIGTERM', stop);
process.on('SIGINT', stop);

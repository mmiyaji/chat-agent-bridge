// Loopback-only Mattermost protocol fixture. No real credentials or external traffic.
const http = require('node:http');
const fs = require('node:fs');
const { WebSocketServer, WebSocket } = require('ws');
const portFile = process.argv[2];
if (!portFile) throw new Error('Provide an output file for the ephemeral loopback port.');
const replies = [];
const updates = [];
const reactions = [];
const reactionAdds = [];
const reactionRemoves = [];
let failProgressEdits = false;
let offlineUntil = 0;
let totalConnections = 0;
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
    res.end(JSON.stringify({ replies, updates, reactions, reactionAdds, reactionRemoves, connections: sockets.size, totalConnections }));
  } else if (req.url === '/test/progress-errors' && req.method === 'POST') {
    failProgressEdits = body.enabled === true;
    res.end('{}');
  } else if (req.url === '/test/disconnect' && req.method === 'POST') {
    offlineUntil = Date.now() + Math.min(10000, Math.max(0, Number(body.offlineMilliseconds) || 0));
    for (const ws of sockets) ws.close(1013, 'temporary test outage');
    res.end('{}');
  } else if (req.headers.authorization !== 'Bearer offline-ui-fixture') {
    res.writeHead(401).end('{}');
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

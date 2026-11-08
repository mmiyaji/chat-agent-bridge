const http = require('node:http');
const fs = require('node:fs');

const portFile = process.argv[2];
if (!portFile) throw new Error('Expected a port-file path.');
const server = http.createServer((req, res) => {
  res.setHeader('Content-Type', 'application/json');
  if (req.method === 'GET' && req.url === '/api/v4/users/me/teams') {
    res.end(JSON.stringify([{ id: 'team-fixture', display_name: 'CAB Test Team' }]));
    return;
  }
  if (req.method === 'GET' && req.url.startsWith('/api/v4/users/me/teams/team-fixture/channels?')) {
    res.end(JSON.stringify([
      { id: 'channel-cab-release', display_name: 'cab-release', type: 'O' },
      { id: 'channel-cab-ops', display_name: 'cab-ops', type: 'O' },
      { id: 'channel-general-fixture', display_name: 'general-fixture', type: 'O' }
    ]));
    return;
  }
  res.writeHead(404).end(JSON.stringify({ id: 'api.channel_picker_fixture.not_found', message: 'Unexpected fixture route' }));
});
server.listen(0, '127.0.0.1', () => fs.writeFileSync(portFile, String(server.address().port)));
process.on('SIGTERM', () => server.close(() => process.exit(0)));

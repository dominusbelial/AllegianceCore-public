// P1061 proof-only reverse proxy: the Caddy shape on loopback.
// TLS terminates here on https://localhost:8443; the lobby and game listener stay
// plaintext on their own loopback ports and receive exactly one forwarded hop.
// No arbitrary-address parameter, public binding, or production hosting path.
const https = require('node:https'), http = require('node:http'), net = require('node:net'), fs = require('node:fs'), path = require('node:path');
const cert = fs.readFileSync(process.argv[2]), key = fs.readFileSync(process.argv[3]);
const lobbyPort = Number(process.argv[4]), gamePort = Number(process.argv[5]), controls = path.resolve(process.argv[6]);
if (![lobbyPort, gamePort].every(p => Number.isInteger(p) && p > 0 && p < 65536)) throw Error('Invalid loopback port');
const origin = 'https://localhost:8443';
// The proof host is reached from a documentation-range client address, so the
// server's source accounting sees a non-loopback peer through the trusted proxy.
const clientAddress = '203.0.113.7';
const forwarded = { 'x-forwarded-for': clientAddress, 'x-forwarded-proto': 'https' };
const server = https.createServer({ cert, key }, (req, res) => {
  const upstream = http.request({ host: '127.0.0.1', port: lobbyPort, method: req.method, path: req.url,
    headers: { ...req.headers, ...forwarded } }, reply => {
    res.writeHead(reply.statusCode, reply.headers); reply.pipe(res);
  });
  upstream.on('error', () => { if (!res.headersSent) res.writeHead(502); res.end(); });
  req.pipe(upstream);
});
const sockets = new Set(), gameSockets = new Set();
const monitor = setInterval(() => {
  const drop = path.join(controls, 'drop-ws');
  if (fs.existsSync(drop)) { fs.unlinkSync(drop); for (const socket of gameSockets) socket.destroy(); }
}, 100);
server.on('connection', socket => { sockets.add(socket); socket.on('close', () => sockets.delete(socket)); });
server.on('upgrade', (req, socket, head) => {
  // P1058 proof control: while `hold-ws` exists the proxy refuses the upgrade,
  // so a dropped browser socket stays in the client's resume window long enough
  // to inspect the launch affordance.
  if (fs.existsSync(path.join(controls, 'hold-ws'))) { socket.destroy(); return; }
  if (req.url !== '/game') { socket.destroy(); return; }
  gameSockets.add(socket); socket.on('close', () => gameSockets.delete(socket));
  const upstream = net.connect(gamePort, '127.0.0.1', () => {
    upstream.write(`${req.method} ${req.url} HTTP/${req.httpVersion}\r\n`);
    const skip = new Set(['x-forwarded-for', 'x-forwarded-proto']);
    for (let i = 0; i < req.rawHeaders.length; i += 2)
      if (!skip.has(req.rawHeaders[i].toLowerCase())) upstream.write(`${req.rawHeaders[i]}: ${req.rawHeaders[i + 1]}\r\n`);
    upstream.write(`X-Forwarded-For: ${clientAddress}\r\nX-Forwarded-Proto: https\r\n\r\n`);
    if (head.length) upstream.write(head);
    socket.pipe(upstream); upstream.pipe(socket);
  });
  upstream.on('error', () => socket.destroy()); socket.on('error', () => upstream.destroy());
  socket.on('close', () => upstream.destroy()); upstream.on('close', () => socket.destroy());
});
server.listen(8443, '127.0.0.1', () => console.log('P1061_PROXY_READY ' + origin));
process.on('SIGTERM', () => { clearInterval(monitor); for (const socket of sockets) socket.destroy(); server.close(() => process.exit()); });

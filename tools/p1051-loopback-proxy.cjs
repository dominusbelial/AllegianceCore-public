// Proof-only fixed loopback proxy: unchanged bytes/headers on WS upgrades.
// No arbitrary-address parameter, public binding, or production hosting path.
const http = require('node:http'), net = require('node:net'), fs = require('node:fs'), path = require('node:path');
const root = path.resolve(process.argv[2]), gamePort = Number(process.argv[3]);
if (!Number.isInteger(gamePort) || gamePort < 1 || gamePort > 65535) throw Error('Invalid local game port');
const mime = { '.html':'text/html', '.js':'text/javascript', '.css':'text/css', '.json':'application/json', '.wasm':'application/wasm', '.png':'image/png' };
const server = http.createServer((req, res) => {
  const pathname = new URL(req.url, 'http://localhost:8048').pathname;
  if (pathname.startsWith('/api/') || ['/identity.html','/identity.js','/identity.css'].includes(pathname)) {
    const upstream = http.request({ host:'127.0.0.1', port:8049, method:req.method, path:req.url, headers:req.headers }, r => { res.writeHead(r.statusCode, r.headers); r.pipe(res); });
    upstream.on('error', () => { res.writeHead(502); res.end(); }); req.pipe(upstream); return;
  }
  const prefix = '/play/preview/';
  if (!pathname.startsWith(prefix)) { res.writeHead(404); res.end(); return; }
  let relative;
  try { relative = decodeURIComponent(pathname.slice(prefix.length) || 'index.html'); } catch { res.writeHead(400); res.end(); return; }
  if (relative.includes('\\') || relative.split('/').some(p => p === '..' || p === '.')) { res.writeHead(404); res.end(); return; }
  const file = path.resolve(root, relative);
  if (!file.startsWith(root + path.sep) || !fs.existsSync(file) || !fs.statSync(file).isFile()) { res.writeHead(404); res.end(); return; }
  let target = file, encoding;
  if ((req.headers['accept-encoding'] || '').includes('br') && fs.existsSync(file+'.br')) { target += '.br'; encoding='br'; }
  else if ((req.headers['accept-encoding'] || '').includes('gzip') && fs.existsSync(file+'.gz')) { target += '.gz'; encoding='gzip'; }
  res.setHeader('Content-Type', mime[path.extname(file)] || 'application/octet-stream');
  res.setHeader('Content-Length', fs.statSync(target).size);
  if (encoding) res.setHeader('Content-Encoding',encoding);
  res.setHeader('Cache-Control','no-cache');
  // Identical to the existing P1047 static proof host. No CSP changes.
  res.setHeader('Content-Security-Policy', "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; connect-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'");
  fs.createReadStream(target).pipe(res);
});
const sockets = new Set(), gameSockets = new Set();
const controls = path.resolve(process.argv[4]);
const monitor = setInterval(() => {
  const drop = path.join(controls, 'drop-ws');
  if (fs.existsSync(drop)) { fs.unlinkSync(drop); for (const socket of gameSockets) socket.destroy(); }
}, 100);
server.on('connection', socket => { sockets.add(socket); socket.on('close',()=>sockets.delete(socket)); });
server.on('upgrade', (req, socket, head) => {
  gameSockets.add(socket); socket.on('close', () => gameSockets.delete(socket));
  if (req.url !== '/game' || req.headers.origin !== 'http://localhost:8048') { socket.destroy(); return; }
  const upstream = net.connect(gamePort,'127.0.0.1', () => {
    upstream.write(`${req.method} ${req.url} HTTP/${req.httpVersion}\r\n`);
    for (let i=0;i<req.rawHeaders.length;i+=2) upstream.write(`${req.rawHeaders[i]}: ${req.rawHeaders[i+1]}\r\n`);
    upstream.write('\r\n'); if (head.length) upstream.write(head);
    socket.pipe(upstream); upstream.pipe(socket);
  });
  upstream.on('error',()=>socket.destroy()); socket.on('error',()=>upstream.destroy());
  socket.on('close',()=>upstream.destroy()); upstream.on('close',()=>socket.destroy());
});
server.listen(8048,'127.0.0.1',()=>console.log('P1051_PROXY_READY'));
process.on('SIGTERM',()=>{ clearInterval(monitor); for(const socket of sockets) socket.destroy(); server.close(()=>process.exit()); });

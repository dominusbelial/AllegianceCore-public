#!/usr/bin/env python3
"""Loopback-only compressed static host for the P1047 local proof (no directory listings)."""
import argparse
import hashlib
import http.server
import mimetypes
from pathlib import Path
from urllib.parse import unquote, urlsplit

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('root', type=Path)
parser.add_argument('--port', type=int, default=8047)
args = parser.parse_args()
root = args.root.resolve()
prefix = '/play/preview/'

class Handler(http.server.BaseHTTPRequestHandler):
    def do_HEAD(self):
        self.serve(False)

    def do_GET(self):
        self.serve(True)

    def serve(self, body):
        path = unquote(urlsplit(self.path).path)
        if not path.startswith(prefix) or '\\' in path or any(p in ('.', '..') for p in path.split('/')):
            self.send_error(404)
            return
        relative = path[len(prefix):] or 'index.html'
        target = root / relative
        if not target.is_file() or not target.resolve().is_relative_to(root) or target.suffix in ('.br', '.gz'):
            self.send_error(404)
            return
        encoding = None
        accepted = self.headers.get('Accept-Encoding', '').split(',')
        accepted = {a.strip().split(';')[0] for a in accepted if ';q=0' not in a}
        if 'br' in accepted and Path(str(target) + '.br').is_file():
            target = Path(str(target) + '.br'); encoding = 'br'
        elif 'gzip' in accepted and Path(str(target) + '.gz').is_file():
            target = Path(str(target) + '.gz'); encoding = 'gzip'
        data = target.read_bytes()
        etag = '"' + hashlib.sha256(data).hexdigest() + '"'
        unchanged = self.headers.get('If-None-Match') == etag
        self.send_response(304 if unchanged else 200)
        self.send_header('ETag', etag)
        self.send_header('Vary', 'Accept-Encoding')
        self.send_header('Cache-Control', 'public,max-age=31536000,immutable' if relative.startswith(('packs/', 'art/')) or relative.startswith('_framework/') else 'no-cache')
        self.send_header('Content-Type', 'application/octet-stream' if relative.endswith('.mdl') else 'application/wasm' if relative.endswith('.wasm') else mimetypes.guess_type(relative)[0] or 'application/octet-stream')
        self.send_header('Content-Security-Policy', "default-src 'none'; script-src 'self' 'wasm-unsafe-eval'; style-src 'self'; img-src 'self' blob:; media-src 'self' blob:; connect-src 'self'; base-uri 'self'; object-src 'none'; frame-ancestors 'none'")
        if encoding:
            self.send_header('Content-Encoding', encoding)
        if not unchanged:
            self.send_header('Content-Length', str(len(data)))
        self.end_headers()
        if body and not unchanged:
            self.wfile.write(data)

server = http.server.ThreadingHTTPServer(('127.0.0.1', args.port), Handler)
print(f'Loopback menu proof listening on port {args.port}', flush=True)
try:
    server.serve_forever()
except KeyboardInterrupt:
    pass
finally:
    server.server_close()

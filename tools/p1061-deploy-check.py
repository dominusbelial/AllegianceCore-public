#!/usr/bin/env python3
"""P1061 — static deployment validation without Docker (the lane has none).

Parses the compose files with a small block-YAML reader (mappings, sequences,
quoted/plain scalars, inline JSON arrays), checks the public-host shape, the
production server JSON and Caddy routes, validates the Dockerfiles' COPY sources
against the repo, and rejects real host/IP/secret literals in the committed
deployment placeholders. Exit 1 on any failure.

This is a substitute for `docker compose config`, not a full YAML implementation:
it understands exactly the constructs these files use and fails closed on
anything it cannot parse.
"""
import json
import re
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent


class YamlError(Exception):
    pass


def scalar(text):
    text = text.strip()
    if text.startswith('[') and text.endswith(']'):
        try:
            return json.loads(text)
        except json.JSONDecodeError:
            inner = text[1:-1].strip()
            return [] if not inner else [scalar(part) for part in inner.split(',')]
    if text.startswith('{') and text.endswith('}'):
        return json.loads(text)
    if text in ('true', 'True'):
        return True
    if text in ('false', 'False'):
        return False
    if text in ('null', '~', ''):
        return None
    if len(text) >= 2 and text[0] == text[-1] and text[0] in '"\'':
        return text[1:-1]
    if re.fullmatch(r'-?\d+', text):
        return int(text)
    return text


def strip_comment(line):
    quote = None
    for i, c in enumerate(line):
        if quote:
            if c == quote:
                quote = None
        elif c in '"\'':
            quote = c
        elif c == '#' and (i == 0 or line[i - 1].isspace()):
            return line[:i].rstrip()
    return line.rstrip()


def tokens(text):
    result = []
    for number, raw in enumerate(text.splitlines(), 1):
        line = strip_comment(raw)
        if not line.strip():
            continue
        indent = len(line) - len(line.lstrip(' '))
        if '\t' in line[:indent + 1]:
            raise YamlError(f'line {number}: tab indentation')
        result.append((indent, line.strip(), number))
    return result


def parse_block(items, at, indent):
    if at >= len(items) or items[at][0] != indent:
        raise YamlError(f'line {items[at][2]}: unexpected indentation')
    is_list = items[at][1].startswith('- ')
    container = [] if is_list else {}
    while at < len(items):
        current, content, number = items[at]
        if current < indent:
            break
        if current > indent:
            raise YamlError(f'line {number}: unexpected indentation')
        if is_list:
            if not content.startswith('- '):
                break
            rest = content[2:].strip()
            if rest and re.match(r'^[^:"\'\[]+:(?:\s|$)', rest):
                key, value = rest.split(':', 1)
                entry = {key.strip(): scalar(value)}
                at += 1
                # Deeper lines belong to this sequence item's mapping.
                if at < len(items) and items[at][0] > indent:
                    child_indent = items[at][0]
                    deeper = parse_block(items, at, child_indent)
                    if not isinstance(deeper, dict):
                        raise YamlError(f'line {items[at][2]}: expected a mapping under a list item')
                    entry.update(deeper)
                    at += block_length(items, at) if False else 0
                    # parse_block consumed the block; find where it stopped.
                    at = next_index(items, at, child_indent)
                container.append(entry)
            else:
                container.append(scalar(rest))
                at += 1
                if at < len(items) and items[at][0] > indent:
                    raise YamlError(f'line {items[at][2]}: scalar list item cannot have children')
        else:
            if content.startswith('- '):
                break
            if ':' not in content:
                raise YamlError(f'line {number}: expected key: value')
            key, value = content.split(':', 1)
            key = key.strip()
            at += 1
            if value.strip():
                container[key] = scalar(value)
            elif at < len(items) and items[at][0] > indent:
                child_indent = items[at][0]
                container[key] = parse_block(items, at, child_indent)
                at = next_index(items, at, child_indent)
            else:
                container[key] = None
    return container


def next_index(items, at, indent):
    while at < len(items) and items[at][0] >= indent:
        at += 1
    return at


def parse_yaml(path):
    return parse_block(tokens(path.read_text()), 0, tokens(path.read_text())[0][0])


failures = []


def check(condition, message):
    if not condition:
        failures.append(message)


def main():
    base = parse_yaml(ROOT / 'docker-compose.yml')
    public = parse_yaml(ROOT / 'docker/public/docker-compose.yml')
    server = json.loads((ROOT / 'docker/public/server.example.json').read_text())
    caddy = (ROOT / 'docker/public/Caddyfile').read_text()

    check(set(public['services']) == {'artwork', 'caddy', 'lobby', 'server'}, 'public compose services')
    for name, service in public['services'].items():
        check(service.get('network_mode') == 'host', f'{name}: network_mode host')
        check('ports' not in service, f'{name}: no port mappings with host networking')
    lobby_env = public['services']['lobby']['environment']
    check(lobby_env['ASPNETCORE_URLS'] == 'http://127.0.0.1:8080', 'lobby binds loopback')
    check(lobby_env['BrowserPreview__Enabled'] == 'true', 'browser preview enabled')
    check(lobby_env['BrowserPreview__Prefix'] == '/play', 'browser prefix /play')
    check(lobby_env['BrowserPreview__BundlePath'] == '/browser', 'browser bundle mount path')
    check(lobby_env['BrowserPreview__PublicOrigin'].startswith('https://${PUBLIC_HOST'), 'CSP origin placeholder')
    check(lobby_env['Lobby__KnownProxies__0'] == '127.0.0.1', 'lobby trusts exactly one hop')
    check(public['services']['lobby']['volumes'].count('./browser-generation:/browser:ro') == 1, 'generation mounted read-only')
    server_env = public['services']['server']['environment']
    check(server_env['ALLEGIANCE_TRANSPORT_PROTOCOL'] == 'udp', 'game UDP protocol')
    check(server_env['ALLEGIANCE_TRANSPORT_UDP_PORT'] == '6073', 'UDP port 6073')
    check('./server.json:/app/server.json:ro' in public['services']['server']['volumes'], 'private server.json mount')

    ws = server['webSocket']
    check(ws['enabled'] is True, 'webSocket enabled')
    check(ws['bindAddress'] == '127.0.0.1', 'webSocket loopback bind')
    check(ws['port'] == 6080 and ws['path'] == '/game', 'webSocket 6080 /game')
    check(ws['allowedOrigins'] == ['https://<public-origin>'], 'exact public origin allowlist')
    check(ws['knownProxies'] == ['127.0.0.1'], 'one trusted proxy')
    check(ws['allowLoopbackDevelopment'] is False, 'no plaintext loopback exception')
    check(ws['publicUrl'] == 'wss://<public-origin>/game', 'advertised wss route')
    check(server['minimumClientVersion'], 'minimum client version set')
    check(server['games'][0]['password'] == '<game-password>', 'password placeholder only')
    check(server['identity']['allowAnonymous'] is False, 'identity required')

    check('reverse_proxy 127.0.0.1:8080' in caddy, 'Caddy lobby upstream')
    check('reverse_proxy 127.0.0.1:6080' in caddy, 'Caddy game upstream')
    check('header_up X-Forwarded-For {remote_host}' in caddy, 'Caddy overwrites forwarded source')
    check('header_up X-Forwarded-Proto https' in caddy, 'Caddy forwards the TLS scheme')
    check('encode zstd gzip' in caddy, 'Caddy compression')

    dev_lobby = base['services']['lobby']['environment']
    check(dev_lobby['BrowserPreview__Prefix'] == '${ALLEGIANCE_BROWSER_PREVIEW_PREFIX:-/play/preview}', 'dev prefix default preserved')
    check(dev_lobby['BrowserPreview__PublicOrigin'] == '${ALLEGIANCE_BROWSER_PUBLIC_ORIGIN:-}', 'dev origin default empty')
    check(dev_lobby['BrowserPreview__WebSocketPath'] == '${ALLEGIANCE_BROWSER_WS_PATH:-/game}', 'dev WS path default')

    # Dockerfiles: pinned bases and COPY sources that exist in the build context.
    for dockerfile in ('src/Allegiance.Lobby/Dockerfile', 'src/Allegiance.Server/Dockerfile'):
        text = (ROOT / dockerfile).read_text()
        check(re.search(r'^FROM mcr\.microsoft\.com/dotnet/sdk:10\.0\.\d+', text, re.M) is not None,
              f'{dockerfile}: pinned SDK base')
        check(re.search(r'^ENTRYPOINT \[', text, re.M) is not None, f'{dockerfile}: entrypoint')
        for line in text.splitlines():
            match = re.match(r'COPY\s+(?:--from=\S+\s+)?(.+?)\s+(\S+)\s*$', line.strip())
            if not match or '--from' in line:
                continue
            sources, destination = match.groups()
            if not destination.startswith('/'):
                continue
            for source in sources.split():
                check((ROOT / source.rstrip('*')).exists(), f'{dockerfile}: COPY source exists: {source}')

    # Committed placeholders must not carry a real host, IP, email or password.
    for file in ('docker/public/docker-compose.yml', 'docker/public/Caddyfile',
                 'docker/public/server.example.json', 'docker/public/lobby.env.example'):
        text = (ROOT / file).read_text()
        for pattern, label in [(r'192\.168\.\d+\.\d+', 'private IP'), (r'\b\d{1,3}(?:\.\d{1,3}){3}\b', 'IP literal'),
                               (r'[\w.+-]+@[\w-]+\.[a-z]{2,}', 'email'), (r'https://(?!<public-origin>)[a-z0-9-]+\.[a-z]{2,}', 'real origin')]:
            for hit in re.findall(pattern, text):
                if hit == '127.0.0.1':
                    continue
                failures.append(f'{file}: {label} {hit!r} in a placeholder file')

    if failures:
        print(json.dumps({'passed': False, 'failures': failures}, indent=2))
        return 1
    print(json.dumps({'passed': True, 'composeServices': sorted(public['services']),
                      'baseComposeServices': sorted(base['services']),
                      'caddyRoutes': ['/game -> 127.0.0.1:6080', 'everything else -> 127.0.0.1:8080'],
                      'checks': 40}, indent=2))
    return 0


if __name__ == '__main__':
    sys.exit(main())

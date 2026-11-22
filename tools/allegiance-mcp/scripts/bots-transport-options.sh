#!/usr/bin/env bash
# Sourced by bot spawners; arrays preserve spaces and never evaluate arguments.
bot_extra=()
bot_approved=()
bot_ws_url=${ALLEGIANCE_BOT_WS_URL:-}
bot_seats=5
while (( $# )); do
  if (( $# < 2 )); then echo "missing value for $1" >&2; exit 2; fi
  case "$1" in
    --i-mean-it-host) bot_approved+=("$2");;
    --seats) bot_seats=$2;;
    --transport|--ws-origin|--ws-ca-file|--ws-fraction) bot_extra+=("$1" "$2");;
    --ws-url) bot_ws_url=$2; bot_extra+=("$1" "$2");;
    *) echo "unknown spawner option: $1" >&2; exit 2;;
  esac
  shift 2
done
python3 - "$bot_server" "$bot_ws_url" "${bot_approved[@]}" <<'PY'
import ipaddress
import sys
from urllib.parse import urlsplit
server, route, *approved = sys.argv[1:]
def loopback(host):
    if host.lower() == 'localhost':
        return True
    try:
        return ipaddress.ip_address(host.strip('[]')).is_loopback
    except ValueError:
        return False
hosts = [server]
if route:
    parsed = urlsplit(route)
    if not parsed.hostname or parsed.scheme not in ('ws', 'wss'):
        sys.exit('Invalid --ws-url')
    hosts.append(parsed.hostname)
for host in hosts:
    if not loopback(host) and host not in approved:
        sys.exit('Refusing non-loopback target ' + host + '; requires --i-mean-it-host ' + host)
PY

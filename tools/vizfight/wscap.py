#!/usr/bin/env python3
"""Raw-stdlib WebSocket capture client for the vizfight showcase.

Connects to ws://HOST:PORT/ws, performs the RFC6455 upgrade, then reads
frames for --seconds, recording every JSON message. No third-party deps.
Usage: python3 wscap.py PORT SECONDS OUT.jsonl
"""
import sys, socket, base64, hashlib, os, json, time

def handshake(port):
    key = base64.b64encode(os.urandom(16)).decode()
    sock = socket.create_connection(("127.0.0.1", port), timeout=5)
    req = (
        "GET /ws HTTP/1.1\r\n"
        f"Host: 127.0.0.1:{port}\r\n"
        "Upgrade: websocket\r\n"
        "Connection: Upgrade\r\n"
        f"Sec-WebSocket-Key: {key}\r\n"
        "Sec-WebSocket-Version: 13\r\n"
        "\r\n"
    )
    sock.sendall(req.encode())
    resp = b""
    while b"\r\n\r\n" not in resp:
        chunk = sock.recv(4096)
        if not chunk:
            raise RuntimeError("closed during handshake")
        resp += chunk
    if b" 101 " not in resp.split(b"\r\n", 1)[0]:
        raise RuntimeError(f"handshake failed: {resp[:200]!r}")
    return sock

def read_frame(sock):
    """Return payload bytes for one server->client text frame."""
    b = sock.recv(2)
    if len(b) < 2:
        return None
    b0, b1 = b[0], b[1]
    opcode = b0 & 0x0F
    masked = b1 & 0x80
    ln = b1 & 0x7F
    if ln == 126:
        ln = int.from_bytes(sock.recv(2), "big")
    elif ln == 127:
        ln = int.from_bytes(sock.recv(8), "big")
    mask = sock.recv(4) if masked else b""
    data = b""
    while len(data) < ln:
        data += sock.recv(ln - len(data))
    if masked:
        data = bytes(c ^ mask[i % 4] for i, c in enumerate(data))
    if opcode == 8:  # close
        return None
    if opcode != 1:  # only care about text
        return None
    return data

def main():
    port = int(sys.argv[1])
    seconds = float(sys.argv[2])
    out = sys.argv[3]
    sock = handshake(port)
    print("handshake OK")
    end = time.time() + seconds
    n = 0
    with open(out, "w") as f:
        sock.settimeout(seconds + 5)
        while time.time() < end:
            try:
                data = read_frame(sock)
            except socket.timeout:
                continue
            if data is None:
                continue
            try:
                obj = json.loads(data.decode("utf-8"))
            except Exception:
                continue
            f.write(json.dumps(obj) + "\n")
            f.flush()
            n += 1
    print(f"captured {n} frames")

if __name__ == "__main__":
    main()

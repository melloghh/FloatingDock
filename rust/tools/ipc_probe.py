"""Talks to a running engine over its IPC (for manual checks and parity runs).

  python rust/tools/ipc_probe.py <profile-dir> <method> [json-params]
  python rust/tools/ipc_probe.py <profile-dir> --events <seconds>   (subscribe to everything and print events)
"""
import json
import socket
import sys
import time
from pathlib import Path


def connect(profile):
    endpoint = (Path(profile) / "engine.endpoint").read_text().strip()
    _, port, token = endpoint.split(":")
    s = socket.create_connection(("127.0.0.1", int(port)), timeout=10)
    f = s.makefile("rwb", buffering=0)
    return s, f, token


def send(f, msg):
    f.write((json.dumps(msg) + "\n").encode())


def recv(f):
    line = f.readline()
    return json.loads(line) if line else None


def call(f, id_, method, params=None):
    send(f, {"id": id_, "method": method, "params": params or {}})
    while True:
        m = recv(f)
        if m is None:
            return None
        if m.get("id") == id_:
            return m


def main():
    profile = sys.argv[1]
    s, f, token = connect(profile)
    hello = call(f, 1, "hello", {"client": "probe", "protocol": 1, "token": token, "pid": 0})
    if sys.argv[2] == "--events":
        call(f, 2, "subscribe", {"topics": ["*"]})
        end = time.time() + float(sys.argv[3])
        s.settimeout(1)
        while time.time() < end:
            try:
                m = recv(f)
            except socket.timeout:
                continue
            if m is None:
                break
            print(json.dumps(m, ensure_ascii=False)[:300])
        return
    params = json.loads(sys.argv[3]) if len(sys.argv) > 3 else {}
    r = call(f, 2, sys.argv[2], params)
    print(json.dumps(r, ensure_ascii=False, indent=1)[:4000])


if __name__ == "__main__":
    main()

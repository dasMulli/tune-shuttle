"""Unauthenticated GET /info against one AirPlay 2 receiver (plain TCP, port 7000).

Used for docs/airplay2/SPEC-control.md section 1.8 (2026-10-09, the srcvers-366 speaker only).
Sends the headers pyatv's RtspSession.exchange() uses. Three variants, each on its
own connection. Prints raw response headers and the decoded bplist.

Usage: python -I get_info.py <receiver ip> [port]
"""
import json
import plistlib
import random
import socket
import sys
import time

HOST = sys.argv[1]
PORT = int(sys.argv[2]) if len(sys.argv) > 2 else 7000

dacp_id = f"{random.randrange(2 ** 64):X}"
active_remote = random.randrange(2 ** 32)


def request(protocol, body=None, cseq=0, extra_headers=None):
    hdrs = [
        f"GET /info {protocol}",
        f"CSeq: {cseq}",
        "User-Agent: AirPlay/550.10",
        f"DACP-ID: {dacp_id}",
        f"Active-Remote: {active_remote}",
        f"Client-Instance: {dacp_id}",
    ]
    if extra_headers:
        hdrs += extra_headers
    if body:
        hdrs.append("Content-Type: application/x-apple-binary-plist")
        hdrs.append(f"Content-Length: {len(body)}")
    msg = ("\r\n".join(hdrs) + "\r\n\r\n").encode() + (body or b"")
    s = socket.create_connection((HOST, PORT), timeout=8)
    s.sendall(msg)
    buf = b""
    deadline = time.time() + 8
    while time.time() < deadline:
        try:
            chunk = s.recv(65536)
        except socket.timeout:
            break
        if not chunk:
            break
        buf += chunk
        if b"\r\n\r\n" in buf:
            head, _, rest = buf.partition(b"\r\n\r\n")
            cl = 0
            for line in head.decode("latin1").split("\r\n"):
                if line.lower().startswith("content-length:"):
                    cl = int(line.split(":", 1)[1].strip())
            if len(rest) >= cl:
                break
    s.close()
    return msg, buf


def jsonable(obj):
    if isinstance(obj, bytes):
        return {"__bytes_hex__": obj.hex(), "__len__": len(obj)}
    if isinstance(obj, dict):
        return {k: jsonable(v) for k, v in obj.items()}
    if isinstance(obj, list):
        return [jsonable(v) for v in obj]
    return obj


def pstrings(data):
    out = []
    i = 0
    while i < len(data):
        ln = data[i]
        out.append(data[i + 1:i + 1 + ln].decode("utf-8", "replace"))
        i += 1 + ln
    return out


def show(label, msg, buf):
    print("=" * 78)
    print(label)
    print("--- request ---")
    print(msg[:400].decode("latin1", "replace"))
    print("--- response head ---")
    head, _, body = buf.partition(b"\r\n\r\n")
    print(head.decode("latin1", "replace"))
    print(f"--- body: {len(body)} bytes ---")
    if body.startswith(b"bplist"):
        pl = plistlib.loads(body)
        j = jsonable(pl)
        if isinstance(pl, dict) and isinstance(pl.get("txtAirPlay"), bytes):
            j["txtAirPlay_decoded"] = pstrings(pl["txtAirPlay"])
        if isinstance(pl, dict) and isinstance(pl.get("txtRAOP"), bytes):
            j["txtRAOP_decoded"] = pstrings(pl["txtRAOP"])
        print(json.dumps(j, indent=2, ensure_ascii=False, default=str))
        if isinstance(pl, dict):
            f = pl.get("features")
            if isinstance(f, int):
                print(f"features as hex: 0x{f:X}")
            sf = pl.get("statusFlags")
            if isinstance(sf, int):
                print(f"statusFlags as hex: 0x{sf:X}")
    else:
        print(body[:600])


v1 = request("RTSP/1.0", cseq=0)
show("Variant A: GET /info RTSP/1.0, no body (pyatv RtspSession.info)", *v1)

v2 = request("HTTP/1.1", cseq=0)
show("Variant B: GET /info HTTP/1.1, no body", *v2)

qual = plistlib.dumps({"qualifier": ["txtAirPlay"]}, fmt=plistlib.FMT_BINARY)
v3 = request("RTSP/1.0", body=qual, cseq=0,
             extra_headers=["X-Apple-ProtocolVersion: 1"])
show("Variant C: GET /info RTSP/1.0 with qualifier bplist (iOS stage 1)", *v3)

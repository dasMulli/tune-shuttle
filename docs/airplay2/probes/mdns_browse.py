"""Minimal passive mDNS browser used for docs/airplay2/SPEC-control.md section 1 (2026-10-09).

Sends one PTR query per service type and collects PTR/SRV/TXT/A/AAAA answers for a few
seconds. Raw sockets only, no third-party packages. Output: JSON with one entry per
service instance.

Usage: python -I mdns_browse.py <local ipv4 to bind> [seconds]
"""
import json
import socket
import struct
import sys
import time

MDNS_ADDR = "224.0.0.251"
MDNS_PORT = 5353
LOCAL_IP = sys.argv[1] if len(sys.argv) > 1 else "0.0.0.0"
SERVICES = ["_airplay._tcp.local", "_raop._tcp.local"]
LISTEN_SECONDS = float(sys.argv[2]) if len(sys.argv) > 2 else 4.0


def encode_name(name):
    out = b""
    for label in name.strip(".").split("."):
        out += bytes([len(label)]) + label.encode()
    return out + b"\x00"


def build_query(names):
    header = struct.pack("!HHHHHH", 0, 0, len(names), 0, 0, 0)
    body = b""
    for n in names:
        body += encode_name(n) + struct.pack("!HH", 12, 1)  # PTR, IN
    return header + body


def read_name(pkt, off):
    labels = []
    jumped = False
    end = None
    guard = 0
    while True:
        guard += 1
        if guard > 200:
            raise ValueError("name loop")
        ln = pkt[off]
        if ln == 0:
            off += 1
            break
        if ln & 0xC0 == 0xC0:
            ptr = struct.unpack("!H", pkt[off:off + 2])[0] & 0x3FFF
            if not jumped:
                end = off + 2
            jumped = True
            off = ptr
            continue
        labels.append(pkt[off + 1:off + 1 + ln].decode("utf-8", "replace"))
        off += 1 + ln
    if end is None:
        end = off
    return ".".join(labels), end


def parse(pkt):
    qd, an, ns, ar = struct.unpack("!HHHH", pkt[4:12])
    off = 12
    for _ in range(qd):
        _, off = read_name(pkt, off)
        off += 4
    records = []
    for _ in range(an + ns + ar):
        name, off = read_name(pkt, off)
        rtype, rclass, ttl, rdlen = struct.unpack("!HHIH", pkt[off:off + 10])
        off += 10
        rdata = pkt[off:off + rdlen]
        rstart = off
        off += rdlen
        if rtype == 12:
            target, _ = read_name(pkt, rstart)
            records.append((name, "PTR", target))
        elif rtype == 33:
            prio, weight, port = struct.unpack("!HHH", rdata[:6])
            target, _ = read_name(pkt, rstart + 6)
            records.append((name, "SRV", {"port": port, "target": target}))
        elif rtype == 16:
            txt = []
            i = 0
            while i < len(rdata):
                ln = rdata[i]
                txt.append(rdata[i + 1:i + 1 + ln].decode("utf-8", "replace"))
                i += 1 + ln
            records.append((name, "TXT", txt))
        elif rtype == 1:
            records.append((name, "A", socket.inet_ntoa(rdata)))
        elif rtype == 28:
            records.append((name, "AAAA", socket.inet_ntop(socket.AF_INET6, rdata)))
    return records


def main():
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM, socket.IPPROTO_UDP)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    sock.bind((LOCAL_IP, 0))
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_IF, socket.inet_aton(LOCAL_IP))
    sock.setsockopt(socket.IPPROTO_IP, socket.IP_MULTICAST_TTL, 255)
    sock.settimeout(0.5)

    query = build_query(SERVICES)
    sock.sendto(query, (MDNS_ADDR, MDNS_PORT))
    time.sleep(0.3)
    sock.sendto(query, (MDNS_ADDR, MDNS_PORT))

    instances = {}
    hosts = {}
    deadline = time.time() + LISTEN_SECONDS
    while time.time() < deadline:
        try:
            pkt, addr = sock.recvfrom(9000)
        except socket.timeout:
            continue
        try:
            recs = parse(pkt)
        except Exception as ex:  # noqa: BLE001
            print("parse error", ex, file=sys.stderr)
            continue
        for name, rtype, val in recs:
            if rtype == "PTR":
                instances.setdefault(val, {"type": name, "from": addr[0]})
            elif rtype == "SRV":
                instances.setdefault(name, {"from": addr[0]})["srv"] = val
            elif rtype == "TXT":
                instances.setdefault(name, {"from": addr[0]})["txt"] = val
            elif rtype in ("A", "AAAA"):
                hosts.setdefault(name, []).append(val)
    for inst in instances.values():
        srv = inst.get("srv")
        if srv:
            inst["addresses"] = hosts.get(srv["target"], [])
    print(json.dumps(instances, indent=2, ensure_ascii=False))


if __name__ == "__main__":
    main()

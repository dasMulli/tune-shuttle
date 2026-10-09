"""Independent generator for the RTP worked-example vector in RtpPacketTests (SPEC-audio.md 1.3).

Pure-Python ChaCha20-Poly1305 (RFC 8439), checked against RFC 8439 section 2.8.2 before use, so the C# test does not
compare the BCL against itself. Run: python -I gen_rtp_vector.py
"""
import struct


def rotl(v, c):
    return ((v << c) & 0xFFFFFFFF) | (v >> (32 - c))


def quarter(s, a, b, c, d):
    s[a] = (s[a] + s[b]) & 0xFFFFFFFF; s[d] = rotl(s[d] ^ s[a], 16)
    s[c] = (s[c] + s[d]) & 0xFFFFFFFF; s[b] = rotl(s[b] ^ s[c], 12)
    s[a] = (s[a] + s[b]) & 0xFFFFFFFF; s[d] = rotl(s[d] ^ s[a], 8)
    s[c] = (s[c] + s[d]) & 0xFFFFFFFF; s[b] = rotl(s[b] ^ s[c], 7)


def block(key, counter, nonce):
    state = [0x61707865, 0x3320646E, 0x79622D32, 0x6B206574]
    state += list(struct.unpack('<8I', key)) + [counter] + list(struct.unpack('<3I', nonce))
    w = state[:]
    for _ in range(10):
        quarter(w, 0, 4, 8, 12); quarter(w, 1, 5, 9, 13); quarter(w, 2, 6, 10, 14); quarter(w, 3, 7, 11, 15)
        quarter(w, 0, 5, 10, 15); quarter(w, 1, 6, 11, 12); quarter(w, 2, 7, 8, 13); quarter(w, 3, 4, 9, 14)
    return struct.pack('<16I', *[(w[i] + state[i]) & 0xFFFFFFFF for i in range(16)])


def chacha20(key, counter, nonce, data):
    out = bytearray()
    for i in range(0, len(data), 64):
        ks = block(key, counter + i // 64, nonce)
        out += bytes(a ^ b for a, b in zip(data[i:i + 64], ks))
    return bytes(out)


def poly1305(key, msg):
    r = int.from_bytes(key[:16], 'little') & 0x0FFFFFFC0FFFFFFC0FFFFFFC0FFFFFFF
    s = int.from_bytes(key[16:], 'little')
    p = (1 << 130) - 5
    acc = 0
    for i in range(0, len(msg), 16):
        n = int.from_bytes(msg[i:i + 16] + b'\x01', 'little')
        acc = ((acc + n) * r) % p
    return ((acc + s) & ((1 << 128) - 1)).to_bytes(16, 'little')


def pad16(b):
    return b'' if len(b) % 16 == 0 else b'\x00' * (16 - len(b) % 16)


def seal(key, nonce, plaintext, aad):
    otk = block(key, 0, nonce)[:32]
    ct = chacha20(key, 1, nonce, plaintext)
    mac = aad + pad16(aad) + ct + pad16(ct) + struct.pack('<QQ', len(aad), len(ct))
    return ct, poly1305(otk, mac)


# RFC 8439 2.8.2 self-check.
k = bytes(range(0x80, 0xA0))
n = bytes.fromhex('070000004041424344454647')
a = bytes.fromhex('50515253c0c1c2c3c4c5c6c7')
pt = b"Ladies and Gentlemen of the class of '99: If I could offer you only one tip for the future, sunscreen would be it."
ct, tag = seal(k, n, pt, a)
assert ct.hex().startswith('d31a8d34648e60db7b86afbc53ef7ec2') and tag.hex() == '1ae10b594f09e26a7e902ecbd0600691'

# SPEC-audio.md 1.3 worked example: seq 0x1234, ts 0x00015888, SSRC 0xDEADBEEF, counter 7, marker clear.
# Key: bytes 0x00..0x1f. Plaintext: the "silence" ALAC frame from docs/airplay2/test-vectors/alac-44100-s16-2ch-352.json.
key = bytes(range(32))
alac = bytes.fromhex('200000040013080981f8c1ff80000013080981f8c1ff800000ff80afbfe02bfc')
header = bytes.fromhex('80601234000158 88deadbeef'.replace(' ', ''))
nonce = b'\x00' * 4 + struct.pack('<Q', 7)
ct, tag = seal(key, nonce, alac, header[4:12])
print('packet =', (header + ct + tag + nonce[4:]).hex())

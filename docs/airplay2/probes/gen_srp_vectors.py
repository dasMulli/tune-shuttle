"""Generates SRP-6a vectors with the HomeKit parameters (RFC 5054 3072-bit group, g=5, SHA-512, I="Pair-Setup"-style
flow) from the RFC 5054 appendix B inputs (I="alice", P="password123", s, a, b), using srptools as an independent
implementation. Output: JSON. Regenerate: python -I gen_srp_vectors.py > srp-3072-sha512.json"""
import hashlib, json
from srptools import SRPContext
from srptools.constants import PRIME_3072, PRIME_3072_GEN
from srptools.utils import int_from_hex, int_to_bytes

def vec(I, P, s_hex, a_hex, b_hex):
    ctx = SRPContext(I, P, prime=PRIME_3072, generator=PRIME_3072_GEN, hash_func=hashlib.sha512)
    s = int_from_hex(s_hex); a = int_from_hex(a_hex); b = int_from_hex(b_hex)
    x = ctx.get_common_password_hash(s)
    v = ctx.get_common_password_verifier(x)
    k = ctx._mult
    A = ctx.get_client_public(a)
    B = ctx.get_server_public(v, b)
    u = ctx.get_common_secret(B, A)
    S_client = ctx.get_client_premaster_secret(x, B, a, u)
    S_server = ctx.get_server_premaster_secret(v, b, A, u)
    assert S_client == S_server
    K = ctx.get_common_session_key(S_client)
    M1 = ctx.get_common_session_key_proof(K, s, B, A)
    M2 = ctx.get_common_session_key_proof_hash(K, M1, A)
    # sanity: the HAP/pair_ap encoding uses fixed-width hash outputs; check no leading zero bytes were stripped
    hn = hashlib.sha512(int_to_bytes(int_from_hex(PRIME_3072))).digest(); hg = hashlib.sha512(int_to_bytes(int_from_hex(PRIME_3072_GEN))).digest()
    xor = bytes(p ^ q for p, q in zip(hn, hg)); assert xor[0] != 0, "xor leading byte zero: srptools would strip it"
    for name, val in (("s", s), ("A", A), ("B", B)):
        assert int_to_bytes(val)[0] != 0, name
    h = lambda n: int_to_bytes(n).hex().upper()
    return {"I": I, "P": P, "s": s_hex, "a": a_hex, "b": b_hex, "x": h(x), "v": h(v), "k": h(k), "A": h(A), "B": h(B), "u": h(u), "S": h(S_client), "K": K.hex().upper(), "M1": M1.hex().upper(), "M2": M2.hex().upper()}

out = {"description": "SRP-6a with the HomeKit parameters: N = RFC 5054 3072-bit group, g = 5, H = SHA-512, k = H(N || PAD(g)), u = H(PAD(A) || PAD(B)), x = H(s || H(I || ':' || P)), K = H(S), M1 = H((H(N) xor H(g)) || H(I) || s || A || B || K), M2 = H(A || M1 || K). Inputs from RFC 5054 appendix B; values generated with srptools (independent Python implementation), see docs/airplay2/probes/gen_srp_vectors.py.",
       "vectors": [vec("alice", "password123", "BEB25379D1A8581EB5A727673A2441EE", "60975527035CF2AD1989806F0407210BC81EDC04E2762A56AFD529DDDA2D4393", "E487CB59D31AC550471E81F00F6928E01DDA08E974A004F49E61F5D105284D20"),
                   vec("Pair-Setup", "3939", "0F1E2D3C4B5A69788796A5B4C3D2E1F0", "1BE63F1F9D6C0C4DF5C2B3A6D77E3E4F0A9B8C7D6E5F40312233445566778899AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899", "9A8B7C6D5E4F30211223344556677889")]}
print(json.dumps(out, indent=2))

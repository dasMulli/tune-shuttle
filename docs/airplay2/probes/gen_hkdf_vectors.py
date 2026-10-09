# Independent HKDF-SHA512 known answers (RFC 5869 extract+expand, Python stdlib only).
import hmac, hashlib
def hkdf(ikm, salt, info, length=32):
    prk = hmac.new(salt, ikm, hashlib.sha512).digest()
    okm = b""; t = b""; i = 1
    while len(okm) < length:
        t = hmac.new(prk, t + info + bytes([i]), hashlib.sha512).digest(); okm += t; i += 1
    return okm[:length]
ss = bytes(range(0x10, 0x50))  # 64-byte fixed "transient" shared secret 10 11 ... 4f
ss32 = bytes(range(0x20, 0x40)) # 32-byte fixed "verified" shared secret 20 21 ... 3f
for name, s in (("ss64", ss), ("ss32", ss32)):
    print(name, s.hex())
    for salt, info in (("Control-Salt","Control-Write-Encryption-Key"),("Control-Salt","Control-Read-Encryption-Key"),
                       ("Events-Salt","Events-Write-Encryption-Key"),("Events-Salt","Events-Read-Encryption-Key"),
                       ("Pair-Setup-Encrypt-Salt","Pair-Setup-Encrypt-Info"),("Pair-Verify-Encrypt-Salt","Pair-Verify-Encrypt-Info")):
        print(" ", salt, info, hkdf(s, salt.encode(), info.encode()).hex())
# cross-check against python's own hashlib-free path: RFC 5869 test case 1 with sha256 to prove the function
def hkdf256(ikm, salt, info, length):
    prk = hmac.new(salt, ikm, hashlib.sha256).digest()
    okm = b""; t = b""; i = 1
    while len(okm) < length:
        t = hmac.new(prk, t + info + bytes([i]), hashlib.sha256).digest(); okm += t; i += 1
    return okm[:length]
assert hkdf256(bytes.fromhex("0b"*22), bytes.fromhex("000102030405060708090a0b0c"), bytes.fromhex("f0f1f2f3f4f5f6f7f8f9"), 42).hex() == "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
print("rfc5869 self-check ok")

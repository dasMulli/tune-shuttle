// Generates ALAC test vectors with Apple's reference encoder at AirPlay parameters:
// 44100 Hz, 16-bit, 2 channels, 352 frames per packet. Output: JSON on stdout.
#include <cstdio>
#include <cstdint>
#include <cstring>
#include <cstdlib>
#include <string>
#include <vector>
#include <cmath>
#include "ALACEncoder.h"
#include "ALACDecoder.h"
#include "ALACBitUtilities.h"
#include "EndianPortable.h"

static const uint32_t kFrames = 352;
static std::string hex(const uint8_t* p, size_t n) {
    static const char* d = "0123456789abcdef"; std::string s; s.reserve(2*n);
    for (size_t i = 0; i < n; i++) { s += d[p[i] >> 4]; s += d[p[i] & 15]; }
    return s;
}
static uint32_t lcg = 0x12345678;
static int16_t prand() { lcg = lcg * 1664525u + 1013904223u; return (int16_t)(lcg >> 16); }

int main() {
    AudioFormatDescription in = {}, out = {};
    in.mSampleRate = 44100; in.mFormatID = kALACFormatLinearPCM;
    in.mFormatFlags = kALACFormatFlagIsSignedInteger | kALACFormatFlagIsPacked; // native endian int
    in.mBytesPerPacket = 4; in.mFramesPerPacket = 1; in.mBytesPerFrame = 4; in.mChannelsPerFrame = 2; in.mBitsPerChannel = 16;
    out.mSampleRate = 44100; out.mFormatID = kALACFormatAppleLossless; out.mFormatFlags = 1; // 1 => 16-bit
    out.mFramesPerPacket = kFrames; out.mChannelsPerFrame = 2;

    ALACEncoder enc; enc.SetFrameSize(kFrames);
    if (enc.InitializeEncoder(out) != 0) { fprintf(stderr, "init failed\n"); return 1; }
    uint8_t cookie[64]; uint32_t cookieSize = sizeof(cookie); enc.GetMagicCookie(cookie, &cookieSize);

    ALACDecoder dec; dec.Init(cookie, cookieSize);

    struct Case { const char* name; std::vector<int16_t> pcm; };
    std::vector<Case> cases;
    { Case c{"silence", std::vector<int16_t>(kFrames*2, 0)}; cases.push_back(c); }
    { Case c{"dc_plus_1000_minus_1000", {}}; for (uint32_t i=0;i<kFrames;i++){c.pcm.push_back(1000);c.pcm.push_back(-1000);} cases.push_back(c); }
    { Case c{"sine_440_1k", {}}; for (uint32_t i=0;i<kFrames;i++){ c.pcm.push_back((int16_t)lrint(12000*sin(2*M_PI*440*i/44100.0))); c.pcm.push_back((int16_t)lrint(8000*sin(2*M_PI*1000*i/44100.0))); } cases.push_back(c); }
    { Case c{"ramp_full_scale", {}}; for (uint32_t i=0;i<kFrames;i++){ int16_t v=(int16_t)(-32768 + (int)((65535.0*i)/(kFrames-1))); c.pcm.push_back(v); c.pcm.push_back((int16_t)-v); } cases.push_back(c); }
    { Case c{"white_noise_lcg_0x12345678", {}}; for (uint32_t i=0;i<kFrames*2;i++) c.pcm.push_back(prand()); cases.push_back(c); }
    { Case c{"sine_440_1k_second_packet", {}}; for (uint32_t i=kFrames;i<2*kFrames;i++){ c.pcm.push_back((int16_t)lrint(12000*sin(2*M_PI*440*i/44100.0))); c.pcm.push_back((int16_t)lrint(8000*sin(2*M_PI*1000*i/44100.0))); } cases.push_back(c); }

    printf("{\n  \"format\": \"44100 Hz, 16-bit signed little-endian interleaved stereo, 352 frames per packet; encoder state carries over between packets in listed order\",\n");
    printf("  \"magic_cookie_hex\": \"%s\",\n  \"packets\": [\n", hex(cookie, cookieSize).c_str());
    std::vector<uint8_t> outbuf(kFrames*2*4 + 64);
    for (size_t k = 0; k < cases.size(); k++) {
        Case& c = cases[k];
        std::vector<uint8_t> pcmBytes(kFrames*2*2);
        for (size_t i=0;i<kFrames*2;i++){ pcmBytes[2*i]=(uint8_t)(c.pcm[i]&0xff); pcmBytes[2*i+1]=(uint8_t)((c.pcm[i]>>8)&0xff); }
        // encoder wants native-endian 16-bit; x86 is little-endian so bytes == native
        int32_t n = (int32_t)pcmBytes.size();
        int32_t st = enc.Encode(in, out, pcmBytes.data(), outbuf.data(), &n);
        if (st != 0) { fprintf(stderr, "encode failed %d\n", st); return 1; }
        // round trip
        BitBuffer bb; BitBufferInit(&bb, outbuf.data(), n);
        std::vector<uint8_t> decoded(kFrames*2*2); uint32_t got = 0;
        int32_t ds = dec.Decode(&bb, decoded.data(), kFrames, 2, &got);
        bool ok = (ds == 0 && got == kFrames && memcmp(decoded.data(), pcmBytes.data(), pcmBytes.size()) == 0);
        printf("    {\"name\": \"%s\", \"pcm_le_hex\": \"%s\", \"alac_hex\": \"%s\", \"alac_len\": %d, \"roundtrip_ok\": %s}%s\n",
               c.name, hex(pcmBytes.data(), pcmBytes.size()).c_str(), hex(outbuf.data(), n).c_str(), n, ok ? "true" : "false", k+1<cases.size() ? "," : "");
    }
    printf("  ]\n}\n");
    return 0;
}

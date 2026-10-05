// Loads flarial_bcrypt_shim.dll, then calls bcrypt!BCryptEncrypt resolved through GetProcAddress (so it goes through the patched
// export table) and writes ciphertexts for run.sh to decrypt with OpenSSL. Usage: shim_test.exe <shim.dll> <modulus.bin> <e.bin>
#include <windows.h>
#include <bcrypt.h>
#include <cstdio>
#include <cstring>
#include <vector>
#pragma comment(lib, "bcrypt.lib")
typedef NTSTATUS(WINAPI* Enc)(BCRYPT_KEY_HANDLE, PUCHAR, ULONG, VOID*, PUCHAR, ULONG, PUCHAR, ULONG, ULONG*, ULONG);
static std::vector<unsigned char> Read(const char* p) { std::vector<unsigned char> v(4096); FILE* f = fopen(p, "rb"); v.resize(fread(v.data(), 1, v.size(), f)); fclose(f); return v; }
static int fails = 0;
#define EXPECT(cond, what) do { bool ok_ = (cond); printf("%s %s\n", ok_ ? "PASS" : "FAIL", what); if (!ok_) ++fails; } while (0)
int main(int, char** argv) {
    if (!LoadLibraryA(argv[1])) { puts("FAIL cannot load shim"); return 2; }
    auto enc = (Enc)GetProcAddress(GetModuleHandleW(L"bcrypt.dll"), "BCryptEncrypt");
    auto mod = Read(argv[2]); auto e = Read(argv[3]);
    std::vector<unsigned char> blob(sizeof(BCRYPT_RSAKEY_BLOB) + e.size() + mod.size());
    *(BCRYPT_RSAKEY_BLOB*)blob.data() = {BCRYPT_RSAPUBLIC_MAGIC, (ULONG)(mod.size() * 8), (ULONG)e.size(), (ULONG)mod.size(), 0, 0};
    memcpy(blob.data() + sizeof(BCRYPT_RSAKEY_BLOB), e.data(), e.size());
    memcpy(blob.data() + sizeof(BCRYPT_RSAKEY_BLOB) + e.size(), mod.data(), mod.size());
    BCRYPT_ALG_HANDLE rsa = 0; BCRYPT_KEY_HANDLE key = 0;
    BCryptOpenAlgorithmProvider(&rsa, BCRYPT_RSA_ALGORITHM, nullptr, 0);
    EXPECT(BCryptImportKeyPair(rsa, nullptr, BCRYPT_RSAPUBLIC_BLOB, &key, blob.data(), (ULONG)blob.size(), 0) == 0, "import public key");
    unsigned char msg[32]; for (int i = 0; i < 32; ++i) msg[i] = (unsigned char)(i * 7 + 1);
    unsigned char out[512]; ULONG w = 0;
    struct Case { const wchar_t* alg; const char* label; const char* file; } cases[] = {
        {BCRYPT_SHA256_ALGORITHM, nullptr, "ct_sha256.bin"}, {BCRYPT_SHA1_ALGORITHM, nullptr, "ct_sha1.bin"}, {BCRYPT_SHA384_ALGORITHM, "flarial", "ct_sha384_label.bin"}};
    for (auto& c : cases) {
        BCRYPT_OAEP_PADDING_INFO pad{(LPWSTR)c.alg, (PUCHAR)c.label, c.label ? (ULONG)strlen(c.label) : 0};
        w = 0; EXPECT(enc(key, msg, 32, &pad, nullptr, 0, nullptr, 0, &w, BCRYPT_PAD_OAEP) == 0 && w == mod.size(), "OAEP size query (NULL output)");
        NTSTATUS s = enc(key, msg, 32, &pad, nullptr, 0, out, (ULONG)sizeof out, &w, BCRYPT_PAD_OAEP);
        EXPECT(s == 0 && w == mod.size(), c.file);
        FILE* f = fopen(c.file, "wb"); fwrite(out, 1, w, f); fclose(f);
        // two encryptions of the same message differ (random seed)
        unsigned char again[512]; ULONG w2 = 0; enc(key, msg, 32, &pad, nullptr, 0, again, (ULONG)sizeof again, &w2, BCRYPT_PAD_OAEP);
        EXPECT(w2 == w && memcmp(again, out, w) != 0, "OAEP is randomized");
    }
    BCRYPT_OAEP_PADDING_INFO pad{(LPWSTR)BCRYPT_SHA256_ALGORITHM, nullptr, 0};
    EXPECT(enc(key, msg, 32, &pad, nullptr, 0, out, 16, &w, BCRYPT_PAD_OAEP) == (NTSTATUS)0xC0000023, "short output buffer -> STATUS_BUFFER_TOO_SMALL");
    unsigned char big[300] = {1};
    EXPECT(enc(key, big, 300, &pad, nullptr, 0, out, 512, &w, BCRYPT_PAD_OAEP) != 0, "oversized message rejected");
    EXPECT(enc(key, msg, 32, nullptr, nullptr, 0, out, 512, &w, BCRYPT_PAD_PKCS1) == 0 && w == mod.size(), "PKCS1 passes through unchanged");
    {   // AES-GCM passes through unchanged
        BCRYPT_ALG_HANDLE aes = 0; BCRYPT_KEY_HANDLE k = 0; unsigned char kb[32] = {2}, nonce[12] = {3}, tag[16], pt[40] = {4}, ct[40];
        BCryptOpenAlgorithmProvider(&aes, BCRYPT_AES_ALGORITHM, nullptr, 0);
        BCryptSetProperty(aes, BCRYPT_CHAINING_MODE, (PUCHAR)BCRYPT_CHAIN_MODE_GCM, sizeof(BCRYPT_CHAIN_MODE_GCM), 0);
        BCryptGenerateSymmetricKey(aes, &k, nullptr, 0, kb, 32, 0);
        BCRYPT_AUTHENTICATED_CIPHER_MODE_INFO ai; BCRYPT_INIT_AUTH_MODE_INFO(ai);
        ai.pbNonce = nonce; ai.cbNonce = 12; ai.pbTag = tag; ai.cbTag = 16;
        EXPECT(enc(k, pt, 40, &ai, nullptr, 0, ct, 40, &w, 0) == 0 && w == 40, "AES-GCM passes through unchanged");
    }
    printf("%s\n", fails ? "RESULT: FAILURES" : "RESULT: ALL PASS");
    return fails ? 1 : 0;
}

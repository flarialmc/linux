// SPDX-License-Identifier: GPL-3.0-or-later
// Copyright (c) 2026 Flarial contributors
//
// Wine compatibility shim: RSA-OAEP public-key encryption through bcrypt.
//
// Wine's bcrypt (checked on wine-11.1, GDK-Proton) rejects BCryptEncrypt(key, ..., BCRYPT_OAEP_PADDING_INFO, ..., BCRYPT_PAD_OAEP)
// with STATUS_INVALID_PARAMETER when an output buffer is supplied ("key_asymmetric_encrypt padding info not found"); the size
// query (pbOutput == NULL) succeeds. The Flarial client wraps its API gateway key with exactly that call, so under Wine every
// gateway request (presence, heartbeat, events, featured servers) failed before reaching the network.
//
// The launcher loads this DLL into Minecraft before the client. It redirects the bcrypt.dll export table entry of BCryptEncrypt
// to a stub that forwards to the original and handles ONLY the case Wine rejects:
//   pPaddingInfo != NULL, BCRYPT_PAD_OAEP, pbOutput != NULL, original returned STATUS_INVALID_PARAMETER, RSA public key.
// There it performs EME-OAEP (RFC 8017 7.1.1, MGF1 with the requested hash, optional label) itself, using bcrypt for hashing and
// randomness, and the raw RSA public operation with a small bignum (public data only, no constant time needed). Every other call
// is passed through untouched. If anything about the installation looks unexpected the shim leaves bcrypt alone (fail open).
// Never logs key material, plaintext, or ciphertext.
//
// Build: see build-bcrypt-shim.sh. Test: tests/bcrypt-shim/ (round-trips against OpenSSL).
#include <windows.h>
#include <bcrypt.h>
#include <cstdint>
#include <cstring>
#include <vector>

#ifndef STATUS_INVALID_PARAMETER
#define STATUS_INVALID_PARAMETER ((NTSTATUS)0xC000000D)
#endif
#ifndef STATUS_BUFFER_TOO_SMALL
#define STATUS_BUFFER_TOO_SMALL ((NTSTATUS)0xC0000023)
#endif
#ifndef STATUS_SUCCESS
#define STATUS_SUCCESS ((NTSTATUS)0)
#endif

namespace {

using EncryptFn = NTSTATUS(WINAPI*)(BCRYPT_KEY_HANDLE, PUCHAR, ULONG, VOID*, PUCHAR, ULONG, PUCHAR, ULONG, ULONG*, ULONG);
EncryptFn g_original = nullptr;
volatile LONG g_installed = 0;
volatile LONG g_logged = 0;

void Log(const char* text) { OutputDebugStringA(text); }

// ---- bignum (little-endian 32-bit limbs) ----------------------------------------------------------------------------------
using Big = std::vector<std::uint32_t>;

Big FromBE(const unsigned char* p, size_t n, size_t limbs) {
    Big r(limbs, 0);
    for (size_t i = 0; i < n; ++i) r[(n - 1 - i) / 4] |= std::uint32_t(p[i]) << (8 * ((n - 1 - i) % 4));
    return r;
}
bool Geq(const Big& a, const Big& b) {
    for (size_t i = a.size(); i-- > 0;) if (a[i] != b[i]) return a[i] > b[i];
    return true;
}
void Sub(Big& a, const Big& b) {
    std::int64_t borrow = 0;
    for (size_t i = 0; i < a.size(); ++i) {
        std::int64_t v = std::int64_t(a[i]) - b[i] - borrow;
        borrow = v < 0;
        a[i] = std::uint32_t(v & 0xffffffff);
    }
}
void AddMod(Big& r, const Big& x, const Big& n) { // r = (r + x) mod n, r,x < n
    std::uint64_t carry = 0;
    for (size_t i = 0; i < r.size(); ++i) {
        std::uint64_t v = std::uint64_t(r[i]) + x[i] + carry;
        r[i] = std::uint32_t(v);
        carry = v >> 32;
    }
    if (carry || Geq(r, n)) Sub(r, n);
}
Big MulMod(const Big& a, const Big& b, const Big& n) { // double-and-add; fine for a few dozen multiplications
    Big r(n.size(), 0);
    for (size_t bit = n.size() * 32; bit-- > 0;) {
        AddMod(r, r, n);
        if ((b[bit / 32] >> (bit % 32)) & 1) AddMod(r, a, n);
    }
    return r;
}
// m^e mod n, e big-endian bytes
Big ModExp(const Big& m, const unsigned char* e, size_t elen, const Big& n) {
    Big result(n.size(), 0);
    result[0] = 1;
    for (size_t i = 0; i < elen; ++i)
        for (int bit = 7; bit >= 0; --bit) {
            result = MulMod(result, result, n);
            if ((e[i] >> bit) & 1) result = MulMod(result, m, n);
        }
    return result;
}

// ---- hashing through bcrypt -----------------------------------------------------------------------------------------------
struct Hasher {
    BCRYPT_ALG_HANDLE alg = nullptr;
    ULONG size = 0;
    ~Hasher() { if (alg) BCryptCloseAlgorithmProvider(alg, 0); }
    bool Open(LPCWSTR id) {
        DWORD copied = 0;
        return BCryptOpenAlgorithmProvider(&alg, id, nullptr, 0) >= 0 &&
               BCryptGetProperty(alg, BCRYPT_HASH_LENGTH, reinterpret_cast<PUCHAR>(&size), sizeof(size), &copied, 0) >= 0 && size > 0 && size <= 64;
    }
    bool Hash(const unsigned char* data, size_t n, unsigned char* out) {
        return BCryptHash(alg, nullptr, 0, const_cast<PUCHAR>(data), static_cast<ULONG>(n), out, size) >= 0;
    }
};

bool Mgf1(Hasher& h, const unsigned char* seed, size_t seedLen, unsigned char* out, size_t outLen) {
    std::vector<unsigned char> in(seed, seed + seedLen);
    in.resize(seedLen + 4);
    std::vector<unsigned char> digest(h.size);
    for (std::uint32_t counter = 0, done = 0; done < outLen; ++counter) {
        in[seedLen] = unsigned char(counter >> 24); in[seedLen + 1] = unsigned char(counter >> 16);
        in[seedLen + 2] = unsigned char(counter >> 8); in[seedLen + 3] = unsigned char(counter);
        if (!h.Hash(in.data(), in.size(), digest.data())) return false;
        for (size_t i = 0; i < digest.size() && done < outLen; ++i) out[done++] = digest[i];
    }
    return true;
}

// Returns the BCryptEncrypt status for the OAEP case; STATUS_INVALID_PARAMETER means "could not handle, report original failure".
NTSTATUS OaepEncrypt(BCRYPT_KEY_HANDLE key, const BCRYPT_OAEP_PADDING_INFO& pad, const unsigned char* msg, ULONG msgLen,
                     PUCHAR out, ULONG outCap, ULONG* written) {
    ULONG blobSize = 0;
    if (BCryptExportKey(key, nullptr, BCRYPT_RSAPUBLIC_BLOB, nullptr, 0, &blobSize, 0) < 0 || blobSize < sizeof(BCRYPT_RSAKEY_BLOB)) return STATUS_INVALID_PARAMETER;
    std::vector<unsigned char> blob(blobSize);
    if (BCryptExportKey(key, nullptr, BCRYPT_RSAPUBLIC_BLOB, blob.data(), blobSize, &blobSize, 0) < 0) return STATUS_INVALID_PARAMETER;
    const auto* head = reinterpret_cast<const BCRYPT_RSAKEY_BLOB*>(blob.data());
    if (head->Magic != BCRYPT_RSAPUBLIC_MAGIC || head->cbPublicExp == 0 || head->cbPublicExp > 8 || head->cbModulus < 128 || head->cbModulus > 1024 ||
        sizeof(BCRYPT_RSAKEY_BLOB) + size_t(head->cbPublicExp) + head->cbModulus > blobSize)
        return STATUS_INVALID_PARAMETER;
    const size_t k = head->cbModulus;
    const unsigned char* exponent = blob.data() + sizeof(BCRYPT_RSAKEY_BLOB);
    const unsigned char* modulus = exponent + head->cbPublicExp;

    if (!pad.pszAlgId) return STATUS_INVALID_PARAMETER;
    Hasher h;
    if (!h.Open(pad.pszAlgId)) return STATUS_INVALID_PARAMETER;
    const size_t hLen = h.size;
    if (k < 2 * hLen + 2 || msgLen > k - 2 * hLen - 2 || (pad.cbLabel && !pad.pbLabel)) return STATUS_INVALID_PARAMETER;
    if (outCap < k) { *written = static_cast<ULONG>(k); return STATUS_BUFFER_TOO_SMALL; }

    // EME-OAEP encoding
    std::vector<unsigned char> em(k, 0), mask(k);
    unsigned char* seed = em.data() + 1;
    unsigned char* db = em.data() + 1 + hLen;
    const size_t dbLen = k - hLen - 1;
    if (!h.Hash(pad.pbLabel ? pad.pbLabel : reinterpret_cast<PUCHAR>(&em[0]), pad.pbLabel ? pad.cbLabel : 0, db)) return STATUS_INVALID_PARAMETER;
    db[dbLen - msgLen - 1] = 0x01;
    std::memcpy(db + dbLen - msgLen, msg, msgLen);
    if (BCryptGenRandom(nullptr, seed, static_cast<ULONG>(hLen), BCRYPT_USE_SYSTEM_PREFERRED_RNG) < 0) return STATUS_INVALID_PARAMETER;
    if (!Mgf1(h, seed, hLen, mask.data(), dbLen)) return STATUS_INVALID_PARAMETER;
    for (size_t i = 0; i < dbLen; ++i) db[i] ^= mask[i];
    if (!Mgf1(h, db, dbLen, mask.data(), hLen)) return STATUS_INVALID_PARAMETER;
    for (size_t i = 0; i < hLen; ++i) seed[i] ^= mask[i];

    // c = em^e mod n
    const size_t limbs = (k + 3) / 4;
    const Big n = FromBE(modulus, k, limbs);
    const Big m = FromBE(em.data(), k, limbs);
    if (Geq(m, n)) return STATUS_INVALID_PARAMETER;
    const Big c = ModExp(m, exponent, head->cbPublicExp, n);
    for (size_t i = 0; i < k; ++i) out[i] = static_cast<unsigned char>(c[(k - 1 - i) / 4] >> (8 * ((k - 1 - i) % 4)));
    *written = static_cast<ULONG>(k);
    return STATUS_SUCCESS;
}

NTSTATUS WINAPI HookedBCryptEncrypt(BCRYPT_KEY_HANDLE key, PUCHAR input, ULONG inputLen, VOID* padInfo, PUCHAR iv, ULONG ivLen,
                                    PUCHAR output, ULONG outputCap, ULONG* result, ULONG flags) {
    const NTSTATUS status = g_original(key, input, inputLen, padInfo, iv, ivLen, output, outputCap, result, flags);
    if (status != STATUS_INVALID_PARAMETER || !(flags & BCRYPT_PAD_OAEP) || !padInfo || !output || !result || (!input && inputLen)) return status;
    ULONG written = 0;
    const NTSTATUS fallback = OaepEncrypt(key, *static_cast<BCRYPT_OAEP_PADDING_INFO*>(padInfo), input, inputLen, output, outputCap, &written);
    if (fallback == STATUS_INVALID_PARAMETER) return status; // not an RSA public key / unsupported parameters: report Wine's own error
    if (InterlockedCompareExchange(&g_logged, 1, 0) == 0) Log("flarial-bcrypt-shim: BCryptEncrypt RSA-OAEP rejected by Wine, using software OAEP\n");
    *result = written;
    return fallback;
}

// ---- installation: redirect the export table entry of bcrypt!BCryptEncrypt to a stub ABOVE bcrypt's base --------------------
// An export RVA is an unsigned 32-bit offset from the module base, so the stub must live at a higher address (within 4 GiB).
void* AllocAbove(void* base, size_t size) {
    SYSTEM_INFO si; GetSystemInfo(&si);
    const std::uintptr_t grain = si.dwAllocationGranularity, origin = reinterpret_cast<std::uintptr_t>(base);
    for (std::uintptr_t distance = grain; distance < 0x70000000; distance += grain * 16)
        if (void* p = VirtualAlloc(reinterpret_cast<void*>(origin + distance), size, MEM_RESERVE | MEM_COMMIT, PAGE_EXECUTE_READWRITE)) return p;
    return nullptr;
}

bool Install() {
    HMODULE bcrypt = GetModuleHandleW(L"bcrypt.dll");
    if (!bcrypt) { Log("flarial-bcrypt-shim: bcrypt.dll not loaded, shim inactive\n"); return false; }
    auto* base = reinterpret_cast<unsigned char*>(bcrypt);
    auto* dos = reinterpret_cast<IMAGE_DOS_HEADER*>(base);
    if (dos->e_magic != IMAGE_DOS_SIGNATURE) return false;
    auto* nt = reinterpret_cast<IMAGE_NT_HEADERS*>(base + dos->e_lfanew);
    if (nt->Signature != IMAGE_NT_SIGNATURE || nt->OptionalHeader.Magic != IMAGE_NT_OPTIONAL_HDR64_MAGIC) return false;
    const auto& dir = nt->OptionalHeader.DataDirectory[IMAGE_DIRECTORY_ENTRY_EXPORT];
    if (!dir.VirtualAddress) return false;
    auto* exports = reinterpret_cast<IMAGE_EXPORT_DIRECTORY*>(base + dir.VirtualAddress);
    auto* names = reinterpret_cast<DWORD*>(base + exports->AddressOfNames);
    auto* ordinals = reinterpret_cast<WORD*>(base + exports->AddressOfNameOrdinals);
    auto* functions = reinterpret_cast<DWORD*>(base + exports->AddressOfFunctions);
    DWORD* entry = nullptr;
    for (DWORD i = 0; i < exports->NumberOfNames; ++i)
        if (std::strcmp(reinterpret_cast<const char*>(base + names[i]), "BCryptEncrypt") == 0) { entry = &functions[ordinals[i]]; break; }
    if (!entry) { Log("flarial-bcrypt-shim: BCryptEncrypt export not found, shim inactive\n"); return false; }
    const DWORD rva = *entry;
    if (rva >= dir.VirtualAddress && rva < dir.VirtualAddress + dir.Size) { Log("flarial-bcrypt-shim: BCryptEncrypt is a forwarder, shim inactive\n"); return false; }
    g_original = reinterpret_cast<EncryptFn>(base + rva);

    // stub: mov rax, imm64 ; jmp rax
    auto* stub = static_cast<unsigned char*>(AllocAbove(base, 32));
    if (!stub) { Log("flarial-bcrypt-shim: no memory above bcrypt.dll, shim inactive\n"); g_original = nullptr; return false; }
    stub[0] = 0x48; stub[1] = 0xB8;
    const auto target = reinterpret_cast<std::uintptr_t>(&HookedBCryptEncrypt);
    std::memcpy(stub + 2, &target, 8);
    stub[10] = 0xFF; stub[11] = 0xE0;
    FlushInstructionCache(GetCurrentProcess(), stub, 12);

    DWORD old = 0;
    if (!VirtualProtect(entry, sizeof(DWORD), PAGE_READWRITE, &old)) { Log("flarial-bcrypt-shim: cannot patch export table, shim inactive\n"); g_original = nullptr; return false; }
    *entry = static_cast<DWORD>(stub - base);
    VirtualProtect(entry, sizeof(DWORD), old, &old);
    Log("flarial-bcrypt-shim: installed (BCryptEncrypt RSA-OAEP fallback)\n");
    return true;
}

} // namespace

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        if (InterlockedCompareExchange(&g_installed, 1, 0) == 0) {
            __try { Install(); } __except (EXCEPTION_EXECUTE_HANDLER) { g_original = nullptr; Log("flarial-bcrypt-shim: exception during install, shim inactive\n"); }
        }
    }
    return TRUE; // never fail the load: the shim is best-effort
}

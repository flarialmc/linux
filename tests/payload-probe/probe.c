/* Test DLL for the launcher payload: does what the Flarial client does in DLL_PROCESS_ATTACH (dll-css takeLauncherPayload):
 * GetThreadDescription(GetCurrentThread()), then SetThreadDescription(L""), and records both reads in "<this dll>.out":
 *   first=<description as UTF-8>
 *   after_clear=<description as UTF-8, must be empty>
 * Build: tests/payload-probe/run.sh (msvc-wine). */
#include <windows.h>

static void put(HANDLE f, const char *key, PWSTR value)
{
    char buf[70000];
    int n = 0, len = (int)lstrlenA(key);
    DWORD written;
    memcpy(buf, key, len);
    n = len;
    if (value && *value)
        n += WideCharToMultiByte(CP_UTF8, 0, value, -1, buf + n, (int)sizeof(buf) - n - 2, NULL, NULL) - 1;
    buf[n++] = '\n';
    WriteFile(f, buf, (DWORD)n, &written, NULL);
}

BOOL WINAPI DllMain(HINSTANCE instance, DWORD reason, LPVOID reserved)
{
    (void)reserved;
    if (reason == DLL_PROCESS_ATTACH) {
        PWSTR first = NULL, second = NULL;
        wchar_t path[MAX_PATH + 8];
        DisableThreadLibraryCalls(instance);
        GetThreadDescription(GetCurrentThread(), &first);
        SetThreadDescription(GetCurrentThread(), L"");
        GetThreadDescription(GetCurrentThread(), &second);

        GetModuleFileNameW(instance, path, MAX_PATH);
        lstrcatW(path, L".out");
        HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
        if (f != INVALID_HANDLE_VALUE) {
            put(f, "first=", first);
            put(f, "after_clear=", second);
            CloseHandle(f);
        }
        if (first) LocalFree(first);
        if (second) LocalFree(second);
    }
    return TRUE;
}

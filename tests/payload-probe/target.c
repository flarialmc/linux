/* Stand-in for Minecraft.Windows.exe (the injector finds the game by this name): marks itself ready, then idles. */
#include <windows.h>

int main(void)
{
    wchar_t path[MAX_PATH + 16];
    DWORD n = GetModuleFileNameW(NULL, path, MAX_PATH), i;
    for (i = n; i > 0 && path[i - 1] != L'\\'; --i) {}
    path[i] = 0;
    lstrcatW(path, L"ready.txt");
    HANDLE f = CreateFileW(path, GENERIC_WRITE, 0, NULL, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
    Sleep(10 * 60 * 1000);
    return 0;
}

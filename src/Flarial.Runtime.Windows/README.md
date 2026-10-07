Windows-only runtime sources, synced with upstream flarialmc/Flarial.Launcher main 80b017b ("Version -> '2026.10.6.734'").
Covers: GDK/Minecraft lookup + bootstrap, FlarialClient.Loader (suspended remote thread: import DLLs and the client queued as APCs, account payload set as the thread description) and Injector, native
ModificationLibrary (LoadLibraryEx + exported-symbol checks), PackageService/TaskService, PasswordVault credentials,
CsWin32 NativeMethods, NativeDialog (TaskDialog), the MSIX self-updater, the SystemIdentification analytics id and
the native beta commit-hash check (Core/FlarialClientBeta.Windows.cs). The Linux twin of the loader is Flarial.Runtime/Core/FlarialClient/FlarialClient.Loader.cs. Package/ holds the MSIX manifest and assets.
Not built on Linux. The cross-platform equivalents (managed PE parsing for the same checks) live in Flarial.Runtime.
A Windows backend implementing the interfaces in Flarial.Runtime/Platform can be ported from here.

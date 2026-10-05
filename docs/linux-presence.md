# Linux presence

The launcher downloads and verifies the normal Windows release or personalized beta DLL and injects that original file through Wine. It does not patch the DLL or require a build-specific profile.

The shared DLL's api-utils client checks the loaded `ntdll.dll` for Wine's `wine_get_version` export. Under Wine it reports `linux`; on native Windows it reports `windows`. The same platform is used in encrypted gateway requests and crash metadata. Heartbeats inherit the connection's platform.

Both client channels pin the api-utils revision containing this detection. Windows and Wine runtime tests in api-utils validate the platform selection. Beta personalization and the DLL's commit export remain unchanged by the launcher.

The API accepts Linux independently for presence and statistics, while bootstrap resolves the Windows release channel. There is no separate Linux client build. Confirm a player appears in the Linux list during a real game session before claiming in-game verification.

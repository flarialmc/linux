# Linux presence patch

The launcher downloads the normal Windows release or personalized beta DLL. Before injection it creates a cached copy under `cache/linux-presence/`, keyed by the original SHA-256. The original stays unchanged for verification and updates.

`Injection/LinuxPresencePatch.cs` recognizes the reviewed `.text` SHA-256 for each supported build. It checks both RIP-relative API platform references and their original `windows` target, writes `linux` into verified zero-filled `.rdata` alignment padding, expands that section's virtual size by six bytes, and redirects only the two API references. Other consumers of the original Windows string, all exports and beta personalization remain unchanged. Unknown or altered builds stop injection with an update message.

Supported builds at introduction:
- Release: `1c86513b2804225eab13f99fc9d5da75ba912938`.
- Beta: `8524ee830b4ca8807972a50842ef252ab1c6a3a8`.

For a new shared client build, inspect the gateway platform and crash-metadata references, confirm their string constructor reads the NUL-terminated length at runtime, and add its `.text` hash and RVAs. Check the exact original instruction and target before adding a profile. Never replace every Windows string or change beta seed bytes.

Run `dotnet run --project tests/LinuxPresence -- /path/to/release.dll /path/to/beta.dll` before releasing a launcher with new profiles. It verifies original preservation, mapped Linux data, bounded changes, cache repair and refusal of an unrecognized code section. Also test a personalized beta and a real game session against the API before claiming in-game verification.

The API accepts Linux independently for presence and statistics, while bootstrap resolves the Windows release channel. There is no separate Linux client build.

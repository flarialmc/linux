using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Update;

public sealed record UpdateManifest(string Version, string Url, string Sha256, long Size, string Signature);

/// <summary>
/// Launcher self-update per linux-release-contract: signed tar.zst -> versions/&lt;v&gt;, atomic `current`/`previous` symlink swap,
/// .started/.healthy rollback. Only active when running from the managed layout (see <see cref="ForRunningInstall"/>).
/// Never exits the process; the caller decides when to restart.
/// </summary>
public sealed class LauncherUpdater
{
    public const string ManifestUrl = "https://cdn.flarial.xyz/launcher/linux/Flarial.Launcher.Linux.json";

    const string PublicKey = """
        -----BEGIN PUBLIC KEY-----
        MFkwEwYHKoZIzj0CAQYIKoZIzj0DAQcDQgAEyE2XQRgyEM/Gy+/MITOSFhmEFoDY
        /8mmGUIAdy66nSToJU7whtSWcAjZDojWUhB29xSvmAM+bX1h5eknNadUUQ==
        -----END PUBLIC KEY-----
        """;

    [DllImport("libc", SetLastError = true)] static extern int rename(string from, string to);

    readonly string _root, _manifestUrl, _publicKey;
    readonly Action<string> _log;

    public LauncherUpdater(string? root = null, string? manifestUrl = null, string? publicKeyPem = null, Action<string>? log = null)
    {
        _root = root ?? DefaultRoot;
        _manifestUrl = manifestUrl ?? ManifestUrl;
        _publicKey = publicKeyPem ?? PublicKey;
        _log = log ?? DefaultLog;
    }

    public static string DefaultRoot => Path.Combine(Paths.Root, "launcher");

    static void DefaultLog(string m)
    {
        try { Directory.CreateDirectory(Paths.Logs); File.AppendAllText(Path.Combine(Paths.Logs, "updater.log"), $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {m}\n"); } catch { }
    }

    void Log(string m) { try { _log(m); } catch { } }

    string Versions => Path.Combine(_root, "versions");
    string VersionDir(string v) => Path.Combine(Versions, v);

    /// <summary>Non-null only when the running executable lives in versions/&lt;v&gt;/ of the default layout.</summary>
    public static LauncherUpdater? ForRunningInstall() => RunningVersion(DefaultRoot) is null ? null : new LauncherUpdater();

    /// <summary>Name of the versions/ directory the current process runs from, or null when not managed.</summary>
    public static string? RunningVersion(string root)
    {
        var dir = Path.GetDirectoryName(Environment.ProcessPath);
        if (dir is null || Path.GetDirectoryName(dir) != Path.Combine(root, "versions")) return null;
        return Path.GetFileName(dir);
    }

    string? LinkName(string link) => Directory.Exists(Path.Combine(_root, link)) && new DirectoryInfo(Path.Combine(_root, link)).LinkTarget is { } t ? Path.GetFileName(t.TrimEnd('/')) : null;
    public string? CurrentVersion => LinkName("current");

    void Link(string name, string version)
    {
        var tmp = Path.Combine(_root, name + ".tmp");
        File.Delete(tmp);
        File.CreateSymbolicLink(tmp, "versions/" + version);
        if (rename(tmp, Path.Combine(_root, name)) != 0) throw new IOException($"rename failed ({Marshal.GetLastPInvokeError()})");
    }

    static Version? Parse(string? s) => Version.TryParse(s?.Trim(), out var v) ? v : null;

    // ---- check / install ----

    public async Task<UpdateManifest?> CheckAsync(string currentVersion, CancellationToken ct = default)
    {
        var json = JsonNode.Parse(await Download.Http.GetStringAsync(_manifestUrl, ct))!;
        var m = new UpdateManifest(json["version"]!.GetValue<string>(), json["url"]!.GetValue<string>(), json["sha256"]!.GetValue<string>().ToLowerInvariant(), json["size"]!.GetValue<long>(), json["signature"]!.GetValue<string>());
        var remote = Parse(m.Version); var local = Parse(currentVersion);
        if (remote is null) throw new InvalidDataException("bad manifest version");
        if (local is not null && remote <= local) return null;
        if (File.Exists(Path.Combine(_root, "bad-version")) && File.ReadAllText(Path.Combine(_root, "bad-version")).Trim() == m.Version) { Log($"skip {m.Version}: rolled back earlier"); return null; }
        if (Directory.Exists(VersionDir(m.Version)) && CurrentVersion == m.Version) return null;
        return m;
    }

    /// <summary>Downloads, verifies (size, sha256, ECDSA), extracts and activates. Throws on any failure leaving current untouched.</summary>
    public async Task InstallAsync(UpdateManifest m, Action<long, long>? progress = null, CancellationToken ct = default)
    {
        var staging = Path.Combine(_root, "staging");
        if (Directory.Exists(staging)) Directory.Delete(staging, true);
        Directory.CreateDirectory(staging);
        try
        {
            Log($"downloading {m.Version} from {m.Url}");
            var archive = Path.Combine(staging, "archive.tar.zst");
            await Download.FileAsync(m.Url, archive, m.Sha256, progress, ct);
            if (new FileInfo(archive).Length != m.Size) throw new InvalidDataException("size mismatch");

            using (var f = File.OpenRead(archive))
            using (var ec = ECDsa.Create())
            {
                ec.ImportFromPem(_publicKey);
                if (!ec.VerifyData(f, Convert.FromBase64String(m.Signature), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence))
                    throw new InvalidDataException("signature verification failed");
            }

            var x = Path.Combine(staging, "x"); Directory.CreateDirectory(x);
            if (await Proc.RunAsync(Proc.Info("tar", ["--zstd", "-xf", archive, "-C", x]), null, TimeSpan.FromMinutes(5), ct) != 0) throw new IOException("tar extract failed");
            var src = Path.Combine(x, "Flarial.Launcher");
            var exe = Path.Combine(src, "Flarial.Launcher");
            if (!File.Exists(exe)) throw new InvalidDataException("archive has no Flarial.Launcher executable");
            // the manifest json is unsigned: bind it to the signed archive so an old archive cannot pose as a newer version
            if (Parse(File.ReadAllText(Path.Combine(src, "version.txt"))) != Parse(m.Version)) throw new InvalidDataException("archive version != manifest version");
            File.SetUnixFileMode(exe, File.GetUnixFileMode(exe) | UnixFileMode.UserExecute);

            Directory.CreateDirectory(Versions);
            var dest = VersionDir(m.Version);
            if (Directory.Exists(dest)) Directory.Delete(dest, true);
            Directory.Move(src, dest);
            Activate(m.Version);
            Log($"installed {m.Version}");
        }
        catch (Exception e) { Log($"install {m.Version} failed: {e.Message}"); throw; }
        finally { try { Directory.Delete(staging, true); } catch { } }
    }

    /// <summary>Swaps current to the given (already present) version, previous to the old current, prunes the rest.</summary>
    public void Activate(string version)
    {
        var cur = CurrentVersion;
        if (cur != version)
        {
            if (cur is not null) Link("previous", cur);
            Link("current", version);
        }
        var keep = new[] { version, cur, LinkName("previous"), RunningVersion(_root) };
        foreach (var d in Directory.GetDirectories(Versions))
            if (!keep.Contains(Path.GetFileName(d))) try { Directory.Delete(d, true); Log($"pruned {Path.GetFileName(d)}"); } catch { }
    }

    // ---- health / rollback ----

    public void MarkStarted()
    {
        if (RunningVersion(_root) is not { } v) return;
        var d = VersionDir(v);
        if (!File.Exists(Path.Combine(d, ".healthy")) && !File.Exists(Path.Combine(d, ".started"))) File.WriteAllText(Path.Combine(d, ".started"), "");
    }

    public void MarkHealthy()
    {
        if (RunningVersion(_root) is { } v) try { File.WriteAllText(Path.Combine(VersionDir(v), ".healthy"), ""); } catch { }
    }

    /// <summary>If current started >60s ago without ever becoming healthy, swap back to previous. True when swapped.</summary>
    public bool RollbackIfUnhealthy()
    {
        var cur = CurrentVersion; var prev = LinkName("previous");
        if (cur is null || prev is null || !Directory.Exists(VersionDir(prev))) return false;
        var started = Path.Combine(VersionDir(cur), ".started");
        if (!File.Exists(started) || File.Exists(Path.Combine(VersionDir(cur), ".healthy"))) return false;
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(started) < TimeSpan.FromSeconds(60)) return false;
        Link("current", prev);
        File.Delete(Path.Combine(_root, "previous"));
        File.WriteAllText(Path.Combine(_root, "bad-version"), cur);
        Log($"rolled back {cur} -> {prev} (never became healthy)");
        return true;
    }

    // ---- restart / self-install ----

    /// <summary>Starts the stable entrypoint after a short delay (lets this process release the single-instance mutex). Does not exit.</summary>
    public static void SpawnLauncher()
    {
        var bin = Path.Combine(Environment.GetEnvironmentVariable("XDG_BIN_HOME") is { Length: > 0 } b ? b : Path.Combine(Paths.Home, ".local", "bin"), "flarial-launcher");
        var target = File.Exists(bin) ? bin : Path.Combine(DefaultRoot, "current", "Flarial.Launcher");
        var psi = new ProcessStartInfo("sh") { UseShellExecute = false };
        psi.ArgumentList.Add("-c"); psi.ArgumentList.Add("sleep 1; exec \"$0\""); psi.ArgumentList.Add(target);
        Process.Start(psi);
    }

    /// <summary>`--install`: copies the running (extracted/dev) build into the managed layout and creates the bin link, desktop entry and icon.</summary>
    public static int SelfInstall()
    {
        var root = DefaultRoot;
        if (RunningVersion(root) is not null) { Console.WriteLine("already installed"); return 0; }
        var src = AppContext.BaseDirectory.TrimEnd('/');
        var version = Parse(Core.FlarialLauncher.Version)?.ToString() ?? "0.0.0.0";
        var u = new LauncherUpdater(root);
        var dest = u.VersionDir(version); var tmp = dest + ".tmp";
        Directory.CreateDirectory(u.Versions);
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        CopyDir(src, tmp);
        if (Directory.Exists(dest)) Directory.Delete(dest, true);
        Directory.Move(tmp, dest);
        u.Activate(version);

        var binDir = Environment.GetEnvironmentVariable("XDG_BIN_HOME") is { Length: > 0 } b ? b : Path.Combine(Paths.Home, ".local", "bin");
        Directory.CreateDirectory(binDir);
        var bin = Path.Combine(binDir, "flarial-launcher");
        File.Delete(bin);
        File.CreateSymbolicLink(bin, Path.Combine(root, "current", "Flarial.Launcher"));

        var desktop = Path.Combine(dest, "flarial-launcher.desktop"); var icon = Path.Combine(dest, "flarial-launcher.png");
        if (File.Exists(desktop)) { var d = Path.Combine(Paths.DataHome, "applications"); Directory.CreateDirectory(d); File.WriteAllText(Path.Combine(d, "flarial-launcher.desktop"), File.ReadAllText(desktop).Replace("@EXEC@", bin)); }
        if (File.Exists(icon)) { var d = Path.Combine(Paths.DataHome, "icons", "hicolor", "256x256", "apps"); Directory.CreateDirectory(d); File.Copy(icon, Path.Combine(d, "flarial-launcher.png"), true); }
        Console.WriteLine($"installed {version} -> {bin}");
        return 0;
    }

    static void CopyDir(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
        foreach (var d in Directory.GetDirectories(from)) CopyDir(d, Path.Combine(to, Path.GetFileName(d)));
    }
}

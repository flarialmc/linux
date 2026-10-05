using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Runtime.Linux.Engine;
using Flarial.Runtime.Linux.Prefix;
using Flarial.Runtime.Linux.Xbox;
using Flarial.Runtime.Linux.Xodus;

namespace Flarial.Runtime.Linux.Launch;

sealed class LauncherCore(IEngine engine, IXodus xodus, IPrefix prefix, IXboxAuth xbox) : ILauncherCore
{
    const string Exe = "Minecraft.Windows.exe";
    static readonly TimeSpan GameWait = TimeSpan.FromSeconds(120);

    /// <summary>A game process whose window is long gone is a stale instance, not a running game.</summary>
    public bool IsRunning => FindGamePid() is { } pid && !GameWatch.Hung(pid);

    /// <summary>The wine process hosting the game: argv[0] is the NT path of the exe (umu/pressure-vessel wrappers only carry it as an argument).</summary>
    public uint? FindGamePid()
    {
        foreach (var pid in ProcScan.PrefixPids())
            if (ProcScan.CmdLine(pid).FirstOrDefault() is { } a && a.Replace('\\', '/').EndsWith("/" + Exe, StringComparison.OrdinalIgnoreCase)) return (uint)pid;
        return null;
    }

    static string Uid() => File.ReadAllLines("/proc/self/status").First(l => l.StartsWith("Uid:")).Split('\t')[1];

    public async Task<uint?> LaunchAsync(string gameDir, LaunchSettings settings, CancellationToken ct)
    {
        if (IsRunning) throw new InvalidOperationException("Minecraft is already running.");
        if (!File.Exists(Path.Combine(gameDir, Exe))) throw new FileNotFoundException("Game executable not found.", Path.Combine(gameDir, Exe));

        // IsRunning is false: whatever still lives in our prefix (a previous session's wineserver, a hung injector.exe, umu/xodus) is stale and would
        // make the injector and the registry write talk to the wrong wineserver. A launch in progress owns its own chain: never touch that.
        if (Interlocked.Exchange(ref s_launching, 1) != 0) throw new InvalidOperationException("A launch is already in progress.");
        try { return await LaunchCoreAsync(gameDir, settings, ct); }
        finally { Volatile.Write(ref s_launching, 0); }
    }

    static int s_launching;

    async Task<uint?> LaunchCoreAsync(string gameDir, LaunchSettings settings, CancellationToken ct)
    {
        if (LaunchLog.TakeFresh()) LaunchLog.Phase("LaunchAsync entered " + gameDir); else LaunchLog.Begin("launch " + gameDir);
        if (ProcScan.PrefixPids() is { Count: > 0 } stale)
        {
            LaunchLog.Phase($"stale cleanup: {stale.Count} leftover process(es) in the prefix, no running game ({string.Join(", ", stale.Take(8).Select(p => p + ":" + Path.GetFileName(ProcScan.CmdLine(p).FirstOrDefault() ?? "?").Replace('\\', '/')))})");
            await KillAsync(ct);
            LaunchLog.Phase(ProcScan.PrefixPids().Count is var left and > 0 ? $"stale cleanup incomplete: {left} process(es) remain" : "stale cleanup done");
        }
        ReadyGate.Start();
        var mlog = Path.Combine(Paths.Logs, "minecraft.log");
        LaunchLog.GameLogOffset = File.Exists(mlog) ? new FileInfo(mlog).Length : 0;
        LaunchLog.Phase("settings loaded (diagnostics=" + settings.Diagnostics + ")");

        // the Xbox refresh is network only and independent of the prefix work: run both at once
        var xboxTask = Task.Run(async () =>
        {
            try { return await xbox.RefreshAsync(ct); }
            catch (OperationCanceledException) { throw; }
            catch { return new XboxSession(false, null, null, null); }
        }, ct);
        var umuEntry = Paths.UmuRun;
        var prep = Task.Run(async () =>
        {
            await LaunchLog.Time("prefix ensure setup", prefix.EnsureSetupAsync(null, ct));
            await LaunchLog.Time("prefix prepare game (cacert/gdk deps/gameinput)", prefix.PrepareGameAsync(gameDir, null, ct));
            LinkContent(gameDir);
            LaunchLog.Phase("content link");
            umuEntry = ExtractUmu();
            LaunchLog.Phase("umu entry ready (" + (umuEntry == Paths.UmuRun ? "zipapp" : "extracted") + ")");
        }, ct);
        WriteWrapper();
        Directory.CreateDirectory(Paths.Logs);
        // xodus decrypt + licence check (~1.6 s) needs neither the Xbox session nor the prefix prep: start it right away on a ready prefix.
        // The wrapper waits for the "go" file (session-dependent env + umu entry) before it execs umu, so the network/prep work hides under the decrypt.
        var goFile = Path.Combine(Paths.Run, "launch-go");
        try { File.Delete(goFile); } catch { }
        System.Diagnostics.Process? chain = null;
        System.Diagnostics.Process Start(XboxSession session)
        {
            var env = BuildEnv(session, settings);
            env["FLARIAL_GO_FILE"] = goFile;
            env["FLARIAL_LAUNCH_LOG"] = Path.Combine(Paths.Logs, "launch.log");
            return xodus.StartRun(gameDir, Paths.Wrapper, env, Paths.Content, Path.Combine(Paths.Logs, "minecraft.log"));
        }
        var early = prefix.IsReady;
        if (early)
        {
            LinkContent(gameDir); // xodus runs with Paths.Content as its working directory
            chain = Start(new XboxSession(false, null, null, null));
            LaunchLog.Phase("xodus-cli run started early (decrypt + licence check, then wrapper waits for go)");
        }
        try
        {
            var session = await LaunchLog.Time("xbox refresh (parallel)", xboxTask);
            await prep;
            await LaunchLog.Time($"set refresh token (online={session.Online})", prefix.SetRefreshTokenAsync(session.RefreshToken, ct));
            var go = $"export FLARIAL_UMU_RUN='{umuEntry}'\n" + (session.Online && session.DeviceJsonPath is { } dj ? $"export WINEGDK_PREAUTH_DEVICE='Z:{dj.Replace('/', '\\')}'\n" : "unset WINEGDK_PREAUTH_DEVICE\n");
            File.WriteAllText(goFile + ".tmp", go); File.Move(goFile + ".tmp", goFile, true);
            LaunchLog.Phase("go file written (session + umu entry)");
            if (chain is null) { chain = Start(session); LaunchLog.Phase("xodus-cli run started (decrypt + licence check, then wrapper, then umu)"); }
        }
        catch
        {
            try { File.WriteAllText(goFile, "exit 1\n"); } catch { } // release the waiting wrapper
            throw;
        }
        using var _chain = chain!;

        var deadline = DateTime.UtcNow + GameWait;
        DateTime? exited = null;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (FindGamePid() is { } pid) { LaunchLog.Phase($"game process found (pid {pid})"); return pid; }
            // umu may hand off and exit while wine keeps starting; give it a moment before giving up.
            if (chain!.HasExited) { exited ??= DateTime.UtcNow; if (DateTime.UtcNow - exited > TimeSpan.FromSeconds(3)) { LaunchLog.Phase("chain exited, no game process"); return null; } }
            await Task.Delay(150, ct);
        }
        LaunchLog.Phase("timed out waiting for the game process");
        return null;
    }

    /// <summary>
    /// umu-run is a zipapp: python recompiles all of its modules on every start (no .pyc cache inside a zip), ~170 ms. Extracted into our own
    /// cache dir (never next to umu-run: with the dev seed that is a symlink into another install) once per umu-run size+mtime (+sha256
    /// recorded in the stamp), python caches the bytecode and runs the directory. Falls back to the zipapp on any problem.
    /// </summary>
    internal static string ExtractUmu()
    {
        try
        {
            var zip = new FileInfo(Paths.UmuRun);
            if (!zip.Exists) return Paths.UmuRun;
            var dir = Path.Combine(Paths.Cache, "umu-run.d"); var stamp = Path.Combine(dir, ".stamp"); var want = $"{zip.Length}-{zip.LastWriteTimeUtc.Ticks}";
            if (File.Exists(stamp) && File.ReadAllText(stamp).StartsWith(want + "-", StringComparison.Ordinal) && File.Exists(Path.Combine(dir, "__main__.py"))) return dir;
            var tmp = dir + ".tmp"; if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
            System.IO.Compression.ZipFile.ExtractToDirectory(Paths.UmuRun, tmp);
            File.WriteAllText(Path.Combine(tmp, ".stamp"), want + "-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Paths.UmuRun)))[..16]);
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
            Directory.Move(tmp, dir);
            return dir;
        }
        catch { return Paths.UmuRun; }
    }

    /// <summary>Paths.Content -> gameDir (the launch cwd and the NT path the wrapper passes to umu).</summary>
    static void LinkContent(string gameDir)
    {
        var info = new FileInfo(Paths.Content);
        if (info.LinkTarget == gameDir) return;
        if (info.LinkTarget is { }) info.Delete();
        else if (Directory.Exists(Paths.Content) || info.Exists) throw new IOException($"{Paths.Content} is not a symlink.");
        Directory.CreateDirectory(Path.GetDirectoryName(Paths.Content)!);
        File.CreateSymbolicLink(Paths.Content, gameDir);
    }

    static void WriteWrapper()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("launch-wrapper.sh")!;
        using var m = new MemoryStream(); s.CopyTo(m);
        var bytes = m.ToArray();
        if (File.Exists(Paths.Wrapper) && File.ReadAllBytes(Paths.Wrapper).AsSpan().SequenceEqual(bytes)) return;
        Directory.CreateDirectory(Paths.Run);
        var tmp = Paths.Wrapper + ".tmp";
        File.WriteAllBytes(tmp, bytes);
        File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.Move(tmp, Paths.Wrapper, true);
    }

    Dictionary<string, string?> BuildEnv(XboxSession session, LaunchSettings s)
    {
        var steam = Path.Combine(Paths.Home, ".steam", "steam");
        var xauth = new[] { Environment.GetEnvironmentVariable("XAUTHORITY"), Path.Combine(Paths.Home, ".Xauthority"), $"/run/user/{Uid()}/.mutter-Xwaylandauth.0" }
            .FirstOrDefault(p => !string.IsNullOrEmpty(p) && File.Exists(p));
        var vkd3d = (Environment.GetEnvironmentVariable("VKD3D_CONFIG") is { Length: > 0 } c ? c + "," : "") + "force_raw_va_cbv" + (s.RayTracing ? "" : ",nodxr");
        var overrides = "cryptbase=n,b;vrclient=;vrclient_x64=;openvr_api=;wineopenxr=;amd_ags_x64=" + (Settings.BlockWineGameInput ? ";gameinput=d" : "") +
            (Environment.GetEnvironmentVariable("WINEDLLOVERRIDES") is { Length: > 0 } o ? ";" + o : "");

        Dictionary<string, string?> env = new()
        {
            ["PROTONPATH"] = engine.ProtonDir,
            ["PROTON_VERB"] = "run",
            ["WINEPREFIX"] = Paths.Prefix,
            ["STEAM_COMPAT_CLIENT_INSTALL_PATH"] = Directory.Exists(steam) ? steam : Paths.SteamCompat,
            ["UMU_FOLDERS_PATH"] = Paths.Root,
            ["UMU_RUNTIME_UPDATE"] = "0",
            ["PROTON_USE_WOW64"] = "1",
            ["GAMEID"] = "umu-default",
            ["VKD3D_CONFIG"] = vkd3d,
            ["VKD3D_DEBUG"] = "info",
            ["VKD3D_SHADER_CACHE_PATH"] = Paths.GraphicsCache,
            ["DXVK_SHADER_CACHE_PATH"] = Paths.GraphicsCache,
            ["WINEDLLOVERRIDES"] = overrides,
            ["MICROSOFT_WINDOWSAPPRUNTIME_BOOTSTRAP_INITIALIZE_SHOWUI"] = "0",
            ["MICROSOFT_WINDOWSAPPRUNTIME_BOOTSTRAP_INITIALIZE_FAILFAST"] = "0",
            ["MICROSOFT_WINDOWSAPPRUNTIME_DEPLOYMENT_INITIALIZE_ONERRORSHOWUI"] = "0",
            ["GNUTLS_SYSTEM_PRIORITY_FILE"] = null,
            ["GNUTLS_SYSTEM_PRIORITY_FAIL_ON_INVALID"] = null,
            ["PROTON_ENABLE_WAYLAND"] = "0",
            ["PROTON_PREFER_SDL"] = "1",
            ["FLARIAL_UMU_RUN"] = Paths.UmuRun,
            ["FLARIAL_EXE_NAME"] = Exe,
            ["WINEDEBUG"] = "-all,+loaddll", // module bases: resolves the address in an "Unhandled page fault" line to a DLL
        };
        if (xauth is { }) env["XAUTHORITY"] = xauth;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"))) env["WINE_DISABLE_VULKAN_OPWR"] = "1";
        env["WINEGDK_PREAUTH_DEVICE"] = session.Online && session.DeviceJsonPath is { } dj ? "Z:" + dj.Replace('/', '\\') : null;
        if (s.Diagnostics)
        {
            env["PROTON_LOG"] = "1";
            env["PROTON_LOG_DIR"] = Paths.Logs;
            env["WINEDEBUG"] = "+gdkc,trace-gdkc,+xgameruntime,trace-xgameruntime,fixme-all";
        }
        foreach (var kv in s.CustomEnv.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            if (kv.IndexOf('=') is > 0 and var i) env[kv[..i]] = kv[(i + 1)..];
        return env;
    }

    public async Task KillAsync(CancellationToken ct)
    {
        var env = new Dictionary<string, string?> { ["WINEPREFIX"] = Paths.Prefix };
        try { await Proc.RunAsync(Proc.Info(engine.Wineserver, ["-k"], env), Path.Combine(Paths.Logs, "minecraft.log"), TimeSpan.FromSeconds(15)); } catch { }
        foreach (var (sig, wait) in new[] { (0, 5), (15, 3), (9, 3) })
        {
            if (sig != 0) foreach (var p in ProcScan.PrefixPids()) ProcScan.Signal(p, sig);
            for (var i = 0; i < wait * 4; i++)
            {
                if (ProcScan.PrefixPids().Count == 0) return;
                await Task.Delay(250, ct);
            }
        }
    }
}

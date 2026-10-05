using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Flarial.Runtime.Linux.Injection;
using Flarial.Runtime.Linux.Launch;
using Flarial.Runtime.Platform;

namespace Flarial.Runtime.Linux;

/// <summary>Injects a DLL into the Wine process hosting Minecraft with `wine injector.exe` against the live prefix.</summary>
public sealed class LinuxInjector : IInjector
{
    static readonly IInjectorCore Core = new InjectorCore(Backend.Engine);
    static string s_last = "Flarial.Client.";

    public bool IsClientRunning => LinuxGameService.Core.FindGamePid() is { } pid && !GameWatch.Hung(pid) && (Core.IsModuleLoaded(pid, "Flarial.Client.") || Core.IsModuleLoaded(pid, s_last));

    /// <summary>Imports are resolved inside the prefix's system directory.</summary>
    public string SystemDirectory => @"C:\windows\system32";

    static bool IsApiSet(string path)
    {
        var n = path[(path.LastIndexOfAny(['\\', '/']) + 1)..];
        return n.StartsWith("api-ms-", StringComparison.OrdinalIgnoreCase) || n.StartsWith("ext-ms-", StringComparison.OrdinalIgnoreCase);
    }

    const int MaxRetries = 2;

    /// <summary>The wine crash line of the attempt that just died (from minecraft.log), for launch.log.</summary>
    static string CrashLine()
    {
        try
        {
            using var f = new FileStream(Path.Combine(Paths.Logs, "minecraft.log"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            f.Seek(Math.Clamp(LaunchLog.GameLogOffset, 0, f.Length), SeekOrigin.Begin);
            return new StreamReader(f).ReadToEnd().Split('\n').LastOrDefault(l => l.Contains("Unhandled", StringComparison.Ordinal) || l.Contains("wine: ", StringComparison.Ordinal))?.Trim() ?? "no crash line in minecraft.log";
        }
        catch { return "minecraft.log unreadable"; }
    }

    public bool Inject(IReadOnlyList<string> libraries, uint processId)
    {
        var dllPath = libraries[^1];
        // the wait cap counts from a launch we did ourselves; an already running game needs none
        var wait = TimeSpan.FromSeconds(Settings.InjectDelaySeconds) - (DateTime.UtcNow - LinuxGameService.LastLaunchUtc);
        if (wait < TimeSpan.Zero) wait = TimeSpan.Zero;
        // api-ms-win-* / ext-ms-win-* are virtual api-set names the loader resolves itself: there is no such file in system32, so loading
        // them by path always fails (upstream Windows has stubs on disk). Skip them; the client's real dependencies are still pre-loaded.
        var last = libraries.Count - 1;
        libraries = libraries.Where((l, i) => i == last || !IsApiSet(l)).ToList();
        var r = Core.InjectAsync(libraries, processId, wait, CancellationToken.None).GetAwaiter().GetResult();
        // intermittent engine crash during startup (before the client exists): relaunch, at most twice, never in a loop
        for (var retry = 1; !r.Ok && r.Message == "Minecraft exited before injection." && retry <= MaxRetries && DateTime.UtcNow - LinuxGameService.LastLaunchUtc < TimeSpan.FromMinutes(2); retry++)
        {
            LaunchLog.Note($"startup crash {retry}: {CrashLine()}");
            LinuxPlatform.Notify?.Invoke("Minecraft crashed during startup, retrying...");
            LinuxGameService.Core.KillAsync(CancellationToken.None).GetAwaiter().GetResult(); // wineserver -k, then the umu/xodus chain, until the prefix is empty
            if (LinuxGameService.Current?.Launch() is not { } pid) { LaunchLog.Note($"retry {retry}: relaunch failed"); break; }
            LaunchLog.Note($"retry {retry}: relaunched (pid {pid})");
            processId = pid;
            r = Core.InjectAsync(libraries, pid, TimeSpan.FromSeconds(Settings.InjectDelaySeconds), CancellationToken.None).GetAwaiter().GetResult();
        }
        if (r.Ok) s_last = Path.GetFileName(dllPath);
        else
        {
            LinuxPlatform.Notify?.Invoke("Injection failed: " + r.Message);
            try { File.AppendAllText(Path.Combine(Paths.Logs, "launcher.log"), $"{DateTime.Now:s} inject failed: {r.Message}\n"); } catch { }
        }
        return r.Ok;
    }
}

using System;
using System.IO;
using System.Threading;

namespace Flarial.Runtime.Linux.Launch;

/// <summary>
/// Windows launcher parity (Minecraft.Bootstrap.cs): the game is "ready" when its menu_load_lock file under
/// %APPDATA%\Minecraft Bedrock\Users is deleted. Started before the game so a create/delete is never missed.
/// </summary>
static class ReadyGate
{
    static FileSystemWatcher? s_watcher;
    static volatile bool s_seen, s_deleted;

    /// <summary>The lock file was created in this launch (the game uses the menu_load_lock protocol).</summary>
    public static bool Seen => s_seen;
    /// <summary>The lock file was deleted: the main menu has loaded.</summary>
    public static bool Deleted => s_deleted;

    public static void Start()
    {
        Stop();
        s_seen = s_deleted = false;
        try
        {
            var dir = Directory.CreateDirectory(Path.Combine(Paths.Prefix, "drive_c", "users", "steamuser", "AppData", "Roaming", "Minecraft Bedrock", "Users")).FullName;
            var w = new FileSystemWatcher(dir, "*menu_load_lock") { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName };
            w.Created += (_, _) => s_seen = true;
            w.Deleted += (_, _) => { s_seen = true; s_deleted = true; };
            w.EnableRaisingEvents = true;
            s_watcher = w;
        }
        catch { } // no watcher: the swapchain fallback in InjectorCore still applies
    }

    public static void Stop() { var w = Interlocked.Exchange(ref s_watcher, null); try { w?.Dispose(); } catch { } }
}

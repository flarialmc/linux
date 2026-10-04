using System;
using System.Diagnostics;
using System.Linq;

namespace Flarial.Runtime.Linux.Launch;

/// <summary>
/// Detects a game process that outlived its window (Wine sometimes never exits after the window is closed). Read-only: asks the X server
/// (xprop, XWayland on Wayland) which windows carry _NET_WM_PID == the game pid. No xprop / no X server = unknown = never "hung".
/// </summary>
static class GameWatch
{
    static readonly TimeSpan Gone = TimeSpan.FromSeconds(5), NeverShown = TimeSpan.FromSeconds(60);
    static readonly object s_lock = new();
    static uint s_pid; static bool s_seen; static DateTime? s_noWindowSince, s_checked;

    static string? Xprop(params string[] args)
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("xprop", args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false })!;
            var o = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(2000)) { try { p.Kill(); } catch { } return null; }
            return p.ExitCode == 0 ? o : null;
        }
        catch { return null; }
    }

    static bool? HasWindow(uint pid)
    {
        if (Xprop("-root", "_NET_CLIENT_LIST") is not { } list) return null;
        foreach (var w in list[(list.IndexOf('#') + 1)..].Split([',', ' ', '\n'], StringSplitOptions.RemoveEmptyEntries))
            if (Xprop("-id", w, "_NET_WM_PID") is { } s && s.EndsWith("= " + pid + "\n", StringComparison.Ordinal)) return true;
        return false;
    }

    /// <summary>True when the game process has had no window for several seconds after having one (or for a minute since it started).</summary>
    public static bool Hung(uint pid)
    {
        if (ReadyGate.Waiting) return false; // startup in progress (<= inject wait cap): after it, the "never shown for 60 s" rule applies
        lock (s_lock)
        {
            var now = DateTime.UtcNow;
            if (s_pid != pid) { s_pid = pid; s_seen = false; s_noWindowSince = null; s_checked = null; }
            if (s_checked is { } c && now - c < TimeSpan.FromSeconds(1.5)) return Verdict(pid, now);
            s_checked = now;
            switch (HasWindow(pid))
            {
                case true: s_seen = true; s_noWindowSince = null; break;
                case false: s_noWindowSince ??= now; break;
                default: s_noWindowSince = null; break; // cannot query
            }
            return Verdict(pid, now);
        }
    }

    static bool Verdict(uint pid, DateTime now)
    {
        if (s_noWindowSince is not { } since || now - since < Gone) return false;
        if (s_seen) return true;
        try { return DateTime.Now - Process.GetProcessById((int)pid).StartTime >= NeverShown; } catch { return false; }
    }
}

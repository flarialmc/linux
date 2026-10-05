using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux;

public static class SystemDependencies
{
    public sealed record Tool(string Command, string Purpose, string Arch, string Debian, string Fedora, string Suse);
    public sealed record Plan(string Manager, string[] Arguments)
    {
        public string Command => "sudo " + Manager + " " + string.Join(' ', Arguments);
    }

    public static readonly Tool[] Tools =
    [
        new("script", "game downloads and progress", "util-linux", "bsdutils", "util-linux-script", "util-linux"),
        new("setsid", "Microsoft sign-in", "util-linux", "util-linux", "util-linux-core", "util-linux"),
        new("stty", "download terminal sizing", "coreutils", "coreutils", "coreutils", "coreutils"),
        new("bash", "game startup", "bash", "bash", "bash", "bash"),
        new("python3", "Wine/Proton startup", "python", "python3", "python3", "python3"),
        new("tar", "unpacking downloads and updates", "tar", "tar", "tar", "tar"),
        new("gzip", "unpacking the game engine", "gzip", "gzip", "gzip", "gzip"),
        new("zstd", "launcher updates", "zstd", "zstd", "zstd", "zstd"),
        new("openssl", "verifying installer signatures", "openssl", "openssl", "openssl", "openssl"),
        new("xdg-open", "opening sign-in pages and folders", "xdg-utils", "xdg-utils", "xdg-utils", "xdg-utils"),
        new("xprop", "detecting when the game window closes", "xorg-xprop", "x11-utils", "xprop", "xprop")
    ];

    public static string LogPath => Path.Combine(Paths.Logs, "dependencies.log");
    public static List<Tool> Missing(Func<string, bool>? available = null) => Tools.Where(t => !(available ?? ExecutableOnPath)(t.Command)).ToList();

    static bool Executable(string path)
    {
        try { return File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0; }
        catch { return false; }
    }

    internal static bool ExecutableOnPath(string command) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(':')
        .Any(d => d.Length > 0 && Executable(Path.Combine(d, command)));

    public static Plan? InstallationPlan(IEnumerable<Tool> missing, string? osRelease = null)
    {
        if (osRelease is null)
        {
            try { osRelease = File.ReadAllText("/etc/os-release"); } catch { osRelease = ""; }
        }
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in osRelease.Split('\n'))
        {
            var pair = line.Split('=', 2);
            if (pair.Length == 2) values[pair[0].Trim()] = pair[1].Trim().Trim('"', '\'');
        }
        var ids = (values.GetValueOrDefault("ID", "") + " " + values.GetValueOrDefault("ID_LIKE", "")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
        foreach (var id in ids)
        {
            var plan = id switch
            {
                "arch" or "cachyos" or "manjaro" => new Plan("pacman", ["-S", "--needed", "--noconfirm", .. missing.Select(t => t.Arch).Distinct()]),
                "debian" or "ubuntu" or "linuxmint" or "pop" => new Plan("apt-get", ["install", "-y", .. missing.Select(t => t.Debian).Distinct()]),
                "fedora" or "nobara" => new Plan("dnf", ["install", "-y", .. missing.Select(t => t.Fedora).Distinct()]),
                "rhel" or "centos" or "rocky" or "almalinux" => new Plan("dnf", ["install", "-y", .. missing.Select(t => t.Command is "script" or "setsid" ? "util-linux" : t.Fedora).Distinct()]),
                "opensuse" or "opensuse-tumbleweed" or "opensuse-leap" or "suse" => new Plan("zypper", ["--non-interactive", "install", .. missing.Select(t => t.Suse).Distinct()]),
                _ => null
            };
            if (plan is { }) return plan;
        }
        return null;
    }

    public static bool ImmutableSystem => File.Exists("/run/ostree-booted") || File.Exists("/etc/NIXOS");
    public static string ManualCommand(Plan plan) => File.Exists("/run/ostree-booted")
        ? "rpm-ostree install " + string.Join(' ', plan.Arguments.Where(a => !a.StartsWith('-') && a != "install")) : plan.Command;
    public static bool CanInstall(Plan plan) => !ImmutableSystem && Executable("/usr/bin/pkexec") && Executable("/usr/bin/" + plan.Manager);

    // Only a plan built here from the fixed tool/package list is passed to privilege elevation.
    public static async Task<int> InstallAsync()
    {
        var missing = Missing();
        if (missing.Count == 0) return 0;
        var plan = InstallationPlan(missing);
        if (plan is null || !CanInstall(plan)) throw new InvalidOperationException("Install the packages in a terminal, then choose Check again.");
        var info = Proc.Info("/usr/bin/pkexec", ["/usr/bin/" + plan.Manager, .. plan.Arguments]);
        using var log = new StreamWriter(LogPath, true) { AutoFlush = true };
        log.WriteLine($"== {DateTime.UtcNow:O} {plan.Command}");
        using var process = Process.Start(info)!;
        process.StandardInput.Close();
        async Task Pump(StreamReader reader)
        {
            while (await reader.ReadLineAsync() is { } line) lock (log) log.WriteLine(line);
        }
        var output = Task.WhenAll(Pump(process.StandardOutput), Pump(process.StandardError));
        // A timeout must not kill a package manager while it is modifying the package database.
        await process.WaitForExitAsync();
        await output;
        log.WriteLine($"== exit {process.ExitCode}");
        return process.ExitCode;
    }
}

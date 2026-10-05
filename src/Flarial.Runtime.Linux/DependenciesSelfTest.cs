using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux;

static class DependenciesSelfTest
{
    public static Task<int> RunAsync()
    {
        var failures = 0;
        void Check(bool result, string name) { Console.WriteLine((result ? "ok   " : "FAIL ") + name); if (!result) failures++; }
        var script = SystemDependencies.Missing(c => c != "script");
        Check(script.Count == 1 && script[0].Command == "script", "missing script detected without starting downloader");
        Check(SystemDependencies.Missing(_ => true).Count == 0, "complete setup requires no install");
        var fedora = SystemDependencies.InstallationPlan(script, "ID=fedora\nVERSION_ID=44");
        Check(fedora?.Command == "sudo dnf install -y util-linux-script", "Fedora split script package");
        Check(SystemDependencies.InstallationPlan(script, "ID=nobara\nID_LIKE=\"fedora rhel\"")?.Command == fedora?.Command, "Fedora derivative prioritizes nearest family");
        Check(SystemDependencies.InstallationPlan(script, "ID=rocky\nID_LIKE=rhel")?.Arguments.Last() == "util-linux", "RHEL does not request Fedora split package");
        Check(SystemDependencies.InstallationPlan(script, "ID=ubuntu\nID_LIKE=debian")?.Arguments.Last() == "bsdutils", "Debian script package");
        var arch = SystemDependencies.InstallationPlan(SystemDependencies.Missing(c => c is not ("script" or "setsid")), "ID=cachyos\nID_LIKE=arch");
        Check(arch?.Arguments.Count(a => a == "util-linux") == 1 && !arch.Arguments.Contains("-Sy"), "Arch deduplicates packages and avoids partial database refresh");
        Check(SystemDependencies.InstallationPlan(script, "ID=opensuse-tumbleweed\nID_LIKE=\"suse opensuse\"")?.Manager == "zypper", "openSUSE supported");
        Check(SystemDependencies.InstallationPlan(script, "ID=unknown\nNAME=\"$(touch /tmp/flarial-invalid)\"") is null, "unknown OS never executes os-release contents");

        var dir = Path.Combine(Path.GetTempPath(), "flarial-dependency-test-" + Guid.NewGuid());
        var oldPath = Environment.GetEnvironmentVariable("PATH");
        try
        {
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "script"); File.WriteAllText(path, "#!/bin/sh\nexit 0\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Environment.SetEnvironmentVariable("PATH", dir);
            Check(SystemDependencies.Missing().Any(t => t.Command == "script"), "nonexecutable file is still missing");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            Check(!SystemDependencies.Missing().Any(t => t.Command == "script"), "recheck sees tool installed during session");
        }
        finally { Environment.SetEnvironmentVariable("PATH", oldPath); Directory.Delete(dir, true); }
        return Task.FromResult(failures == 0 ? 0 : 1);
    }
}

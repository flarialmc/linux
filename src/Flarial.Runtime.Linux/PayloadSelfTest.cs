using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Flarial.Runtime.Core;
using Flarial.Runtime.Linux.Injection;
using Flarial.Runtime.Linux.Launch;

namespace Flarial.Runtime.Linux;

/// <summary>
/// `--selftest-payload`: checks the launcher payload the client reads from its loader thread description.
/// Always: the JSON bytes and the client's acceptance rules (dll-css LauncherPayload / FlarialAccountSession::consumeLauncherPayload).
/// With FLARIAL_PAYLOAD_PROBE=&lt;dir with probe.dll and Minecraft.Windows.exe&gt; (see tests/payload-probe/run.sh, run with a scratch
/// XDG_DATA_HOME): delivery through the real InjectorCore + injector.exe into a Wine process, read back with GetThreadDescription.
/// </summary>
static class PayloadSelfTest
{
    static int s_fail;
    static void Ok(bool condition, string what) { Console.WriteLine((condition ? "ok   " : "FAIL ") + what); if (!condition) s_fail = 1; }

    /// <summary>The client's rules for the payload (a mirror of dll-css FlarialAccountSession::consumeLauncherPayload).</summary>
    static string? ClientToken(string payload)
    {
        if (payload.Length is 0 || payload.Length > 64 * 1024) return null;
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind is not JsonValueKind.Object) return null;
            if (!document.RootElement.TryGetProperty("access_token", out var token) || token.ValueKind is not JsonValueKind.String) return null;
            var value = token.GetString()!;
            if (value.Length is 0 or > 4096 || value.Any(c => c <= 0x20 || c >= 0x7f)) return null;
            return value;
        }
        catch (JsonException) { return null; }
    }

    public static async Task<int> RunAsync()
    {
        Ok(FlarialClient.CreatePayload("abc.DEF-123_xyz") == """{"access_token":"abc.DEF-123_xyz"}""", "payload: exact JSON for a token");
        Ok(FlarialClient.CreatePayload(null) == """{"access_token":null}""", "payload: signed out is a null token (upstream sends it too)");
        Ok(ClientToken(FlarialClient.CreatePayload("abc.DEF-123_xyz")) == "abc.DEF-123_xyz", "client accepts the token");
        Ok(ClientToken(FlarialClient.CreatePayload(null)) is null, "client ignores a signed out payload");
        Ok(ClientToken(FlarialClient.CreatePayload("tokén")) is null && FlarialClient.CreatePayload("tokén").All(c => c < 0x80), "non-ASCII is escaped, never raw (the injector forwards it as ASCII)");
        var big = new string('A', 4096);
        Ok(ClientToken(FlarialClient.CreatePayload(big)) == big, "client accepts the largest token (4096)");
        Ok(ClientToken(FlarialClient.CreatePayload(big + "A")) is null, "client rejects a 4097 character token");

        FlarialClient.AccessToken = "from-account";
        Ok(FlarialClient.CreatePayload(FlarialClient.AccessToken) == """{"access_token":"from-account"}""", "AccessToken feeds the payload");
        FlarialClient.AccessToken = null;
        Ok(FlarialClient.CreatePayload(FlarialClient.AccessToken) == """{"access_token":null}""", "logout clears it");

        if (Environment.GetEnvironmentVariable("FLARIAL_PAYLOAD_PROBE") is { Length: > 0 } probe)
            await WineAsync(probe);
        else
            Console.WriteLine("skip Wine delivery check (set FLARIAL_PAYLOAD_PROBE, see tests/payload-probe/run.sh)");

        return s_fail;
    }

    static async Task WineAsync(string dir)
    {
        // never touch a real prefix: only a fresh, scratch data home may be used
        if (Environment.GetEnvironmentVariable("XDG_DATA_HOME") is not { Length: > 0 } || Directory.Exists(Path.Combine(Paths.Prefix, "drive_c")) || ProcScan.PrefixPids().Count > 0)
        {
            Ok(false, "wine: refusing to run outside a scratch XDG_DATA_HOME with a fresh prefix");
            return;
        }

        var engine = Backend.Engine;
        if (!File.Exists(engine.Wine)) { Ok(false, "wine: engine missing at " + engine.Wine); return; }

        var env = new Dictionary<string, string?> { ["WINEPREFIX"] = Paths.Prefix, ["WINEDEBUG"] = "-all", ["WINEDLLOVERRIDES"] = "winemenubuilder.exe=d;mscoree=d;mshtml=d" };
        var ready = Path.Combine(dir, "ready.txt");
        File.Delete(ready);

        using var target = Process.Start(Proc.Info(engine.Wine, [Path.Combine(dir, "Minecraft.Windows.exe")], env))!;
        target.OutputDataReceived += static (_, _) => { }; target.ErrorDataReceived += static (_, _) => { };
        target.BeginOutputReadLine(); target.BeginErrorReadLine(); target.StandardInput.Close();
        try
        {
            for (var end = DateTime.UtcNow.AddSeconds(180); !File.Exists(ready) && DateTime.UtcNow < end && !target.HasExited;) await Task.Delay(250);
            if (!File.Exists(ready)) { Ok(false, "wine: the target process never started"); return; }

            var core = new InjectorCore(engine);
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)) + new string('t', 4000 - 32); // largest realistic token
            var payload = FlarialClient.CreatePayload(token);

            // the token must never be visible in any command line while the injector runs
            var leaked = false;
            using var stop = new CancellationTokenSource();
            var watch = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    foreach (var d in Directory.EnumerateDirectories("/proc"))
                        if (int.TryParse(Path.GetFileName(d), out var pid) && string.Join(' ', ProcScan.CmdLine(pid)).Contains(token[..32], StringComparison.Ordinal)) leaked = true;
                    await Task.Delay(20);
                }
            });

            async Task<string[]?> InjectAsync(string name, string? value, bool clearsEnvironment)
            {
                var copy = Path.Combine(dir, name + ".dll");
                File.Copy(Path.Combine(dir, "probe.dll"), copy, true);
                File.Delete(copy + ".out");
                var result = await core.InjectAsync([copy], (uint)target.Id, TimeSpan.Zero, value, CancellationToken.None);
                Ok(result.Ok, $"wine {name}: injector.exe loaded the DLL ({result.Message})");
                if (!result.Ok) return null;
                for (var end = DateTime.UtcNow.AddSeconds(5); !File.Exists(copy + ".out") && DateTime.UtcNow < end;) await Task.Delay(50);
                if (!File.Exists(copy + ".out")) { Ok(false, $"wine {name}: DllMain wrote no result"); return null; }
                _ = clearsEnvironment;
                return File.ReadAllLines(copy + ".out");
            }

            if (await InjectAsync("with-payload", payload, true) is { } with)
            {
                Ok(with.Length >= 2 && with[0] == "first=" + payload, "wine: DllMain got the exact payload through GetThreadDescription(GetCurrentThread())");
                Ok(with.Length >= 2 && with[1] == "after_clear=", "wine: clearing the description empties it (as the client does)");
            }

            if (await InjectAsync("without-payload", null, false) is { } without)
                Ok(without.Length >= 2 && without[0] == "first=", "wine: no payload means an empty description (custom DLLs)");

            stop.Cancel(); await watch;
            Ok(!leaked, "payload never appeared in a process command line");
        }
        finally
        {
            try { target.Kill(true); } catch { }
        }
    }
}

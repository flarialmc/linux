using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace Flarial.Runtime.Linux.Update;

/// <summary>--selftest-updater: fake signed archives served by a local HttpListener; checks install/swap/rollback/prune/tamper rejection.</summary>
static class UpdaterSelfTest
{
    static int _fail;
    static void Check(string name, bool ok) { Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}"); if (!ok) _fail++; }

    static async Task<bool> Throws(Func<Task> f) { try { await f(); return false; } catch { return true; } }

    public static async Task<int> RunAsync()
    {
        var dummy = new ProcessStartInfo("sleep", "30") { Environment = { ["WINEPREFIX"] = Paths.Prefix } };
        using (var d = Process.Start(dummy)!)
        {
            await Task.Delay(200);
            Check("pid filter: own chain in PrefixPids", Launch.ProcScan.PrefixPids().Contains(d.Id));
            Check("pid filter: own chain not a wine pid", !Launch.ProcScan.WinePids().Contains(d.Id));
            d.Kill();
        }
        var tmp = Directory.CreateTempSubdirectory("flarial-updtest").FullName;
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pub = key.ExportSubjectPublicKeyInfoPem();

        var files = new Dictionary<string, byte[]>();
        UpdateManifest Make(string v, string? fileVersion = null, ECDsa? signer = null, bool badSha = false)
        {
            var d = Path.Combine(tmp, "build-" + v, "Flarial.Launcher"); Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "version.txt"), fileVersion ?? v);
            File.WriteAllText(Path.Combine(d, "Flarial.Launcher"), "#!/bin/sh\n");
            var ar = Path.Combine(tmp, v + ".tar.zst");
            Process.Start("tar", ["--zstd", "-cf", ar, "-C", Path.GetDirectoryName(d)!, "Flarial.Launcher"])!.WaitForExit();
            var bytes = File.ReadAllBytes(ar); files["/" + v + ".tar.zst"] = bytes;
            var sig = (signer ?? key).SignData(bytes, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
            return new(v, $"{url}{v}.tar.zst", badSha ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(bytes)), bytes.Length, Convert.ToBase64String(sig));
        }

        var port = new Random().Next(20000, 40000);
        url = $"http://127.0.0.1:{port}/";
        using var http = new HttpListener(); http.Prefixes.Add(url); http.Start();
        string manifestJson = "";
        _ = Task.Run(async () =>
        {
            while (http.IsListening)
            {
                HttpListenerContext c; try { c = await http.GetContextAsync(); } catch { return; }
                var p = c.Request.Url!.AbsolutePath;
                var b = p == "/m.json" ? System.Text.Encoding.UTF8.GetBytes(manifestJson) : files.GetValueOrDefault(p);
                if (b is null) c.Response.StatusCode = 404; else { c.Response.ContentLength64 = b.Length; await c.Response.OutputStream.WriteAsync(b); }
                c.Response.Close();
            }
        });

        string J(UpdateManifest m) => $"{{\"version\":\"{m.Version}\",\"url\":\"{m.Url}\",\"sha256\":\"{m.Sha256}\",\"size\":{m.Size},\"signature\":\"{m.Signature}\"}}";
        var root = Path.Combine(tmp, "launcher");
        var u = new LauncherUpdater(root, url + "m.json", pub, _ => { });
        string? Cur() => u.CurrentVersion;
        string? Prev() => new DirectoryInfo(Path.Combine(root, "previous")).LinkTarget is { } t ? Path.GetFileName(t) : null;
        async Task<UpdateManifest?> Offer(UpdateManifest m, string installed) { manifestJson = J(m); return await u.CheckAsync(installed); }

        var v1 = Make("2026.1.1.1"); var v2 = Make("2026.1.1.2"); var v3 = Make("2026.1.1.3");
        Directory.CreateDirectory(root);

        var m1 = await Offer(v1, "0.0.0.0"); Check("check offers newer", m1 is not null);
        await u.InstallAsync(m1!); Check("install v1 -> current", Cur() == "2026.1.1.1" && Prev() is null);
        Check("exe is executable", (File.GetUnixFileMode(Path.Combine(root, "current", "Flarial.Launcher")) & UnixFileMode.UserExecute) != 0);
        Check("no offer when same version", await Offer(v1, "2026.1.1.1") is null);
        Check("no offer for downgrade", await Offer(v1, "2026.1.1.9") is null);

        await u.InstallAsync((await Offer(v2, "2026.1.1.1"))!); Check("install v2 -> current v2, previous v1", Cur() == "2026.1.1.2" && Prev() == "2026.1.1.1");
        await u.InstallAsync((await Offer(v3, "2026.1.1.2"))!);
        Check("install v3 prunes v1", Cur() == "2026.1.1.3" && Prev() == "2026.1.1.2" && !Directory.Exists(Path.Combine(root, "versions", "2026.1.1.1")));
        Check("staging cleaned", !Directory.Exists(Path.Combine(root, "staging")));

        var v4 = Make("2026.1.1.4");
        Check("tamper: bad sha rejected", await Throws(() => u.InstallAsync(v4 with { Sha256 = new string('0', 64) })) && Cur() == "2026.1.1.3");
        Check("tamper: bad size rejected", await Throws(() => u.InstallAsync(v4 with { Size = v4.Size + 1 })) && Cur() == "2026.1.1.3");
        Check("tamper: wrong-key signature rejected", await Throws(() => u.InstallAsync(Make("2026.1.1.4", signer: other))) && Cur() == "2026.1.1.3");
        Check("tamper: archive/manifest version mismatch rejected", await Throws(() => u.InstallAsync(Make("2026.1.1.5", fileVersion: "2026.1.1.1"))) && Cur() == "2026.1.1.3");
        Check("tamper: flipped archive byte rejected", await Throws(async () => { var m = Make("2026.1.1.6"); var b = files["/2026.1.1.6.tar.zst"]; b[10] ^= 1; await u.InstallAsync(m); }) && Cur() == "2026.1.1.3");

        // rollback
        var cur = Path.Combine(root, "versions", "2026.1.1.3");
        File.WriteAllText(Path.Combine(cur, ".started"), "");
        Check("no rollback within 60s", !u.RollbackIfUnhealthy());
        File.SetLastWriteTimeUtc(Path.Combine(cur, ".started"), DateTime.UtcNow.AddMinutes(-2));
        File.WriteAllText(Path.Combine(cur, ".healthy"), "");
        Check("no rollback when healthy", !u.RollbackIfUnhealthy());
        File.Delete(Path.Combine(cur, ".healthy"));
        Check("rollback when never healthy", u.RollbackIfUnhealthy() && Cur() == "2026.1.1.2");
        Check("rolled-back version not re-offered", await Offer(v3, "2026.1.1.2") is null);

        http.Stop(); Directory.Delete(tmp, true);
        Console.WriteLine(_fail == 0 ? "updater selftest OK" : $"updater selftest FAILED ({_fail})");
        return _fail == 0 ? 0 : 1;
    }

    static string url = "";
}

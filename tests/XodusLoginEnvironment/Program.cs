using System;
using System.Collections.Generic;
using System.Diagnostics;
using Flarial.Runtime.Linux;
using Flarial.Runtime.Linux.Xodus;

static void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("ok   " + message);
}

Dictionary<string, string?> nvidia = new() { ["DISPLAY"] = ":1", ["WAYLAND_DISPLAY"] = "wayland-0", ["HOME"] = "/isolated-xodus-home" };
XodusLoginEnvironment.Apply(nvidia, true);
Check(nvidia["GDK_BACKEND"] == "x11" && nvidia["WEBKIT_DISABLE_DMABUF_RENDERER"] == "1", "NVIDIA login uses XWayland and disables DMA-BUF");
Check(nvidia["WAYLAND_DISPLAY"] == "wayland-0" && nvidia["HOME"] == "/isolated-xodus-home", "desktop socket and isolated account home survive");

Dictionary<string, string?> overrides = new() { ["DISPLAY"] = ":1", ["GDK_BACKEND"] = "wayland", ["WEBKIT_DISABLE_DMABUF_RENDERER"] = "0" };
XodusLoginEnvironment.Apply(overrides, true);
Check(overrides["GDK_BACKEND"] == "wayland" && overrides["WEBKIT_DISABLE_DMABUF_RENDERER"] == "0", "explicit native Wayland and renderer overrides survive");

Dictionary<string, string?> otherGpu = new() { ["DISPLAY"] = ":1", ["WAYLAND_DISPLAY"] = "wayland-0" };
XodusLoginEnvironment.Apply(otherGpu, false);
Check(otherGpu.Count == 2, "non-NVIDIA environment remains unchanged");

foreach (var display in new string?[] { null, "", " " })
{
    Dictionary<string, string?> native = new() { ["WAYLAND_DISPLAY"] = "wayland-0" };
    if (display is not null) native["DISPLAY"] = display;
    XodusLoginEnvironment.Apply(native, true);
    Check(!native.ContainsKey("GDK_BACKEND") && native["WEBKIT_DISABLE_DMABUF_RENDERER"] == "1", "without XWayland, keep the native backend");
}

// Exercise the actual process environment path. The workaround must never reach the launcher or game.
var keys = new[] { "DISPLAY", "GDK_BACKEND", "WEBKIT_DISABLE_DMABUF_RENDERER" };
Dictionary<string, string?> saved = new();
foreach (var key in keys) saved[key] = Environment.GetEnvironmentVariable(key);
try
{
    Environment.SetEnvironmentVariable("DISPLAY", ":1");
    Environment.SetEnvironmentVariable("GDK_BACKEND", null);
    Environment.SetEnvironmentVariable("WEBKIT_DISABLE_DMABUF_RENDERER", null);
    var login = Proc.Info("/bin/sh", ["-c", "printf '%s/%s' \"$GDK_BACKEND\" \"$WEBKIT_DISABLE_DMABUF_RENDERER\""]);
    XodusLoginEnvironment.Apply(login.Environment, true);
    using var child = Process.Start(login)!;
    child.StandardInput.Close();
    var output = await child.StandardOutput.ReadToEndAsync();
    await child.WaitForExitAsync();
    Check(child.ExitCode == 0 && output == "x11/1", "login child receives compatibility settings");
    var game = Proc.Info("/bin/true", []);
    Check(!game.Environment.ContainsKey("GDK_BACKEND") && !game.Environment.ContainsKey("WEBKIT_DISABLE_DMABUF_RENDERER"), "launcher and subsequent game processes do not inherit login overrides");
}
finally
{
    foreach (var (key, value) in saved) Environment.SetEnvironmentVariable(key, value);
}

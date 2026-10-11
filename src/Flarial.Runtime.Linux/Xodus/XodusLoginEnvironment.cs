using System.Collections.Generic;

namespace Flarial.Runtime.Linux.Xodus;

internal static class XodusLoginEnvironment
{
    public static void Apply(IDictionary<string, string?> env, bool nvidiaDriverLoaded)
    {
        if (!nvidiaDriverLoaded) return;

        // NVIDIA can fail GBM allocation even under X11. Limit the workaround to the login webview.
        if (!env.ContainsKey("WEBKIT_DISABLE_DMABUF_RENDERER"))
            env["WEBKIT_DISABLE_DMABUF_RENDERER"] = "1";

        // XWayland keeps the login window usable inside a Wayland desktop. Respect explicit backend choices.
        if (!env.ContainsKey("GDK_BACKEND") && env.TryGetValue("DISPLAY", out var display) && !string.IsNullOrWhiteSpace(display))
            env["GDK_BACKEND"] = "x11";
    }
}

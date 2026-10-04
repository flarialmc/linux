using System.Text.Json.Nodes;

namespace Flarial.Runtime.Linux;

/// <summary>Optional advanced settings from Root/settings.json (no UI): {"inject_wait":"menu","inject_delay":120,"inject_settle_ms":0,"custom_env":"K=V K=V","ray_tracing":false,"diagnostics":false,"block_wine_gameinput":true}.</summary>
static class Settings
{
    static JsonObject Load() => Json.ReadObject(Paths.Settings);

    /// <summary>The most we wait after a launch for the game to be ready (0 = never wait).</summary>
    public static int InjectDelaySeconds => Load()["inject_delay"]?.GetValue<int>() ?? 120;
    /// <summary>Extra wait after the ready signal.</summary>
    public static int InjectSettleMs => Load()["inject_settle_ms"]?.GetValue<int>() ?? 0;
    /// <summary>"menu" (default, as the Windows launcher: menu_load_lock deleted), "swapchain" (first real vkd3d swapchain) or "none" (as soon as the process exists).</summary>
    public static string InjectWait => Load()["inject_wait"]?.GetValue<string>() ?? "menu";
    public static string CustomEnv => Load()["custom_env"]?.GetValue<string>() ?? "";
    public static bool RayTracing => Load()["ray_tracing"]?.GetValue<bool>() ?? false;
    /// <summary>Default true: disable Wine's builtin gameinput.dll in the game process. The client's SDL/GameInput loader otherwise creates a WineGDK DirectInput mouse bound to the foreground window, which steals the game's raw input when injected while the game is focused. The game itself uses the native GameInputRedist.dll.</summary>
    public static bool BlockWineGameInput => Load()["block_wine_gameinput"]?.GetValue<bool>() ?? true;
    public static bool Diagnostics => Load()["diagnostics"]?.GetValue<bool>() ?? false;
}

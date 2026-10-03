using System.Text.Json;
using AssetManager = global::Android.Content.Res.AssetManager;

namespace Allegiance.ClientCore.Android;

/// <summary>
/// P499 — the head's connect configuration, read from <c>Assets/connect.json</c>
/// (editable per device without rebuilding). No LAN/server address is
/// hard-coded into the app; the phone joins whatever host:port this file says
/// (the same server the DesktopGL head joins → cross-play). For a pre-release
/// sideload this is the pragmatic stand-in for an in-app connect screen
/// (touch controls / a GUI connect form are later steps).
/// </summary>
public sealed class AndroidConnectConfig
{
    public string Host { get; init; } = "127.0.0.1";
    public ushort Port { get; init; } = 2326;
    public string PlayerName { get; init; } = "android";

    /// <summary>Load the connect config from the APK asset
    /// <c>connect.json</c>. Missing/unparsable config degrades to loopback
    /// defaults (so the app still boots), which is the documented per-device
    /// edit point rather than a shipped server address.</summary>
    public static AndroidConnectConfig Load(AssetManager assets)
    {
        try
        {
            using var s = assets.Open("connect.json");
            using var reader = new StreamReader(s, System.Text.Encoding.UTF8);
            string json = reader.ReadToEnd();
            var cfg = JsonSerializer.Deserialize<AndroidConnectConfig>(json);
            if (cfg is not null && cfg.Port != 0 && !string.IsNullOrWhiteSpace(cfg.Host))
                return cfg;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[android] connect.json unreadable ({ex.Message}); using defaults");
        }
        return new AndroidConnectConfig();
    }
}

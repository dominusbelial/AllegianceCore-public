using Allegiance.ClientCore.MonoGame;
using AssetManager = global::Android.Content.Res.AssetManager;

namespace Allegiance.ClientCore.Android;

/// <summary>
/// P499 — the head's connect configuration, read from <c>Assets/connect.json</c>.
/// P500 — this is now ONLY the <b>first-run seed</b> for the in-app connect
/// screen: on the first launch it populates the saved-server list + player
/// name, which thereafter live in the app's private data dir
/// (<see cref="AndroidConnectStore"/>) and are editable in-game. A rebuild is
/// no longer needed to change server or name.
/// </summary>
public static class AndroidConnectConfig
{
    /// <summary>
    /// Load the connect config from the APK asset <c>connect.json</c>. Missing
    /// / unparsable config degrades to loopback defaults (so the app still
    /// boots). P500 FIX: parsed case-insensitively (the committed file is
    /// camelCase) and the EFFECTIVE values are logged, with a warning when the
    /// file is present but no field was recognised.
    /// </summary>
    public static ConnectConfigValues Load(AssetManager assets)
    {
        try
        {
            using var s = assets.Open("connect.json");
            using var reader = new StreamReader(s, System.Text.Encoding.UTF8);
            string json = reader.ReadToEnd();

            var parsed = ConnectConfigJson.Parse(json);
            if (parsed.Values is not null && parsed.HasConfig)
            {
                var v = parsed.Values;
                Console.WriteLine($"[android] connect.json seed: host={v.Host} port={v.Port} name='{v.PlayerName}' lobby='{(string.IsNullOrEmpty(v.LobbyUrl) ? "(none)" : v.LobbyUrl)}'");
                return v;
            }

            if (parsed.Warning is not null)
                Console.WriteLine($"[android] WARNING: {parsed.Warning}; using defaults");
            else
                Console.WriteLine("[android] connect.json unreadable; using defaults");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[android] connect.json unreadable ({ex.Message}); using defaults");
        }
        return new ConnectConfigValues();
    }
}

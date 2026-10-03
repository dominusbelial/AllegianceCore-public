using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Allegiance.ClientCore.MonoGame;
using Microsoft.Xna.Framework;   // AndroidGameActivity

namespace Allegiance.ClientCore.Android;

/// <summary>
/// P499 — the Android head's single entry Activity. It boots the SAME shared
/// <see cref="Game1"/> the DesktopGL head runs (so the phone and the desktop
/// client speak the same protocol to the same server → cross-play), feeding it
/// an <see cref="AndroidAssetSource"/> over the packed artwork and the connect
/// target from <c>Assets/connect.json</c>.
///
/// Landscape, immersive fullscreen and keep-screen-on are applied here;
/// Bluetooth gamepads need no runtime permission (HID input arrives through
/// MonoGame's GamePad API and drives the flight controls / dock navigation
/// implemented in the shared library — see GamePadFlightRoles +
/// Game1.ApplyGamePadFlight).
///
/// Lifecycle (item 4 of the task): OnPause closes the wire session cleanly
/// (<see cref="Game1.NotifyPaused"/>), OnResume offers a reconnect
/// (<see cref="Game1.NotifyResumed"/>), and the Activity is NOT recreated on
/// rotation (ConfigurationChanges), so players stay seated like the P480
/// desktop path.
/// </summary>
[Activity(
    Label = "Allegiance",
    MainLauncher = true,
    Theme = "@android:style/Theme.NoTitleBar.Fullscreen",
    ScreenOrientation = ScreenOrientation.Landscape,
    ConfigurationChanges = ConfigChanges.Orientation
        | ConfigChanges.ScreenSize
        | ConfigChanges.KeyboardHidden
        | ConfigChanges.Keyboard)]
public class MainActivity : AndroidGameActivity
{
    private Game1? _game;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);

        // Fullscreen + keep-screen-on via the window flags. (Deliberately no
        // SystemUiVisibility/ImmersiveSticky here: that triggers Android's
        // "Viewing full screen" one-shot tooltip overlay over the game and adds
        // nothing the Fullscreen flag doesn't already do for a sideloaded head.)
        var win = Window;
        win?.AddFlags(WindowManagerFlags.Fullscreen | WindowManagerFlags.KeepScreenOn);

        // P500 — the in-app connect screen. connect.json is now only the
        // FIRST-RUN SEED: its host/port/name/lobbyUrl are folded into the
        // persisted connect profile (app-private data dir) once, after which the
        // player edits servers/name in-game (soft keyboard) and the changes
        // survive reboots. We boot into the shared server-browser (Lobby) state
        // with no DirectHost, so the connect screen shows instead of an
        // unconditional direct connect — no rebuild to change server or name.
        var seed = AndroidConnectConfig.Load(Assets!);
        var store = new AndroidConnectStore(this);
        var profile = store.Load();

        if (!profile.Initialized && !string.IsNullOrWhiteSpace(seed.Host))
        {
            profile.Servers.Add(new SavedServer(seed.Host, seed.Host, unchecked((ushort)seed.Port)));
            if (!string.IsNullOrWhiteSpace(seed.PlayerName))
                profile.PlayerName = seed.PlayerName;
            if (!string.IsNullOrWhiteSpace(seed.LobbyUrl))
                profile.LobbyUrl = seed.LobbyUrl;
            profile.Initialized = true;
            store.Save(profile);
            Console.WriteLine($"[android] first-run: seeded connect profile from connect.json ({seed.Host}:{seed.Port}, '{profile.PlayerName}')");
        }

        Console.WriteLine($"[android] connect screen: {profile.Servers.Count} saved server(s), player '{profile.PlayerName}', lobby '{(profile.LobbyUrl.Length == 0 ? "(none)" : profile.LobbyUrl)}'");

        _game = new Game1(new Game1.LaunchOptions
        {
            PlayerName = profile.PlayerName,
            // No DirectHost: boot into the server-browser / connect screen.
            ConnectStore = store,
            TextPrompt = null,                       // Game1 defaults to MonoGame's KeyboardInput (soft keyboard)
            LobbyUrl = profile.LobbyUrl,             // empty = no lobby (manual connect only; 221 runs no lobby)
            AssetSource = new AndroidAssetSource(Assets!),
        });

        SetContentView((View)_game.Services.GetService(typeof(View))!);
        _game.Run();
    }

    protected override void OnPause()
    {
        _game?.NotifyPaused();   // close the connection cleanly on background
        base.OnPause();
    }

    protected override void OnResume()
    {
        base.OnResume();
        _game?.NotifyResumed();  // offer reconnect on foreground
    }

    protected override void OnDestroy()
    {
        _game?.NotifyPaused();
        _game?.Dispose();
        _game = null;
        base.OnDestroy();
    }
}

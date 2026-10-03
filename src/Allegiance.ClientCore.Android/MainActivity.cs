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

        var cfg = AndroidConnectConfig.Load(Assets!);
        Console.WriteLine($"[android] connect target {cfg.Host}:{cfg.Port} as '{cfg.PlayerName}' (edit Assets/connect.json)");

        _game = new Game1(new Game1.LaunchOptions
        {
            PlayerName = cfg.PlayerName,
            DirectHost = cfg.Host,
            DirectPort = cfg.Port,
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

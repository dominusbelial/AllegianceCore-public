using Android.App;
using Android.Content.PM;
using Android.OS;
using Android.Views;
using Allegiance.ClientCore.MonoGame;
using Microsoft.Xna.Framework;   // AndroidGameActivity
using System.Reflection;
using XnaKeyboard = Microsoft.Xna.Framework.Input.Keyboard;

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

    // P501 — cached reflection handles for MonoGame.Android's INTERNAL
    // Keyboard.KeyDown/KeyUp (they take an Android View Keycode). See
    // DispatchKeyEvent for why reflection is needed (the vendor-gate workaround).
    private static MethodInfo? _kbKeyDown;
    private static MethodInfo? _kbKeyUp;

    /// <summary>
    /// P501 — fixes the blocker where Android hardware-keyboard events never
    /// reach MonoGame's <c>Keyboard.GetState()</c> (observed 2026-10-02: an
    /// injected <c>adb shell input keyevent KEYCODE_Y</c> produced no ready-check
    /// log line while the same key worked on desktop).
    ///
    /// Root cause (inspected in MonoGame.Framework.Android 3.8.5.1's
    /// <c>MonoGameAndroidGameView.OnKeyDown/OnKeyUp</c>): keys are forwarded to
    /// MonoGame's <c>Keyboard</c> only when <c>IsKeyboard(device)</c> passes —
    /// that requires the event's device to report <c>SOURCE_KEYBOARD|SOURCE_DPAD</c>
    /// <b>and</b> a NON-ZERO vendor/product id. The <c>adb</c>/emulator "virtual"
    /// keyboard (and several cheap keyboards) report vendor/product = 0, so the
    /// gate silently drops every injected key. A real Bluetooth keyboard (nonzero
    /// vendor/product) does pass the gate and reaches the view's focus (MonoGame
    /// requests it on resume) — so real keyboards mostly worked — but the virtual
    /// keyboard that the emulator/ADB and some devices present never did.
    ///
    /// Fix: intercept key events at the Activity's <c>DispatchKeyEvent</c> (the
    /// entry point for every hardware key, virtual or real) and feed the keycode
    /// straight into MonoGame's <c>Keyboard</c> state for ANY keyboard-class
    /// device, bypassing the faulty vendor gate. The call is idempotent (KeyDown
    /// sets a pressed bit; KeyUp clears it), so real keyboards that ALSO reach the
    /// view's OnKeyDown are not double-processed — the state is simply set twice
    /// to the same value. A Bluetooth keyboard thus works too.
    /// </summary>
    public override bool DispatchKeyEvent(KeyEvent? e)
    {
        try
        {
            var device = e?.Device;
            bool isKeyboard = device is not null
                && (device.Sources & InputSourceType.Keyboard) != 0;
            if (isKeyboard)
            {
                _kbKeyDown ??= typeof(XnaKeyboard).GetMethod(
                    "KeyDown", BindingFlags.Static | BindingFlags.NonPublic);
                _kbKeyUp ??= typeof(XnaKeyboard).GetMethod(
                    "KeyUp", BindingFlags.Static | BindingFlags.NonPublic);
                var m = e!.Action == KeyEventActions.Down ? _kbKeyDown : _kbKeyUp;
                m?.Invoke(null, new object[] { e.KeyCode });
            }
        }
        catch
        {
            // Never let input routing take down the game; fall back to base.
        }
        return base.DispatchKeyEvent(e!);
    }

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

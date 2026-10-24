using Allegiance.ClientCore.Session;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public partial class Game1
{
    private readonly IdentityScreenModel _identityScreen = new();
    private LobbyIdentityClient? _identityClient;
    private string _identityOrigin = "";
    private Task? _identityTask;
    private bool _identityKey;
    private CancellationTokenSource? _identityCancellation;

    private void UpdateIdentity(KeyboardState keyboard)
    {
        bool down = keyboard.IsKeyDown(Keys.F2);
        if (down && !_identityKey && _identityTask is { IsCompleted: false }) _identityCancellation?.Cancel();
        if (down && !_identityKey && _identityTask is not { IsCompleted: false })
        {
            string origin = string.IsNullOrEmpty(_lobbyUrl) ? ServerListing.LobbyUrlDefault : _lobbyUrl;
            if (_identityClient is not null && _identityOrigin != origin && _identityClient.Account is null)
            {
                _identityClient.Dispose(); _identityClient = null;
                _identityTask = ToggleIdentityAsync(origin);
            }
            else _identityTask = ToggleIdentityAsync(origin);
        }
        _identityKey = down;
        // UI state is copied only on the game thread after the task has completed.
        if (_identityTask is { IsCompleted: true })
        {
            _identityScreen.Account = _identityClient?.Account;
            if (_identityScreen.Account is { } account)
            {
                _playerName = account.Callsign;
                _pilotBuf.Clear(); _pilotBuf.Append(account.Callsign);
                _introEditingPilot = false;
            }
            _identityTask = null;
        }
    }
    private async Task ToggleIdentityAsync(string origin)
    {
        try
        {
            _identityCancellation?.Dispose();
            _identityCancellation = new CancellationTokenSource();
            _identityClient ??= new LobbyIdentityClient(origin);
            _identityOrigin = origin;
            if (_identityClient.Account is not null)
            {
                await _identityClient.SignOutAsync();
                _identityClient.Dispose(); _identityClient = null;
                _identityScreen.Device = null;
                _identityScreen.Status = "Signed out. F2 sign in with a passkey (optional)";
                return;
            }
            var device = await _identityClient.StartAsync(_identityCancellation.Token);
            _identityScreen.Device = device;
            _identityScreen.Status = "Approve this code in your browser; expires in 5 minutes";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(device.VerificationUri) { UseShellExecute = true }); }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or PlatformNotSupportedException) { /* URL remains visible for manual opening. */ }
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_identityCancellation.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(device.ExpiresIn));
            while (true)
            {
                await Task.Delay(TimeSpan.FromSeconds(device.Interval), deadline.Token);
                string status = await _identityClient.PollAsync(device, deadline.Token);
                if (status == "approved") { _identityScreen.Status = "Signed in with your lobby account"; break; }
                if (status == "expired") throw new OperationCanceledException();
            }
        }
        catch (OperationCanceledException) { _identityScreen.Status = "Sign-in cancelled or expired. Press F2 to try again"; }
        catch (Exception e) when (e is HttpRequestException or ArgumentException or InvalidOperationException)
        { _identityScreen.Status = "Sign-in failed: " + e.Message; }
        finally { _identityScreen.Device = null; }
    }
}

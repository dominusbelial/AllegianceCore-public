using Allegiance.Core.Identity;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Shared by Classic and Modern; account identity is independent of HUD artwork.</summary>
public sealed class IdentityScreenModel
{
    // One async operation publishes status/code; the render thread reads immutable references.
    private volatile string _status = "F2 sign in with a passkey (optional)";
    private volatile DeviceAuthorization? _device;
    public string Status { get => _status; set => _status = value; }
    public DeviceAuthorization? Device { get => _device; set => _device = value; }
    public IdentityAccount? Account { get; set; }
    public string PilotLabel(string anonymousName) => Account is { } a ? $"Pilot: {a.Callsign} (verified)  |  F2 sign out" : $"Pilot: {anonymousName} (unverified)";
    public string ApprovalLine => Device is { } d ? $"Code: {d.UserCode}  |  {d.VerificationUri}" : Status;
}

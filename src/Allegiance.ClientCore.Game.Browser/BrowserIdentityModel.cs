using Allegiance.Core.Identity;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Browser identity adapter for the shared Classic/Modern intro. Desktop model stays unchanged.</summary>
public sealed class BrowserIdentityModel
{
    public IdentityAccount? Account { get; set; }
    public string? Guest { get; set; }
    public string ApprovalLine => "Play online to choose a guest identity or sign in (optional).";
    public string PilotLabel(string _) => Account is { } account ? "Signed in: " + account.Callsign
        : Guest is { Length: > 0 } guest ? "Guest: " + guest : "Choose Play online - guest or sign in";
}

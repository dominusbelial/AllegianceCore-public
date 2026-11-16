namespace Allegiance.Lobby.BrowserPreview;

public sealed class BrowserPreviewSettings
{
    public bool Enabled { get; set; }
    // A published browser wwwroot, outside the lobby's ordinary wwwroot.
    public string BundlePath { get; set; } = "browser-preview";
    public string ArtworkPath { get; set; } = "";
}

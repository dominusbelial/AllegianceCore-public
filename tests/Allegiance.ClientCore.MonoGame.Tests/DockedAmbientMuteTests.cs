namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Regression: projectile/probe/aleph ambient loops kept playing in the hangar,
/// because only the flight-frame updates stop them. Docked, the updates run muted:
/// only the planners' Stop commands are replayed (no Start/Update/one-shots).</summary>
public sealed class DockedAmbientMuteTests
{
    private enum Act { Start, Update, Stop, Explosion }

    [Fact]
    public void Muted_replays_only_stops()
    {
        var cmds = new List<Act> { Act.Start, Act.Update, Act.Stop, Act.Explosion, Act.Stop };
        Assert.Equal(new[] { Act.Stop, Act.Stop }, Game1.AmbientCommandsFor(true, cmds, a => a == Act.Stop));
    }

    [Fact]
    public void Unmuted_replays_everything()
    {
        var cmds = new List<Act> { Act.Start, Act.Update, Act.Stop, Act.Explosion };
        Assert.Equal(cmds, Game1.AmbientCommandsFor(false, cmds, a => a == Act.Stop));
    }
}

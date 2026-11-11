namespace Allegiance.Core.Sim;

/// <summary>
/// P993 — the ONE distance at which the port's client will draw a
/// <c>PROJECTILE_INSTANCE</c> round around the local ship, shared by the
/// renderer (which culls to it) and the server's fire relay (which, behind
/// <c>broadcast.fireAudienceCull</c>, stops sending rounds to viewers that can
/// never draw them).
/// <para><b>Why it lives in Core.</b> The value was born as the MonoGame
/// renderer's private <c>Game1.ProjectileVisibilityRadiusSqr</c> (P231), was
/// centralised for the headless draw path in
/// <c>Allegiance.ClientCore.State.ProjectileVisibility</c> (P542), and is now
/// needed by the server audience too. <c>Allegiance.Server</c> references
/// <c>Allegiance.Core</c> only (never the client), so the number itself lives
/// here and both sides reference this single definition:
/// <list type="bullet">
/// <item><see cref="Allegiance.ClientCore.State.ProjectileVisibility.VisibilityRadius"/>
/// (the renderer's own cull, <c>Game1.Rendering.cs</c> bolt loop and
/// <c>ClientWorld.DiagnoseDraw</c>);</item>
/// <item><c>Server.FireAudience.ProjectileDrawRangeKeeps</c> (the P993 relay
/// cull).</item>
/// </list>
/// Changing the renderer's reach therefore changes the relay's with it — there
/// is no second copy to drift.</para>
/// <para><b>Units.</b> World units, centred on the viewer's own ship pose
/// (<c>Game1.Rendering.cs</c> anchors the bolt test on
/// <c>world.Ships[myShip].Latest.Position</c>).</para>
/// </summary>
public static class ProjectileDrawRadius
{
    /// <summary>5900 world units — the client's projectile draw radius around
    /// its own ship (formerly <c>Game1.ProjectileVisibilityRadiusSqr</c>'s
    /// literal, P231; the "sector filter" analog for a wire that carries no
    /// projectile cluster id, roughly the camera far plane).</summary>
    public const float WorldUnits = 5900f;

    /// <summary><see cref="WorldUnits"/> squared, for the renderer's and the
    /// relay's squared-distance comparisons (no square root on either path).</summary>
    public const float WorldUnitsSquared = WorldUnits * WorldUnits;
}

using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>The own-ship state the HUD-6 status readouts actually come from.
/// Kept here (not inline in <c>Game1</c>) so the DATA SOURCE — not only the
/// model switch — is pinned by tests: native reads the parent (source) ship for
/// the carried flag/artifact and the signature (<c>WinTrek/console.cpp:845</c>,
/// <c>:865-866</c>), the autopilot lamp from <c>trekClient.autoPilot()</c>
/// (<c>WinTrek/console.cpp:793</c>) and vector lock from the echoed
/// <c>StateM &amp; CoastButton</c> (<c>WinTrek/consoledata.cpp:465</c>).</summary>
public readonly record struct OwnStatusTelemetry(
    ShipEntity SourceShip,
    bool AutopilotEngaged,
    bool VectorLockEngaged,
    bool CarriedFlag,
    bool CarriedArtifact);

public static class OwnStatusData
{
    /// <summary>Native <c>trekClient.GetShip()-&gt;GetSourceShip()</c>
    /// (WinTrek/console.cpp:845): while riding a turret the carrier is the
    /// source ship, so a gunner sees the carrier's flag/artifact and signature.
    /// Falls back to the own entity if the parent is not known yet.</summary>
    public static ShipEntity SourceShip(ShipEntity own,
        IReadOnlyDictionary<short, ShipEntity> ships)
        => own.ParentId >= 0 && ships.TryGetValue(own.ParentId, out var parent)
            ? parent : own;

    /// <summary>Resolve the HUD status inputs from client state. The flag and
    /// artifact always come from the source ship (console.cpp:846-861).</summary>
    public static OwnStatusTelemetry FromClientState(ShipEntity own,
        IReadOnlyDictionary<short, ShipEntity> ships, bool autoPilot, int stateM)
    {
        var source = SourceShip(own, ships);
        return new(
            source,
            autoPilot,
            (stateM & ShipControlState.CoastButton) != 0,
            source.CarriedFlagSideId >= 0,
            source.CarriedFlagSideId == Side.ArtifactSentinelFlag);
    }

    /// <summary>Native signature percentage, <c>100 * GetSignature() /
    /// side.GetGlobalAttributeSet().GetAttribute(c_gaSignature)</c>
    /// (WinTrek/console.cpp:865-866), with the native per-part contribution
    /// rules:
    /// <list type="bullet">
    ///   <item>a weapon adds its signature only while firing
    ///         (weaponIGC.h:103-122 — Activate/Deactivate);</item>
    ///   <item>afterburner and cloak add theirs only while active
    ///         (afterburnerIGC.h:78,89; cloakigc.h:76,89);</item>
    ///   <item>shields and launchers contribute whenever mounted
    ///         (shieldIGC.h:72, magazineigc.h:74 — <c>fActive</c> is the
    ///         always-mounted slot 0);</item>
    ///   <item>the whole hull signature is multiplied by the cloak factor
    ///         (shipIGC.h:268-270).</item>
    /// </list>
    /// The wire has no continuous <c>GetCloaking()</c> ramp, so
    /// <paramref name="cloakActive"/> uses the engaged end state (factor 0);
    /// the continuous ramp remains the documented gap.</summary>
    public static float? SignaturePercent(
        float hullSignature,
        IReadOnlyList<ExpandedPartData>? loadout,
        Func<short, DataPartTypeIGC?> resolve,
        bool firing, bool groupFire, int selectedMount,
        bool afterburnerActive, bool cloakActive,
        float sideSignature)
    {
        if (sideSignature <= 0f) return null;
        float signature = hullSignature;
        if (loadout is not null)
        {
            foreach (var mounted in loadout)
            {
                // Negative mount (the byte's high bit) = cargo, never
                // mounted/active (native part activation is per mount).
                int mount = unchecked((sbyte)mounted.MountId);
                if (mount < 0) continue;
                if (resolve(mounted.PartId) is not { } part) continue;
                bool counts = part.EquipmentType switch
                {
                    EquipmentType.Weapon =>
                        firing && (groupFire || mount == selectedMount),
                    EquipmentType.Afterburner => afterburnerActive,
                    EquipmentType.Cloak => cloakActive,
                    // Shield/launcher/dispenser/chaff: always mounted => always
                    // contributes (shieldIGC.h:72, magazineigc.h:74).
                    _ => true,
                };
                if (counts) signature += part.Signature;
            }
        }
        // shipIGC.h:268-270 — GetSignature() is the accumulated value times
        // m_cloaking. The own-ship wire carries only the engaged bit.
        if (cloakActive) signature = 0f;
        return 100f * signature / sideSignature;
    }
}

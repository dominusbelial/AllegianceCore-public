using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P816 — the last sim-rule leftovers (report <c>docs/reports/P816_SIM_LEFTOVERS.md</c>).
/// <list type="bullet">
/// <item><b>SIM-0239</b> — the magazine is a native ready-notification producer
/// like the weapon/cloak/shield/dispenser: when a mounted magazine's
/// <c>m_mountedFraction</c> reaches 1, native plays <c>mountedSound</c> + posts
/// "%s ready." on the ship (<c>Igc/magazineigc.cpp:96-97</c>). The port rides
/// the same <c>IIgcSite.WeaponReady</c> seam the other parts use
/// (<c>Ship.AdvanceFlightMounts</c> → <c>BroadcastWeaponReady</c> →
/// VoiceOver=1023 <c>mountedSound</c>/<c>unmount.ogg</c> playback, the P676
/// pipeline).</item>
/// <item><b>SIM-0014</b> — closed with measurement only (no code): every
/// shipped core in the artwork corpus carries <c>FloatConstants[13]</c>
/// (<c>c_fcidRipcordTime</c>) = 10.0 and the hull ripcord values are the
/// baked <c>ShipTypes_ripcordSpeed × slot 13</c> products (see the report for
/// the <c>BuildIGCFiles</c>/<c>LoadDbCache</c> evidence); the port's
/// asset-sourced hull value therefore already matches native's effective
/// <c>GetRipcordSpeed()</c> input. Covered here by a regression guard that the
/// shipped core's slot 13 stays at the native default the asset bake used.</item>
/// </list>
/// </summary>
public sealed class P816SimLeftoversTests
{
    private static Mission Domain(IIgcSite? site = null, int sideCount = 2)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var clusters = Enumerable.Range(0, 3).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = (short)i }).ToArray();
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], []),
            Enumerable.Range(0, sideCount).Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray(), site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        return m;
    }

    private static Ship Place(Mission m, short id = 100, short side = 0, short sector = 0)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            Mass = 100, Thrust = 0, Speed = 1000, MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0,
            HitPoints = 1000, Length = 10, HabmCapabilities = 0, RipcordSpeed = 1,
            MaxFixedWeapons = 1, MaxWeapons = 1, CapacityMagazine = 1000, CapacityDispenser = 1000,
            Hardpoints = [new(0, 0, "", "", short.MaxValue, true)], ScannerRange = 10000
        };
        var s = new Ship(id, hull, new(0, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = side, IsNpc = true, PilotType = Ship.PilotTypeWingman };
        m.PlaceShip(s, sector); return s;
    }

    private static DataPartTypeIGC Part(DataPartTypeIGC.Subtype subtype) =>
        new(0, 0, "", "", "Test nan", "", 0, [], [], 0, 0, 32000, -1, 0, "", subtype);

    private static void Call(Ship ship, string method, params object[] args) =>
        typeof(Ship).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args);

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Magazine_ready_notification_occurs_once_on_float_completion_and_again_after_remount()
    {
        // SIM-0239 — native magazineigc.cpp:96-97 fires the ready notification
        // on the magazine's own mount completion, exactly like the weapon
        // (weaponIGC.h:44-46, the P676 precedent this test mirrors).
        Require.Artwork();
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var s = Place(m);
        var part = s.MountPart(Part(new DataPartTypeIGC.MissileLauncher(0)), 1, 0, 0)!;
        Assert.Equal(EquipmentType.Magazine, part.EquipmentType);
        m.Core.Constants.FloatConstants[10] = 1;

        Call(s, "AdvanceFlightMounts", 0.999f);
        Assert.DoesNotContain(site.Calls, c => c.StartsWith("WeaponReady("));

        Call(s, "AdvanceFlightMounts", 0.002f); Call(s, "AdvanceFlightMounts", 1f);
        Assert.Single(site.Calls, c => c.StartsWith("WeaponReady("));

        // A remount (cargo swap resets the fraction) re-cues exactly once.
        Call(s, "ResetEquippedFraction", EquipmentType.Magazine);
        Call(s, "AdvanceFlightMounts", 1f);
        Assert.Equal(2, site.Calls.Count(c => c.StartsWith("WeaponReady(")));
        Assert.Equal(1, part.MountedFraction);
    }

    [SkippableFact, Trait("Requires", "Artwork")]
    public void Shipped_core_ripcord_slot_stays_at_the_native_asset_bake_default()
    {
        // SIM-0014 (measurement-closed, no code) — the shipped core's
        // FloatConstants[13] (c_fcidRipcordTime) is the value
        // BuildIGCFiles/LoadDbCache (FedSrv.CPP:1676,9712-9720) multiplied the
        // DB ripcordSpeed by when the asset was written; the port reads the
        // baked product, so a core that ever changed slot 13 would need the
        // P788 re-evaluation, not a runtime multiply. Guard the shipped default.
        Require.Artwork();
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        Assert.True(core.Constants.FloatConstants.Length > 13);
        Assert.Equal(10.0f, core.Constants.FloatConstants[13]);   // c_dtRipcord, missionigc.cpp:2700
    }
}

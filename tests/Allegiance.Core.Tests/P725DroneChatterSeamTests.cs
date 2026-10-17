using System.Linq;
using Allegiance.Core.Collision;
using Allegiance.Core.MapGen;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P725 — the drone/constructor in-flight chatter seam. The server-side AI
/// edges already existed in <see cref="Ship"/> (<c>SendDroneChat</c> at the
/// native runaway / coming-home-empty / too-crowded moments), but broadcast
/// SoundIDs that were silently wrong (+1001 off the true sounds.h ordinals)
/// and routed through the un-overridden <c>IIgcSite.SendChat</c> no-op, so the
/// chatter never reached a client audibly. P725 fixes the ordinals to P723's
/// verified <see cref="DroneChatterCue"/> values and routes the chatter through
/// a new <see cref="IIgcSite.DroneChatEvent"/> seam that the server host turns
/// into a team-chat broadcast carrying the SoundID as the wire
/// <c>VoiceOver</c> (which the client resolves via
/// <c>Game1.Session.ProcessIncomingQuickChatVoices</c> → <c>DroneChatterCue</c>).
/// </summary>
/// <remarks>
/// The tests are deliberately deterministic (no live server): they pin the
/// four native chatter SoundIDs to their verified ordinals, prove each
/// resolves to a real client-playable clip, and prove a real chatter edge
/// routes through <see cref="RecordingIgcSite.DroneChatEvent"/> with the
/// correct id — the wire seam the host converts to the broadcast.
/// </remarks>
public sealed class P725DroneChatterSeamTests
{
    private static Mission Domain(IIgcSite? site = null)
    {
        var core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
        var clusters = Enumerable.Range(0, 3).Select(i => DataClusterIGC.Decode(new byte[DataClusterIGC.Size]) with { ClusterId = (short)i }).ToArray();
        var civ = core.Civilizations.Keys.First();
        var m = Mission.Assemble(core, MissionMap.FromLists(clusters, [], [], []),
            Enumerable.Range(0, 2).Select(i => new SideSpec((short)i, $"Side {i}", civ)).ToArray(), site ?? new NullIgcSite());
        m.Stage = MissionStage.Started;
        foreach (var side in m.Sides) side.GlobalAttributes = GlobalAttributeSet.Identity;
        return m;
    }

    private static Ship Place(Mission m, short id = 100, short side = 0)
    {
        var hull = m.Core.HullTypes.Values.First() with
        {
            Mass = 100, Thrust = 0, Speed = 1000, MaxEnergy = 1000, MaxAmmo = 1000, RechargeRate = 0,
            HitPoints = 1000, Length = 10, HabmCapabilities = 0, RipcordSpeed = 1,
            MaxFixedWeapons = 1, MaxWeapons = 1, CapacityDispenser = 1000,
            Hardpoints = [new(0, 0, "", "", short.MaxValue, true)], ScannerRange = 10000
        };
        var s = new Ship(id, hull, new(0, 0, 0), Vector3.Zero, Orientation.Identity)
            { SideId = side, IsNpc = true, PilotType = Ship.PilotTypeMiner };
        m.PlaceShip(s, 0); return s;
    }

    /// <summary>
    /// The four drone-chatter SoundIDs P725 fixes (constructor-running 342,
    /// too-much-damage 326, coming-home-empty 328, too-crowded 341) must be
    /// P723's verified native ordinals — NOT the previous Ship.cs literals that
    /// were +1001 higher (1327/1329/1342/1343) and therefore unresolvable by the
    /// client (silent chatter). Guards the regression if anyone reintroduces the
    /// off-by-thousand values.
    /// </summary>
    [Fact]
    public void Four_native_chatter_sound_ids_are_the_verified_ordinals_and_resolve()
    {
        Assert.Equal(326, DroneChatterCue.DroneTooMuchDamageSound);      // sounds.h:380
        Assert.Equal(328, DroneChatterCue.DroneComingHomeEmptySound);    // sounds.h:382
        Assert.Equal(341, DroneChatterCue.DroneTooCrowdedSound);         // sounds.h:395
        Assert.Equal(342, DroneChatterCue.ConstructorRunningSound);      // sounds.h:396

        // Every one of the four must resolve to a real client-playable clip —
        // if it returned null the client would ignore the broadcast (the bug
        // the +1001 values caused).
        foreach (var id in new short[] { 326, 328, 341, 342 })
            Assert.NotNull(DroneChatterCue.Resolve(id));
    }

    /// <summary>
    /// A real chatter edge — <see cref="Ship.ComplainMiningCrowded"/> (native
    /// droneTooCrowdedSound, shipIGC.cpp:2199) — routes through the new
    /// <see cref="IIgcSite.DroneChatEvent"/> seam carrying the corrected
    /// SoundID (341). Failing-first: pre-P725 this routed through the no-op
    /// <c>SendChat</c> with the buggy 1342, so the site recorded no
    /// <c>DroneChatEvent</c>.
    /// </summary>
    [Fact]
    public void Crowded_miner_routes_droneTooCrowdedSound_341_through_DroneChatEvent()
    {
        var site = new RecordingIgcSite();
        var m = Domain(site);
        var s = Place(m, side: 0);

        s.ComplainMiningCrowded(now: 30.0);   // past the 30 s cooldown

        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("341", call);                                   // DroneTooCrowdedSound
        Assert.Contains("\"Miner requesting unoccupied He3 asteriod.\"", call);
    }

    /// <summary>
    /// RecordingIgcSite observes the new seam directly (argument shape:
    /// ship-type, sideId, soundId, quoted text).
    /// </summary>
    [Fact]
    public void RecordingIgcSite_records_DroneChatEvent()
    {
        var site = new RecordingIgcSite();
        var m = Domain();
        var s = Place(m, side: 1);

        site.DroneChatEvent(s, (short)1, (short)328, "Coming home empty");

        var call = Assert.Single(site.Calls, c => c.StartsWith("DroneChatEvent("));
        Assert.Contains("1,328", call.Replace(" ", ""));           // sideId 1, soundId 328
        Assert.Contains("\"Coming home empty\"", call);
    }
}

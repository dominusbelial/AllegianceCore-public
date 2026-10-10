using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P135 — live loopback proof of the periodic per-side <c>PROBES_UPDATE</c>(92)
/// broadcast (<see cref="Srv.BroadcastProbesUpdate"/>, called every
/// <see cref="Srv.TickSim"/>), the probe sibling of P133's
/// <c>STATIONS_UPDATE</c> and P134's <c>ASTEROIDS_UPDATE</c> — the third and
/// last consumer of P131/P132's real visibility, and the one that needed
/// <b>no</b> P134-style sweep extension because a probe is a real
/// <see cref="Model"/> carrying a <c>SideId</c> (so the sweep's own probe case
/// already scores it as both a target and a scanner for its own side,
/// Sector.cs:2133-2136):
/// <list type="number">
/// <item>a real probe (deployed via <c>Ship.DeployProbe</c>) is delivered in its
/// own side's <c>PROBES_UPDATE</c> with the correct <c>probeID</c> and its
/// div-240 <b>health</b> fraction (full = 240 for an undamaged probe) — and, by
/// the same-side rule, <b>always</b> included for its own side with no scan
/// needed;</item>
/// <item>the <b>same</b> probe, not visible to side B (B's only scanner ship is
/// 30 km away, far beyond any detection radius), is <b>absent</b> from B's
/// updates — indeed B receives <b>no</b> <c>PROBES_UPDATE</c> at all, because
/// the demo map has zero probes and the only probe is invisible to it — the
/// per-side batching proof (each side receives only what it can see, not the
/// whole mission);</item>
/// <item>a real P100 health change (the direct-hit
/// <see cref="Probe.ReceiveDamage"/> path, the same entry point combat
/// collisions dispatch to) is reflected in the <b>next</b> <c>PROBES_UPDATE</c>:
/// the <c>bpFraction</c> byte drops from 240 to the div-240 encoding of the
/// probe's new <see cref="Probe.Fraction"/>.</item>
/// </list>
/// Construction mirrors P133/P134's 3-bot / 2-side harness: side A's deployer
/// at the origin lays the probe (the demo map has zero probes, so it is the
/// only probe in the universe), side B's scanner-only ship 30 km away (never in
/// detection range of it), and a side-A teammate nearby (which must also see it
/// — the per-side fan-out to every connection of the owning side). No Windows
/// VM, no native client.
/// </summary>
[Collection("LiveNetwork")]
public sealed class ProbesUpdateLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Probes Update Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 991u, DwCookie = 0x991u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 99000000000000000ul, DrmHash = "probesupdate",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    private static byte Percent(float f) => (byte)(f * 240.0f + 0.5);   // div-240 BytePercentage (Utility.h:1307-1312)

    /// <summary>A real probe type the deployer will lay — one whose decoded
    /// <c>Lifespan</c> is comfortably longer than the few dozen sim-ticks these
    /// tests pump, so the probe never expires mid-assertion.</summary>
    private static DataProbeTypeIGC RealProbeType(StaticCore core)
    {
        var t = core.ProbeTypes.Values
            .Where(pt => pt.Lifespan >= 2.0f && pt.Lifespan < 12.0f)
            .OrderBy(pt => pt.Lifespan)
            .FirstOrDefault();
        if (t.ExpendableTypeId == 0 && core.ProbeTypes.Count > 0)
            throw new InvalidOperationException("representative core has no probe with 2<=lifespan<12");
        return t;
    }

    /// <summary>A booted 3-bot / 2-side live server plus the resolved roles.
    /// Slot 0 = <b>side A</b> DEPLOYER at the origin (lays the probe); slot 1 =
    /// <b>side B</b>, 30 km away on +Z (its only scanner — never in detection
    /// range of the probe); slot 2 = <b>side A</b> teammate near the origin
    /// (same side, must also see the probe). A scanner hull (largest
    /// <c>ScannerRange</c>) is used so detection is robust, though the probe's
    /// own side never needs a scanner anyway — the same-side rule suffices.</summary>
    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Deployer = null!, CrossSide = null!, Teammate = null!;
        public FltBot DeployerBot = null!, CrossSideBot = null!, TeammateBot = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            foreach (var b in new[] { DeployerBot, CrossSideBot, TeammateBot }) b?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated SeatThree()
    {
        var seat = new Seated();
        seat.Server = new Srv();

        Vector3 apos = Vector3.Zero;
        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.ScannerRange).First();
            var ship = placed switch
            {
                0 => new Ship(id, hull, apos, Vector3.Zero, Orientation.Identity),                                        // side A deployer at origin
                1 => new Ship(id, hull, new Vector3(0f, 0f, 100000f), Vector3.Zero, Orientation.Identity),   // side B, 100 km away (well past any probe's detection radius)
                _ => new Ship(id, hull, new Vector3(200f, 0f, 100f), Vector3.Zero, Orientation.Identity),                 // side A teammate near origin
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A,B,A
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;  // P180: test assumes an immediately-flyable mission; bypass ready-up
        var port = seat.Server.BoundPort;

        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p135_a", "p135_b", "p135_team" };
        try
        {
            for (int i = 0; i < bots.Length; i++) { bots[i].Connect(port, names[i]); bots[i].SendLogonRequest(Request(names[i])); }
            PumpUntil(seat.Server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            foreach (var bot in bots)
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                if (ship.Position.Length() < 1f) { seat.Deployer = ship; seat.DeployerBot = bot; }
                else if (ship.Position.Z > 1000f) { seat.CrossSide = ship; seat.CrossSideBot = bot; }
                else { seat.Teammate = ship; seat.TeammateBot = bot; }
            }
            Assert.NotNull(seat.Deployer); Assert.NotNull(seat.CrossSide); Assert.NotNull(seat.Teammate);
            Assert.True(seat.Deployer.SideId != seat.CrossSide.SideId, "cross-side peer must be on the other side from the deployer");
            Assert.True(seat.Deployer.SideId == seat.Teammate.SideId, "teammate must be same-side as the deployer");
            seat.Deployer.Velocity = Vector3.Zero;
            seat.Teammate.Velocity = Vector3.Zero;
            seat.CrossSide.Velocity = Vector3.Zero;
            return seat;
        }
        catch
        {
            foreach (var b in bots) b.Dispose();
            seat.Server.Dispose();
            throw;
        }
    }

    private static ProbesUpdateEntry EntryOf(ProbesUpdate up, short probeId)
        => up.Entries.FirstOrDefault(e => e.ProbeId == probeId);

    private static bool HasEntry(ProbesUpdate up, short probeId)
        => up.Entries.Any(e => e.ProbeId == probeId);

    /// <summary>⭐ A probe visible to a side is delivered in that side's
    /// <c>PROBES_UPDATE</c> with the correct id and div-240 health fraction; the
    /// SAME probe is absent from a different side's updates (per-side batching
    /// — no whole-mission leak). Because it is the deployer's own side, the
    /// probe needs <b>no scan</b> — the same-side rule includes it always, and
    /// both same-side connections (deployer + teammate) receive it. B (30 km
    /// away, sees nothing) receives no <c>PROBES_UPDATE</c> at all.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Visible_probe_received_correct_fields_absent_from_unseeing_side()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var deployer = seat.Deployer;
        var mission = deployer.Mission!;
        var pType = RealProbeType(mission.Core);
        var probe = deployer.DeployProbe(pType, mission.LastUpdate);
        short pId = probe.Id;

        // The probe is the deployer's own side's, so it is visible to side A by
        // the same-side rule regardless of any scanner — the no-scan-needed case.
        // The sweep populates SeenBySide on the next tick (DeployProbe does not
        // run it synchronously), so pump briefly first.
        PumpUntil(seat.Server, () => probe.SeenBySide(deployer.SideId!.Value), 200);
        Assert.True(probe.SeenBySide(deployer.SideId!.Value), "own-side probe must be visible to its own side (same-side rule)");
        Assert.False(probe.SeenBySide(seat.CrossSide.SideId!.Value), "the distant side B must NOT see the origin probe");

        // Start counting afresh so every accumulated update below is post-deploy.
        seat.DeployerBot.ResetProbesUpdates();
        seat.TeammateBot.ResetProbesUpdates();
        seat.CrossSideBot.ResetProbesUpdates();

        // Side A sees exactly one probe (ours), so its periodic update flows
        // every tick; pump until the deployer has at least one.
        PumpUntil(seat.Server, () => seat.DeployerBot.ProbesUpdates.Count > 0, 2000);
        Assert.True(seat.DeployerBot.ProbesUpdates.Count > 0, "side A must receive PROBES_UPDATE (it owns the probe)");

        var aUp = seat.DeployerBot.ProbesUpdates[0];
        Assert.True(HasEntry(aUp, pId),
            $"A's update must contain the probe {pId}; got {string.Join(",", aUp.Entries.Select(e => e.ProbeId))}");
        var e = EntryOf(aUp, pId);
        Assert.Equal((byte)240, e.BpFraction);          // undamaged probe -> full health fraction

        // The same-side teammate's connection also receives it (per-side fan-out
        // to every connection of the owning side).
        PumpUntil(seat.Server, () => seat.TeammateBot.ProbesUpdates.Count > 0, 2000);
        Assert.True(seat.TeammateBot.ProbesUpdates.Count > 0, "the same-side teammate must also receive PROBES_UPDATE");
        Assert.True(HasEntry(seat.TeammateBot.ProbesUpdates[0], pId),
            "the teammate's update must contain the same probe");

        // B's side never sees the probe (its only scanner is 30 km away), and the
        // demo map has no other probes, so B receives NO PROBES_UPDATE at all.
        PumpUntil(seat.Server, () => seat.CrossSideBot.ProbesUpdates.Count > 0, 500);
        Assert.True(seat.CrossSideBot.ProbesUpdates.Count == 0,
            "side B must receive NO PROBES_UPDATE (it sees nothing)");

        Console.WriteLine($"P135-VISIBLE: probe {pId} (bp {e.BpFraction}/240, own-side no-scan) " +
                          $"-> A's PROBES_UPDATE(92) to {2} same-side peers; B gets none [PER SIDE]");
    }

    /// <summary>⭐ A real P100 health change (the direct-hit
    /// <see cref="Probe.ReceiveDamage"/> path — the same entry point combat
    /// collisions dispatch to, Probe.cs:256-289) is reflected in the NEXT
    /// <c>PROBES_UPDATE</c>: the <c>bpFraction</c> byte drops from the full 240
    /// to the div-240 encoding of the probe's new <see cref="Probe.Fraction"/>,
    /// proving the periodic sync carries live health (fsship.cpp:877
    /// bpFraction = GetFraction()).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Combat_damage_reflected_in_next_update()
    {
        Require.Artwork();
        using var seat = SeatThree();
        var deployer = seat.Deployer;
        var mission = deployer.Mission!;
        var pType = RealProbeType(mission.Core);
        var probe = deployer.DeployProbe(pType, mission.LastUpdate);
        short pId = probe.Id;

        seat.DeployerBot.ResetProbesUpdates();
        // Capture the undamaged state first: at least one full-fraction update.
        PumpUntil(seat.Server, () => seat.DeployerBot.ProbesUpdates.Count > 0, 2000);
        Assert.True(seat.DeployerBot.ProbesUpdates.Count > 0, "side A must receive PROBES_UPDATE before damage");
        int baseCount = seat.DeployerBot.ProbesUpdates.Count;
        Assert.Equal((byte)240, EntryOf(seat.DeployerBot.ProbesUpdates[^1], pId).BpFraction);

        // Real P100 damage: a moderate non-lethal direct hit (same ReceiveDamage
        // entry point the collision pass dispatches to). Kept well above 0 so the
        // probe survives and stays in its sector (a kill would remove it and the
        // next update could legitimately drop it).
        float maxHP = probe.ProbeType.HitPoints;
        probe.ReceiveDamage(
            type: 0,
            amount: 0.25f * maxHP,
            now: mission.LastUpdate,
            position1: deployer.Position,
            position2: probe.Position,
            launcher: deployer);
        Assert.True(!probe.Dead, "the test damage must be non-lethal so the probe survives");
        float newFraction = Math.Clamp(probe.Fraction, 0f, 1f);

        // The next tick's sweep + broadcast carries the new health.
        PumpUntil(seat.Server, () => seat.DeployerBot.ProbesUpdates.Count > baseCount, 2000);
        Assert.True(seat.DeployerBot.ProbesUpdates.Count > baseCount,
            "a subsequent PROBES_UPDATE must arrive after the damage");
        var up = seat.DeployerBot.ProbesUpdates[^1];
        Assert.True(HasEntry(up, pId), "the damaged probe must still appear in A's update (it survived)");
        var e = EntryOf(up, pId);
        Assert.Equal(Percent(newFraction), e.BpFraction);     // div-240 encoding of the live fraction
        Assert.True(e.BpFraction < 240, "the damaged probe's fraction must be strictly below full");

        Console.WriteLine($"P135-DMG: probe {pId} bp 240->{e.BpFraction}/240 as real P100 damage lands " +
                          $"(Fraction {1f:F3}->{probe.Fraction:F3}); carried in the next PROBES_UPDATE(92)");
    }

    /// <summary>⭐ Deterministic codec round-trip: <c>PROBES_UPDATE</c>(92) with
    /// several <see cref="ProbesUpdateEntry"/>s survives Encode/Decode with the
    /// exact wire layout (fmid 92, one ib/cb var pair, count implicit in the
    /// blob length over the 3-byte pack(1) element stride — the "packed to 3
    /// bytes" the native comment at Messages.h:635 demands).</summary>
    [Fact]
    public void ProbesUpdate_codec_round_trips_multiple_entries()
    {
        var msg = new ProbesUpdate
        {
            Entries =
            [
                new ProbesUpdateEntry((short)6001, (byte)240),
                new ProbesUpdateEntry((short)6012, (byte)137),
                new ProbesUpdateEntry((short)6023, (byte)96),
            ],
        };

        byte[] wire = msg.Encode();
        Assert.Equal(8 + 3 * 3, wire.Length);          // FixedSize 8 + 3 entries × 3
        Assert.Equal((ushort)92, BitConverter.ToUInt16(wire, 2));   // fmid
        Assert.Equal((ushort)8, BitConverter.ToUInt16(wire, 4));    // ib = blob at the fixed-region end
        Assert.Equal((ushort)9, BitConverter.ToUInt16(wire, 6));    // cb = 3 × 3
        Assert.Equal((ushort)wire.Length, BitConverter.ToUInt16(wire, 0));   // cbmsg

        var dec = ProbesUpdate.Decode(wire);
        Assert.Equal(3, dec.Entries.Length);
        Assert.Equal(((short)6001, (byte)240), (dec.Entries[0].ProbeId, dec.Entries[0].BpFraction));
        Assert.Equal(((short)6012, (byte)137), (dec.Entries[1].ProbeId, dec.Entries[1].BpFraction));
        Assert.Equal(((short)6023, (byte)96), (dec.Entries[2].ProbeId, dec.Entries[2].BpFraction));

        Console.WriteLine($"P135-CODEC: PROBES_UPDATE(92) {msg.Entries.Length} entries, wire {wire.Length} B, stride 3, round-tripped");
    }
}

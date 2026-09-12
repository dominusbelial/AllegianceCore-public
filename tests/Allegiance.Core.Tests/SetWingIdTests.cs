using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P146 — the <c>FM_CS_SET_WINGID(106)</c> loopback proof. Three real bots over
/// real loopback TCP (3 bots / 2 teams round-robin ⇒ a guaranteed same-side
/// pair and a cross-side enemy, all spawned in the same starter sector — the
/// same construction as <see cref="DropPartTests"/>). The subject tags its own
/// ship with a wing id; asserts (a) every <b>same-side</b> peer — the same-side
/// observer <em>and</em> the subject's own ship — receives <c>SET_WINGID</c>
/// with the corrected <c>shipID</c>/<c>wingID</c>/<c>bCommanded=false</c>, (b)
/// the <b>cross-side</b> enemy flying in the <em>same sector</em> does
/// <b>not</b> receive it — proving the side-wide
/// (<c>CFSSide::FromIGC(pside)-&gt;GetGroup()</c>, FedSrv.CPP:5337), not
/// sector-wide (<c>GetGroupSectorFlying</c>), audience — and (c) the ship's
/// server-side <c>WingId</c> actually changed. Also exercises the
/// self-targeting-only rejection: a player trying to set a <em>different</em>
/// ship's wing is silently ignored (no broadcast, no state change).
/// </summary>
[Collection("LiveNetwork")]
public class SetWingIdTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Set Wing Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 424u, DwCookie = 0x2Au,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 34900000000000000ul, DrmHash = "setwingidtest",
    };

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>Pin the wire layout: fixed 10 B (header 4 + shipID short @4 +
    /// wingID short @6 + bCommanded bool @8 + 1 B align pad), fmid 106, and a
    /// lossless round-trip of all three fields. <c>WingID</c> is a <c>short</c>
    /// (a <c>typedef ObjectID</c> alias, <c>igc.h:477,504</c>), NOT a byte.</summary>
    [Fact]
    public void SetWingId_codec_roundtrips_fixed_10_byte_layout()
    {
        var msg = new SetWingId
        {
            ShipID = 5678,
            WingId = 5,
            BCommanded = true,
        };

        var encoded = msg.Encode();
        Assert.Equal(10, encoded.Length);
        Assert.Equal((ushort)10, FedMessageHeader.Read(encoded).CbMsg);
        Assert.Equal(SetWingId.Fmid, FedMessageHeader.Read(encoded).Fmid);

        var again = SetWingId.Decode(encoded);
        Assert.Equal((short)5678, again.ShipID);
        Assert.Equal((short)5, again.WingId);
        Assert.True(again.BCommanded);

        // Native structural layout (Messages.h:705-708): bytes @4/5 shipID LE,
        // @6/7 wingID LE, @8 bCommanded, @9 pad. (5678 = 0x162E; wingID 5.)
        byte[] native = [0x0A, 0x00, 0x6A, 0x00, 0x2E, 0x16, 0x05, 0x00, 0x01, 0x00];
        var nat = SetWingId.Decode(native);
        Assert.Equal((short)5678, nat.ShipID);
        Assert.Equal((short)5, nat.WingId);
        Assert.True(nat.BCommanded);
    }

    /// <summary>The <c>c_widMax = 10</c> bound is a codec-level constant
    /// (igc.h:505), matching the ten named wings (common.cpp:23-35).</summary>
    [Fact]
    public void SetWingId_widmax_is_ten()
    {
        Assert.Equal((short)10, SetWingId.WidMax);
    }

    [Fact]
    public void SetWingId_self_tag_relays_sidewide_not_sectorwide_and_mutates()
    {
        using var server = new Srv();
        server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot(), new FltBot() };
        string[] names = { "p146_subject", "p146_observer", "p146_enemy" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "all three bots must be assigned a ship");

            var ships = server.Ships.ToDictionary(s => s.Ship.Id, s => s.Ship.SideId);
            short[] ids = bots.Select(b => b.ShipAssignment!.Ship.ShipId).ToArray();

            int subject = -1, observer = -1, enemy = -1;
            for (int i = 0; i < bots.Length && observer < 0; i++)
                for (int j = 0; j < bots.Length; j++)
                    if (j != i && ships[ids[j]] == ships[ids[i]]) { subject = i; observer = j; break; }
            Assert.True(subject >= 0, "round-robin must yield a same-side pair");
            for (int k = 0; k < bots.Length; k++)
                if (k != subject && k != observer) { enemy = k; break; }
            Assert.True(enemy >= 0 && ships[ids[enemy]] != ships[ids[subject]],
                "3 bots / 2 teams must leave one bot cross-side");

            short subjectId = ids[subject];
            var subBot = bots[subject];
            var obsBot = bots[observer];
            var eneBot = bots[enemy];

            // All three are in the SAME starter sector — so an absence at the
            // cross-side enemy isolates the side-wide (not sector-wide) audience.
            var subjectSector = server.Ships.Single(s => s.Ship.Id == subjectId).Ship.Sector!.Id;
            Assert.Equal(subjectSector, server.Ships.Single(s => s.Ship.Id == ids[enemy]).Ship.Sector!.Id);
            Assert.Equal(subjectSector, server.Ships.Single(s => s.Ship.Id == ids[observer]).Ship.Sector!.Id);

            Ship Subj() => server.Ships.Single(s => s.Ship.Id == subjectId).Ship;

            // Fresh default: a ship's WingId starts at 0 (this port's applied
            // default; native constructs at NA=-1, shipIGC.cpp:47).
            Assert.Equal((short)0, Subj().WingId);

            foreach (var b in bots) b.ResetSetWingIdsReceived();

            // Tag the subject's OWN ship with wing id 5 ("bravo", common.cpp:29).
            subBot.SendSetWingId(subjectId, wingId: 5);

            // (a)+(c) Both same-side peers — the observer and the subject's own
            // ship — receive the corrected SET_WINGID.
            PumpUntil(server,
                () => subBot.SetWingIds.Count >= 1 && obsBot.SetWingIds.Count >= 1
                   && subBot.SetWingIds.Any(w => w.ShipID == subjectId && w.WingId == 5 && !w.BCommanded)
                   && obsBot.SetWingIds.Any(w => w.ShipID == subjectId && w.WingId == 5 && !w.BCommanded),
                4000);
            Assert.Contains(subBot.SetWingIds, w => w.ShipID == subjectId && w.WingId == 5 && !w.BCommanded);
            Assert.Contains(obsBot.SetWingIds, w => w.ShipID == subjectId && w.WingId == 5 && !w.BCommanded);
            // bCommanded is server-corrected to false for a self-assignment.
            Assert.DoesNotContain(subBot.SetWingIds, w => w.BCommanded);
            Assert.DoesNotContain(obsBot.SetWingIds, w => w.BCommanded);

            // (b) The cross-side enemy — flying in the same sector — does NOT
            // receive it: the audience is side-wide, not sector-wide (this is
            // the audience-distinction that separates SET_WINGID from
            // SWAP_PART/DROP_PART/PROMOTE, which are sector-wide).
            PumpUntil(server, () => false, 20);   // let any wrongful arrival surface
            Assert.Empty(eneBot.SetWingIds);

            // (c) Server-side state: the target ship's WingId actually changed.
            Assert.Equal((short)5, Subj().WingId);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }

    /// <summary>The self-targeting-only rejection: attempting to set a
    /// DIFFERENT ship's wing (not the sender's own) is silently ignored — no
    /// broadcast to anyone, no server-side state change (the leader-retag branch,
    /// FedSrv.CPP:5323-5330, is unreachable without a team-leader system).</summary>
    [Fact]
    public void SetWingId_foreign_ship_target_is_silently_ignored()
    {
        using var server = new Srv();
        server.Boot(Config());
        var bots = new[] { new FltBot(), new FltBot() };
        string[] names = { "p146_attacker", "p146_victim" };
        try
        {
            for (int i = 0; i < bots.Length; i++)
            {
                bots[i].Connect(server.BoundPort, names[i]);
                bots[i].SendLogonRequest(Request(names[i]));
            }
            PumpUntil(server, () => bots.All(b => b.ShipAssignment is not null), 4000);
            Assert.True(bots.All(b => b.ShipAssignment is not null), "both bots must be assigned a ship");

            short attackerId = bots[0].ShipAssignment!.Ship.ShipId;
            short victimId = bots[1].ShipAssignment!.Ship.ShipId;

            Ship Victim() => server.Ships.Single(s => s.Ship.Id == victimId).Ship;

            Assert.Equal((short)0, Victim().WingId);
            foreach (var b in bots) b.ResetSetWingIdsReceived();

            // The attacker tries to tag the VICTIM's ship (a different ship).
            bots[0].SendSetWingId(victimId, wingId: 7);

            PumpUntil(server, () => false, 20);   // let any wrongful relay arrive
            Assert.Empty(bots[0].SetWingIds);
            Assert.Empty(bots[1].SetWingIds);
            // No state change on the would-be target (nor on the sender).
            Assert.Equal((short)0, Victim().WingId);
            Assert.Equal((short)0, server.Ships.Single(s => s.Ship.Id == attackerId).Ship.WingId);
        }
        finally
        {
            foreach (var b in bots) b.Dispose();
        }
    }
}

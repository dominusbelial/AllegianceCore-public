using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Xunit;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// The P61 loopback proof: a real in-process <see cref="Srv"/> over loopback
/// TCP, a real <see cref="ClientHarness"/> bot, and a real
/// <c>FM_C_BUY_LOADOUT(4)</c> → <c>FM_S_BUY_LOADOUT_ACK(5)</c> round-trip that
/// (1) names an actual hull and an actual weapon part id pulled from a loaded
/// <c>StaticCore</c>, and (2) asserts the ACK's <c>ExpandedPartData[]</c> tail
/// (<c>nParts &gt;= 1</c>) round-trips the requested part byte-for-byte. It also
/// pins the P26/P27/P28 zero-part "adopt a hull" path: a hull-only request still
/// elicits a hull-only ACK (<c>nParts = 0</c>), bytes identical to the logon
/// burst's own hull-only ACK.
///
/// Serialized with the other live classes via <c>[Collection("LiveNetwork")]</c>
/// (the same LiveCombatWireTests race rationale).
/// </summary>
[Collection("LiveNetwork")]
public class LoadoutRoundTripTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static StaticCore Core() =>
        StaticCore.Load(IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc")));

    /// <summary>The first real weapon part id in the core (any Weapon subtype),
    /// pulled from the loaded static core exactly the way the server's own part
    /// resolution does (StaticCore.PartTypes keyed by PartId).</summary>
    private static (short partId, DataPartTypeIGC.Weapon w) RealWeapon()
    {
        var core = Core();
        foreach (var p in core.PartTypes.Values
                     .Where(p => p.EquipmentType == EquipmentType.Weapon)
                     .OrderBy(p => p.PartId))
            return (p.PartId, (DataPartTypeIGC.Weapon)p.Type);
        throw new InvalidOperationException("no weapon in representative core");
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm
            {
                Name = "Test Mission", Map = "XXV_simple.igc", Teams = 2,
                CivIds = [1, 3], SideNames = ["Alpha", "Bravo"],
            },
        ],
    };

    private static LogonRequest SampleRequest(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 912u, DwCookie = 0xBEEFu,
        CrcFileList = -1, SteamAuthTicket = new byte[16], SteamAuthTicketLength = 0,
        SteamID = 75000000000000000ul, DrmHash = "loadout",
    };

    /// <summary>The requested weapon on a real mount, with a real amount and a
    /// fully-mounted fraction — the tail we expect the ACK to echo back.</summary>
    private static ExpandedPartData RequestedWeapon()
    {
        var (partId, _) = RealWeapon();
        return new ExpandedPartData(partId, Amount: 20, MountId: 0, FractionMounted: ExpandedPartData.FullyMounted);
    }

    private static Task<Srv> BootWithLoop(out CancellationTokenSource cts, out Task loop)
    {
        var server = new Srv();
        server.Boot(Config(port: 0));
        var tokenSrc = new CancellationTokenSource();
        cts = tokenSrc;
        loop = Task.Run(() => server.Loop(tokenSrc.Token));
        return Task.FromResult(server);
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task BuyLoadout_WithRealHullAndWeapon_RoundTrips()
    {
        Require.Artwork();
        using var server = await BootWithLoop(out var cts, out var loop);
        using var client = new ClientHarness();
        try
        {
            client.Join(server.BoundPort, "LOADOUT-RT");
            var ack = client.RequestLogon(SampleRequest("LOADOUT-RT"));
            Assert.True(ack.FValidated, $"logon failed: {ack.CharNameOrFailureReason}");
            client.RequestSide(0);   // P281 — pick a side to trigger the deferred launch burst

            // The logon burst's own BUY_LOADOUT_ACK is hull-only (nParts=0) and
            // names the ship's real current hull — grab that hull for our request.
            var logonAckRaw = client.WaitForFmid(BuyLoadoutAck.Fmid, 5000);
            var logonAck = BuyLoadoutAck.Decode(logonAckRaw);
            Assert.Empty(logonAck.Parts);            // zero-part regression: logon-burst ACK has no tail
            short hullId = logonAck.HullId;
            Assert.True(hullId > 0, $"expected a real hull, got {hullId}");

            // Drop the rest of the burst, then send a real per-part request.
            client.DrainAll();
            var requested = RequestedWeapon();
            // H-07 / shipIGC.h:1029: explicitly sell the chosen real weapon
            // and verify its hardpoint, rather than relying on first-record order.
            var mission = server.Missions.Single();
            Assert.True(mission.Core.HullTypes[hullId].CanMountPart(mission.Core.PartTypes[requested.PartId], 0));
            EconomyLoadoutTestSetup.Sell(mission, mission.Core.PartTypes[requested.PartId]);
            var req = new BuyLoadoutRequest { HullId = hullId, FLaunch = false, Parts = [requested] };
            client.Send(FedPacket.Pack(new[] { req.Encode() }));

            var respRaw = client.WaitForFmid(BuyLoadoutAck.Fmid, 5000);
            var resp = BuyLoadoutAck.Decode(respRaw);
            Assert.Equal(hullId, resp.HullId);
            Assert.True(resp.FBoughtEverything, "every requested part was granted -> fBoughtEverything true");
            Assert.False(resp.FLaunch);
            Assert.True(resp.Parts.Count >= 1, $"expected nParts>=1, got {resp.Parts.Count}");
            Assert.Equal(requested, resp.Parts[0]);   // byte-for-byte round-trip of the requested part

            // The ACK is stable: re-encode → re-decode reproduces the same fields.
            var again = BuyLoadoutAck.Decode(resp.Encode());
            Assert.Equal(resp.HullId, again.HullId);
            Assert.Equal(resp.Parts.Count, again.Parts.Count);
            Assert.Equal(requested, again.Parts[0]);
        }
        finally
        {
            client.Shutdown();
            cts.Cancel();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            server.Shutdown();
        }
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public async Task BuyLoadout_ZeroParts_StillReturnsHullOnlyAck()
    {
        Require.Artwork();
        using var server = await BootWithLoop(out var cts, out var loop);
        using var client = new ClientHarness();
        try
        {
            client.Join(server.BoundPort, "LOADOUT-ZERO");
            var ack = client.RequestLogon(SampleRequest("LOADOUT-ZERO"));
            Assert.True(ack.FValidated);
            client.RequestSide(0);   // P281 — pick a side to trigger the deferred launch burst

            var logonAck = BuyLoadoutAck.Decode(client.WaitForFmid(BuyLoadoutAck.Fmid, 5000));
            short hullId = logonAck.HullId;
            client.DrainAll();

            // Hull-only request: must elicit a hull-only ACK (P26/P27/P28 path).
            client.Send(FedPacket.Pack(new[] { new BuyLoadoutRequest { HullId = hullId, FLaunch = false, Parts = [] }.Encode() }));
            var respRaw = client.WaitForFmid(BuyLoadoutAck.Fmid, 5000);
            var resp = BuyLoadoutAck.Decode(respRaw);
            Assert.Equal(hullId, resp.HullId);
            Assert.Empty(resp.Parts);                // nParts = 0, still
            Assert.False(resp.FBoughtEverything);    // honest: nothing bought
            Assert.False(resp.FLaunch);

            // Byte-identical to the logon burst's hull-only ACK form.
            Assert.Equal(new BuyLoadoutAck { HullId = hullId }.Encode(), respRaw);
        }
        finally
        {
            client.Shutdown();
            cts.Cancel();
            try { await loop.WaitAsync(TimeSpan.FromSeconds(2)); } catch (TimeoutException) { }
            server.Shutdown();
        }
    }
}

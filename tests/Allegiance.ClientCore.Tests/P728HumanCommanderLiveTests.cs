using System.Diagnostics;
using System.Text.Json;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.SinglePlayer;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit.Abstractions;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
[Trait("Tier", "Live")]
public class P728HumanCommanderLiveTests(ITestOutputHelper output)
{
    [SkippableFact]
    public async Task Human_research_spends_wallet_unlocks_bot_purchase_obeys_order_and_survives_next_match()
    {
        Require.Artwork();
        using var host = await SkirmishHost.StartAsync(new() { Commander = SkirmishCommander.You, PlayerCiv = 18 });
        string? proof = Environment.GetEnvironmentVariable("ALLEGIANCE_P728_PROOF_DIRECTORY");
        string? progress = proof is null ? null : Path.Combine(proof, "human-" + Guid.NewGuid().ToString("N") + ".jsonl");
        void Trace(string message)
        {
            output.WriteLine(message);
            if (progress is not null) File.AppendAllText(progress, JsonSerializer.Serialize(new { utc = DateTime.UtcNow, message, sim = host.SimulationSeconds }) + Environment.NewLine);
        }
        Trace("host logs: " + host.DirectoryPath);
        using var pilot = new ConnectionSession();
        const string name = "skirmish-test-pilot";
        pilot.Connect(host.Port, name);
        pilot.SendLogonRequest(new() { CharacterName = name, FedsrvVer = LogonRequest.MsgVer,
            ZoneTicket = [1, 2, 3, 4], CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", CrcFileList = -1, SteamAuthTicket = new byte[16] });
        pilot.WaitForAck(10000);
        pilot.SendPositionReq(0);
        (long Generation, short Ship) seat = (-1, -1);
        async Task Until(Func<bool> done, string reason, int seconds = 120)
        {
            var timer = Stopwatch.StartNew();
            while (timer.Elapsed.TotalSeconds < seconds)
            {
                lock (pilot.Gate)
                {
                    if (pilot.World.MyShipId is { } id && seat != (pilot.World.MissionGeneration, id))
                    {
                        pilot.SendAutoDonate(-1);
                        pilot.SendPlayerReady(true, id);
                        seat = (pilot.World.MissionGeneration, id);
                    }
                    if (!pilot.World.IsTeamLeader && pilot.World.MyShipId is { } self && pilot.World.AutoDonateTargetByShipId.ContainsKey(self)) pilot.SendAutoDonate(-1);
                    pilot.SendPing();
                }
                if (done()) { Trace("observed: " + reason); return; }
                Assert.True(host.BotsAlive, host.Failure);
                await Task.Delay(100);
            }
            output.WriteLine(host.Stats.GetRawText());
            Assert.Fail(reason);
        }
        await Until(() => { lock (pilot.Gate) return pilot.World.IsTeamLeader && pilot.World.Mission?.Stage == MissionStageMessage.StageStarted; }, "Human did not acquire native donation-vote leadership");
        short me = seat.Ship;
        Assert.True(host.OnSimulation(s => s.Missions[0].Sides[0].LeaderShipId == me));
        Assert.False(host.Options.CreateConfig().LiveSettings.TestHarness!.IgnoreTechGate);
        await Until(() => host.Stats.ValueKind == JsonValueKind.Object && host.Stats.GetProperty("workers").EnumerateArray()
            .Any(w => w.GetProperty("report").TryGetProperty("research", out var research) && research.GetArrayLength() == 1
                && research[0].GetProperty("Side").GetInt32() == 1), "Enemy research commander absent");
        lock (pilot.Gate) Assert.DoesNotContain(pilot.World.Ships.Values, s => s.CharacterName.StartsWith("research-cmd0-"));
        var core = Catalog.RepresentativeCore();
        var choice = host.OnSimulation(s =>
        {
            var m = s.Missions[0]; var side = m.Sides[0];
            var dock = m.Sectors.SelectMany(c => c.StationRuntimes).First(st => st.SideId == 0).Type;
            foreach (var b in side.Buckets.Where(b => b.Development is not null && side.CanBuy(b)).OrderBy(b => b.Price))
            {
                var techs = side.Techs.Or(TechTreeBitMask.FromBytes(b.Development!.Value.TtbmEffects));
                foreach (var role in new[] { Showcase.HullRole.Fighter, Showcase.HullRole.Interceptor, Showcase.HullRole.Scout, Showcase.HullRole.Bomber })
                {
                    var selected = TechLoadoutSelector.Select(core, 18, role, techs, dock.StationTypeId);
                    if (selected is null || TechTreeBitMask.RequiredTechsBuyable(core.HullTypes[selected.HullId].TtbmRequired, dock.TtbmLocal, side.Techs)) continue;
                    return (Bucket: b, Hull: selected.HullId, Cost: selected.Price, Role: role);
                }
            }
            throw new InvalidOperationException("No single-development hull unlock available at the starting station");
        });
        var bucket = choice.Bucket;
        output.WriteLine($"human development={bucket.Development!.Value.DevelopmentId} '{bucket.Name}' ${bucket.Price} {bucket.TimeToBuild}s unlocks {choice.Role} hull={choice.Hull} cost={choice.Cost}");
        var defs = BucketCatalog.Describe(core, pilot.World.InitialTechsFor(0)!.Value, GlobalAttributeSet.FromArray(core.Civilizations[18].GasBaseAttributes))
            .Select(b => new InvestmentBucket(b.Id, b.ItemType, b.Name, b.Price, b.TimeToBuildSec, b.HullIdDrone, b.DroneTypeId, b.DevelopmentId, b.StationTypeId)).ToArray();
        var target = defs.Single(b => b.Id == bucket.Id);
        await Until(() => { lock (pilot.Gate) return pilot.World.MyMoney >= bucket.Price; }, "Native income did not fund the human wallet", 480);
        int before;
        lock (pilot.Gate)
        {
            before = pilot.World.MyMoney!.Value;
            var model = new InvestmentPaneModel([target], pilot.World.BucketFundingFor(0), true, true, before,
                canBuy: b => InvestmentTechGates.CanBuy(core, b, pilot.World.DevelopmentTechsFor(0)!.Value,
                    pilot.World.Stations.Values.Where(st => st.SideId == 0).Select(st => core.StationTypes[st.StationTypeId]).ToArray()));
            Assert.True(model.TryInvest(out short id, out int amount));
            Assert.Equal(bucket.Price, amount);
            pilot.SendBucketDonate(id, amount);
            Assert.Equal(before, pilot.World.MyMoney); // no speculative local debit
        }
        await Until(() => host.OnSimulation(s => s.Missions[0].Sides[0].Buckets.Single(b => b.Id == bucket.Id).InvestedMoney == bucket.Price), "Donation did not reach authoritative bucket");
        await Until(() => { lock (pilot.Gate) return pilot.World.CompletedDevelopments.Contains(bucket.Development!.Value.DevelopmentId); }, "Development completion did not reach the human", 240);
        short[] botIds;
        lock (pilot.Gate) botIds = pilot.World.Ships.Values.Where(s => s.SideId == 0 && s.CharacterName.StartsWith("mcp-") && !s.CharacterName.StartsWith("mcp-probe"))
            .Select(s => s.ShipId).ToArray();
        Assert.Equal(4, botIds.Length);
        await Until(() => { lock (pilot.Gate) return pilot.World.MyMoney >= choice.Cost * botIds.Length; }, "Native income did not cover fleet purchases", 240);
        foreach (short id in botIds)
            pilot.SendPlayerDonation(new PlayerDonate { ShipID = id, MoneyGiven = checked((int)choice.Cost) });
        await Until(() => host.OnSimulation(s => botIds.All(id => s.Ships.Single(l => l.Ship.Id == id).Wallet.Money >= choice.Cost)), "Pilot allocations were not acknowledged");
        // Exercise the normal lethal-damage -> pod -> restart -> paced purchase
        // path. No hull/tech/wallet injection or BUY_LOADOUT acknowledgement shortcut.
        host.OnSimulation(s =>
        {
            foreach (short id in botIds)
            {
                var ship = s.Ships.Single(l => l.Ship.Id == id).Ship;
                ship.ReceiveDamage(0, 1_000_000, s.SimNow, ship.Position, ship.Position, null);
                ship.ReceiveDamage(0, 1_000_000, s.SimNow, ship.Position, ship.Position, null);
            }
            return true;
        });
        await Until(() => host.OnSimulation(s => botIds.Any(id => s.Ships.Any(l => l.Ship.Id == id && l.Ship.HullType.HullId == choice.Hull && l.Ship.StationId is null))), "Bots did not buy the human-unlocked hull", 120);
        output.WriteLine("unlocked hull observed on live bot: " + choice.Hull);
        // The same CHAT payload as the commander UI: named recipient + enemy ship.
        short recipient = host.OnSimulation(s => botIds.First(id => s.Ships.Any(l => l.Ship.Id == id
            && l.Ship.HullType.HullId == choice.Hull && l.Ship.StationId is null)));
        // A durable, unarmed stock constructor is a deterministic order target.
        // Existing enemy pilots may have become lifepods during the research wait.
        // This adds only the attack workload; all research and purchases above
        // used the ordinary match economy and legal ship catalog.
        short enemyId = host.OnSimulation(s =>
        {
            var recipientShip = s.Ships.Single(l => l.Ship.Id == recipient).Ship;
            var targetType = core.DroneTypes.Values.Where(d => d.PilotType == Ship.PilotTypeBuilder)
                .OrderByDescending(d => core.HullTypes[d.HullTypeId].HitPoints).First();
            return s.SpawnNpcShip(s.Missions[0], recipientShip.Sector!, 1,
                recipientShip.Position + new Allegiance.Core.Collision.Vector3(1000, 0, 0), targetType.DroneTypeId).Id;
        });
        pilot.SendChat("", ChatTarget.Individual, recipient, commandId: OrderChange.CidAttack, otTarget: (short)IgcObjectType.Ship, oidTarget: enemyId);
        await Until(() => host.OnSimulation(s => s.Ships.Single(l => l.Ship.Id == recipient).Ship.AcceptedShipTarget?.Id == enemyId), "Bot did not accept the human's command");
        await Until(() => host.Stats.GetProperty("workers").EnumerateArray().Any(w => w.GetProperty("report").GetProperty("commanderOrders")
            .EnumerateArray().Any(o => o.GetProperty("Ship").GetInt16() == recipient && o.GetProperty("OrderedTarget").GetInt16() == enemyId)),
            "Bot did not adopt the human target through OrderedTarget");
        Assert.Equal(0, host.OnSimulation(s => s.SimPassStageFailures));
        var oldMission = host.OnSimulation(s => s.Missions[0]);
        host.OnSimulation(s => { P660NextMatchLiveTests.End(s); return true; });
        await Until(() => host.OnSimulation(s => !ReferenceEquals(oldMission, s.Missions[0]) && s.Missions[0].Stage == MissionStage.Started)
            && pilot.World.IsTeamLeader, "Human command did not survive next match", 120);
        lock (pilot.Gate) Assert.DoesNotContain(pilot.World.Ships.Values, s => s.CharacterName.StartsWith("research-cmd0-"));
        output.WriteLine(JsonSerializer.Serialize(new { development = bucket.Development!.Value.DevelopmentId, hull = choice.Hull, bots = botIds.Length, humanCommander = true, nextMatch = true, logs = host.DirectoryPath }));
    }
}

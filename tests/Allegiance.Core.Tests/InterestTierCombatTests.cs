using System.Diagnostics;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using FltBot = Allegiance.Bot.Bot;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class InterestTierCombatTests
{
    [SkippableFact, Trait("Requires", "Artwork")]
    public void Six_bot_firing_lanes_keep_kills_and_target_freshness_with_tiers()
    {
        Require.Artwork();
        var results = new List<object>();
        var completion = new List<double>();
        foreach (bool tiers in new[] { false, true })
        {
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            int placed = 0;
            DataPartTypeIGC.Weapon? gun = null;
            server.ShipSpawner = (core, mission, id) =>
            {
                var hull = core.HullTypes.Values.Where(h => h.MaxEnergy > 0 && h.MaxAmmo > 0
                    && h.Hardpoints.Length > 0 && h.Hardpoints[0].Fixed).OrderBy(h => h.HullId).First();
                gun ??= core.PartTypes.Values.Where(p => p.EquipmentType == EquipmentType.Weapon)
                    .Select(p => (DataPartTypeIGC.Weapon)p.Type)
                    .Where(w => w.Dispersion == 0 && w.EnergyPerShot <= hull.MaxEnergy
                        && core.ProjectileTypes[w.ProjectileTypeId].Power > 0)
                    .OrderBy(w => w.EnergyPerShot).First();
                var ship = new Ship(id, hull, new(0, 15000, 0), Vector3.Zero, Orientation.Identity)
                    { SideId = (short)(placed++ % 2) };
                mission.PlaceShip(ship, mission.Sectors[0]);
                return (ship, mission.Sectors[0]);
            };
            var config = InterestTierTests.Config();
            config.Broadcast!.InterestTiers = tiers;
            config.Broadcast.ImmutableSnapshot = config.Broadcast.EncodeOnce = true;
            server.Boot(config);
            var bots = new List<FltBot>();
            try
            {
                for (int i = 0; i < 12; i++)
                {
                    var bot = new FltBot(); bots.Add(bot);
                    string name = $"p677fight{i}";
                    bot.Connect(server.BoundPort, name);
                    bot.SendLogonRequest(new LogonRequest { CharacterName = name, ZoneTicket = [1],
                        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw", FedsrvVer = LogonRequest.MsgVer,
                        Time = 677, DwCookie = uint.MaxValue, CrcFileList = -1, SteamAuthTicket = new byte[16],
                        SteamAuthTicketLength = 0, SteamID = (ulong)(74000000000677000 + i), DrmHash = "p677" });
                    var wait = Stopwatch.StartNew();
                    while (bot.ShipAssignment is null && wait.Elapsed.TotalSeconds < 15)
                    { server.TickSim(); Thread.Sleep(2); }
                    Assert.NotNull(bot.ShipAssignment);
                }
                var ships = bots.Select(b => server.Ships.Single(s => s.Ship.Id == b.ShipAssignment!.Ship.ShipId).Ship).ToArray();
                server.TestMission!.Stage = MissionStage.Started;
                for (int i = 0; i < ships.Length; i++)
                {
                    var ship = ships[i];
                    ship.Position = new(10000 + i / 2 * 2200, 15000, i % 2 == 0 ? 180 : 0);
                    ship.Velocity = Vector3.Zero;
                    ship.Orientation.Reset();
                    ship.SideId = i % 2 == 0 ? (short)0 : null; // final deaths, no P107 pod replacement
                    ship.SideVisibility.SetSideVisibility(0, true);
                    ship.SideVisibility.SetSideVisibility(1, true);
                    ship.StationId = null;
                    if (i % 2 == 0)
                    {
                        ship.Energy = ship.HullType.MaxEnergy * 100;
                        ship.Ammo = short.MaxValue;
                        ship.MountWeapon(gun!);
                        ship.CommandTarget = ships[i + 1];
                    }
                    else ship.SetFraction(.25f);
                    bots[i].CommandControls(new(0, 0, 0, -1));
                    bots[i].ResetHeavyShipsUpdates(); bots[i].ResetKills();
                }
                double start = server.SimNow;
                var wall = Stopwatch.StartNew();
                var fresh = new int[6];
                var deathTimes = new double[6];
                var lastTargetTick = new uint[6];
                int ticks = 0;
                // Bots acquire their paired command target from real HEAVY
                // receipts before firing, then aim down independent clear lanes.
                while (ticks++ < 400 && deathTimes.Any(t => t == 0))
                {
                    for (int lane = 0; lane < 6; lane++)
                    {
                        var updates = bots[lane * 2].HeavyShipsUpdates;
                        var target = ships[lane * 2 + 1];
                        var update = updates.LastOrDefault(u => u.HeavyShipEntries.Any(e => e.ShipId == target.Id));
                        if (update is not null)
                        {
                            var entry = update.HeavyShipEntries.First(e => e.ShipId == target.Id);
                            Vector3 observed = entry.Position(update.PositionReference);
                            if (Math.Abs(observed.X - target.Position.X) < 2 && observed.Z < ships[lane * 2].Position.Z)
                                bots[lane * 2].CommandControls(new(0, 0, 0, -1), ShipControlState.AllWeapons);
                            if (update.TimeReference != lastTargetTick[lane])
                            { fresh[lane]++; lastTargetTick[lane] = update.TimeReference; }
                        }
                    }
                    server.TickSim();
                    server.FlushBroadcastSnapshots();
                    for (int lane = 0; lane < 6; lane++)
                        if (deathTimes[lane] == 0 && ships[lane * 2 + 1].Dead)
                            deathTimes[lane] = server.SimNow - start;
                    Thread.Sleep(10);
                }
                // Let both ends observe the final reliable kill receipts.
                var drain = Stopwatch.StartNew();
                while (bots[0].Kills.Select(k => k.ShipId).Distinct().Count() < 6 && drain.Elapsed.TotalSeconds < 5)
                { server.TickSim(); Thread.Sleep(5); }
                Assert.All(deathTimes, t => Assert.True(t > 0, "each bot must kill its paired target"));
                Assert.Equal(6, bots[0].Kills.Select(k => k.ShipId).Distinct().Count());
                Assert.All(fresh, n => Assert.True(n > 0));
                double seconds = deathTimes.Max();
                completion.Add(seconds);
                results.Add(new { tiers, peers = bots.Count, kills = 6, simulationSeconds = seconds,
                    killsPerSimSecond = 6 / seconds, wallSeconds = wall.Elapsed.TotalSeconds,
                    deathTimes, targetReceiptsPerLane = fresh, ticks });
            }
            finally { foreach (var bot in bots) bot.Dispose(); }
        }
        File.WriteAllText(Path.Combine(Path.GetTempPath(), "p677-combat.json"), JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        Assert.True(completion[1] <= completion[0] + .5, $"combat completion degraded: {completion[0]} -> {completion[1]}");
    }
}

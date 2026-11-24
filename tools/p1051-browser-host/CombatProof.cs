using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using SimVec = Allegiance.Core.Collision.Vector3;

// Loopback proof fixture, inert until a local scratch request exists. All drops
// are real sector models; broadcasts use production codecs and server methods.
// No production config, admission, simulation or rendering rule is changed.
internal static class CombatProof
{
    private static readonly BindingFlags Private = BindingFlags.NonPublic | BindingFlags.Instance;
    private static short? _protectedPilot;

    internal static void ObserveLog(string scratch) => Console.SetOut(new ExplosionLog(Console.Out, scratch));

    internal static void Poll(Server server, string scratch)
    {
        var ships = server.Missions.SelectMany(m => m.GetShips()).ToArray();
        if (_protectedPilot is { } protectedId && ships.FirstOrDefault(s => s.Id == protectedId) is { } protectedShip)
        { protectedShip.SetFraction(1); protectedShip.SetShieldFraction(1); }
        string request = Path.Combine(scratch, "combat-request.json");
        if (!File.Exists(request)) return;
        using var data = JsonDocument.Parse(File.ReadAllText(request));
        short id = data.RootElement.GetProperty("ship").GetInt16();
        string action = data.RootElement.GetProperty("action").GetString()!;
        var pilot = ships.Single(s => s.Id == id);
        if (pilot.Sector is not { } sector || pilot.StationId is not null) throw new InvalidOperationException("Combat proof needs a flying pilot.");
        var mission = pilot.Mission!;
        var peerTable = (IDictionary)typeof(Server).GetField("_peerShips", Private)!.GetValue(server)!;
        var peers = new List<ConnectionId>();
        foreach (DictionaryEntry entry in peerTable)
            if (ReferenceEquals(entry.Value!.GetType().GetProperty("Ship")!.GetValue(entry.Value), pilot)) peers.Add((ConnectionId)entry.Key);
        if (peers.Count != 1) throw new InvalidOperationException("Proof pilot must own one live peer.");
        void Invoke(string method, params object?[] args) => typeof(Server).GetMethods(Private)
            .Single(m => m.Name == method && m.GetParameters().Length == args.Length
                && m.GetParameters().Zip(args).All(pair => pair.Second is null || pair.First.ParameterType.IsInstanceOfType(pair.Second)))
            .Invoke(server, args);
        void Send(IFedMessage message) => Invoke("SendPeer", peers[0], FedPacket.Pack(message.Encode()));
        var forward = pilot.Orientation.GetForward();
        var right = pilot.Orientation.GetRight();
        var above = pilot.Orientation.GetUp();
        var center = pilot.Position + forward * 45;
        object details = new { };
        switch (action)
        {
            case "protect": _protectedPilot = id; break;
            case "unprotect": _protectedPilot = null; break;
            case "props":
            {
                var mine = new Mine(mission.NextObjectId(), sector, mission.Core.MineTypes.Values.First(), center + right * 14, server.SimNow, pilot);
                sector.Add(mine); Invoke("BroadcastExportMine", mine);
                var probeType = mission.Core.ProbeTypes.Values.First(t => t.ModelName == "utl02");
                var probe = new Probe(mission.NextObjectId(), sector, probeType, center - right * 14, server.SimNow, pilot.SideId, pilot);
                sector.Add(probe); Invoke("BroadcastExportProbe", probe);
                var pos = center + above * 12;
                var treasureData = new DataTreasureIGC([pos.X, pos.Y, pos.Z], [0, 0, 0], 120, 0,
                    mission.NextObjectId(), 500, sector.Id, 500, TreasureCode.Cash, true);
                var treasure = new Treasure(treasureData.ObjectId, sector, treasureData, server.SimNow, mission);
                sector.Add(treasure); sector.RefreshTreasureVisibility(treasure); mission.Site.TreasureGeneratedEvent(treasure);
                var chaffType = mission.Core.ChaffTypes.Values.First();
                var chaff = new Chaff(mission.NextObjectId(), sector, chaffType, center + right * 28 - above * 12, SimVec.Zero, server.SimNow, pilot);
                sector.Add(chaff);
                Send(new CreateChaff { P0 = chaff.Position, V0 = chaff.Velocity,
                    Time0 = IgcTime.ToMilliseconds(server.SimNow), Etid = chaffType.ExpendableTypeId });
                details = new { mine = mine.Id, probe = probe.Id, treasure = treasure.Id, chaff = chaff.Id };
                break;
            }
            case "missile":
            {
                var type = mission.Core.MissileTypes.Values.First(t => t.ModelName == "mis05");
                var missile = new Missile(mission.NextObjectId(), sector, type, pilot.Position - forward * pilot.HullType.Length + right * (pilot.HullType.Length * .4f)
                    + above * (pilot.HullType.Length * .3f), forward * 25, forward, server.SimNow, pilot);
                sector.Add(missile); Invoke("BroadcastFireMissile", missile);
                details = new { missile = missile.Id, type = type.ExpendableTypeId };
                break;
            }
            case "shield":
            case "hull":
                // Controlled hit cue at the real pilot shield; the live battle
                // sample separately requires actual combat hits before fixtures.
                var impact = (-forward + right).Normalize();
                Send(new ProjectileHullHit { TargetId = id, ClusterId = sector.Id, Radius = pilot.HullType.Length * .5f,
                    Position = pilot.Position + impact * pilot.HullType.Length * .5f, Normal = impact,
                    AbsorbedByShield = action == "shield", SourceId = -1 });
                break;
            case "volumetric":
            {
                // P1083 spike: a bounded, simultaneous native-event burst. The
                // read-only proof camera is 200 units above the real pilot,
                // with the blast 150 units straight ahead (same as P834 full).
                int count = data.RootElement.GetProperty("count").GetInt32();
                if (count is not (0 or 1 or 4 or 10)) throw new InvalidDataException("Volumetric proof count must be 0, 1, 4 or 10.");
                var origin = pilot.Position + new SimVec(0, 200, 0);
                var blast = origin + new SimVec(0, 0, -150);
                for (int i = 0; i < count; i++)
                    Invoke("SendExplosionEvent", peers, ExplosionType.SmallShip, sector.Id, blast, SimVec.Zero, pilot.HullType.Length);
                details = new { count, type = (int)ExplosionType.SmallShip, distance = 150,
                    eye = new[] { origin.X, origin.Y, origin.Z }, position = new[] { blast.X, blast.Y, blast.Z } };
                break;
            }
            case "explosion":
                Invoke("SendExplosionEvent", peers, ExplosionType.SmallShip, sector.Id, center, SimVec.Zero, pilot.HullType.Length);
                break;
            case "pod":
            {
                _protectedPilot = null;
                var attacker = ships.First(s => s.SideId != pilot.SideId && s.Sector == sector && !s.Dead);
                // Drive the real combat kill/eject path, rather than editing the
                // browser world, injecting EJECT or manufacturing a blast. The
                // browser's native KILL_SHIP cue must supply the hull explosion.
                pilot.SetShieldFraction(0);
                pilot.ReceiveDamage(0, 1000000, server.SimNow, pilot.Position, attacker.Position, attacker);
                details = new { hull = pilot.HullType.HullId, pilot.Dead,
                    explosionPosition = new[] { pilot.Position.X, pilot.Position.Y, pilot.Position.Z } };
                break;
            }
            default: throw new InvalidDataException("Unknown combat proof action.");
        }
        var receipt = new { action, ship = id, sector = sector.Id, localMs = Environment.TickCount64, details };
        File.WriteAllText(Path.Combine(scratch, "combat-response.tmp"), JsonSerializer.Serialize(receipt));
        File.Move(Path.Combine(scratch, "combat-response.tmp"), Path.Combine(scratch, "combat-response.json"), true);
        File.Delete(request);
    }

    private sealed class ExplosionLog(TextWriter original, string scratch) : TextWriter
    {
        public override Encoding Encoding => original.Encoding;
        public override void Write(char value) => original.Write(value);
        public override void Write(string? value) => original.Write(value);
        public override void WriteLine(string? value)
        {
            original.WriteLine(value);
            if (value?.StartsWith("[explosion]", StringComparison.Ordinal) == true && File.Exists(Path.Combine(scratch, "combat-observe")))
                File.AppendAllText(Path.Combine(scratch, "combat-events.jsonl"), JsonSerializer.Serialize(new
                { localMs = Environment.TickCount64, line = value }) + "\n");
        }
    }
}

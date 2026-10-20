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
/// P751 — callback and broadcast parity for <c>FedSrvSiteBase::DamageShipEvent</c>
/// (FedSrv.CPP:12089-12178, shipIGC.cpp:398, shipIGC.cpp:1312).
/// <list type="number">
/// <item>The <see cref="IIgcSite.DamageShipEvent"/> seam is raised on a non-lethal
/// ship hit and carries the concrete ship, launcher, time, damage type and amount.</item>
/// <item>A live enemy hit on an NPC ship produces a team "Under attack" chat with
/// the pilot-type-specific voice line on the defending side; the attacker side
/// hears nothing.</item>
/// <item>A same-side hit produces a private "Watch your fire" to the launcher pilot.</item>
/// <item>A second hit inside the 10-second throttle window produces no second alert.</item>
/// </list>
/// </summary>
[Collection("LiveNetwork")]
public sealed class ShipDamageAlertTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const byte DmgIdNoWarn = 0x20;          // c_dmgidNoWarn (igc.h:559)

    private static StaticCore Core()
    {
        var file = IgcReader.ParseFile(Path.Combine(ArtRoot, "PCore001.igc"));
        if (file.Wrapper != IgcWrapper.VersionAndDataSize)
            throw new InvalidDataException("PCore001.igc is not a static core");
        return StaticCore.Load(file);
    }

    private static MissionMap SomeMap()
    {
        foreach (var path in Directory.EnumerateFiles(ArtRoot, "*.igc"))
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count > 0) return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    }

    private static float Dtm(StaticCore core, byte dmg, byte def) => core.Constants.DamageConstants[dmg][def];

    private static byte PositiveCellDamageType(StaticCore core)
    {
        foreach (var pt in core.ProjectileTypes.Values)
        {
            if (pt.Power <= 0f) continue;
            if (Dtm(core, pt.DamageType, 0) > 0f) return pt.DamageType;
        }
        throw new InvalidOperationException("no projectile damage type with a positive cell");
    }

    private static Mission NewStartedMission(IIgcSite site)
    {
        var core = Core();
        var civ = core.Civilizations.Values.OrderBy(c => c.CivilizationId).First().CivilizationId;
        var m = Mission.Assemble(core, SomeMap(), [new SideSpec(0, "Team 1", civ), new SideSpec(1, "Team 2", civ)], site);
        m.Stage = MissionStage.Started;
        return m;
    }

    private static Ship NewShooter(DataHullTypeIGC hull) =>
        new(9700, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity);

    private static void SetPilotType(Ship ship, byte pilotType)
    {
        typeof(Ship).GetProperty(nameof(Ship.PilotType))!.SetValue(ship, pilotType);
    }

    /// <summary>⭐ A non-lethal ship hit raises <c>DamageShipEvent</c> through the
    /// mission site seam with the ship, launcher, time, type and amount.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Nonlethal_ship_hit_raises_DamageShipEvent_with_launcher_and_amount()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();

        var target = NewShooter(hull);
        target.SideId = 0;
        SetPilotType(target, Ship.PilotTypeMiner);
        mission.PlaceShip(target, sector);

        var launcher = NewShooter(hull);
        launcher.SideId = 1;
        mission.PlaceShip(launcher, sector);

        byte dtype = PositiveCellDamageType(core);
        float dtm = Dtm(core, dtype, 0);
        float maxHP = target.EffectiveMaxHitPoints();
        float amount = 0.01f * maxHP / dtm; // 1% hull damage, non-lethal
        var res = target.ReceiveDamage(dtype, amount, mission.LastUpdate, Vector3.Zero, target.Position, launcher);
        Assert.Equal(DamageResult.HullDamage, res);
        Assert.True(target.Fraction > 0f, "target must survive for DamageShipEvent, not KillShipEvent");

        var call = recording.Calls.FirstOrDefault(c => c.StartsWith("DamageShipEvent("));
        Assert.NotNull(call);
        Assert.Contains($", {amount.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)}", call);
        Console.WriteLine($"P751-CALLBACK: {call}");
    }

    /// <summary>⭐ <c>DamageShipEvent</c> is still raised with the no-warn bit set;
    /// the native no-warn gate lives in the host callback (FedSrv.CPP:12091), not
    /// in the simulation seam. The live test below verifies the broadcast is suppressed.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void NoWarn_damage_still_raises_DamageShipEvent_for_host_gating()
    {
        Require.Artwork();
        var recording = new RecordingIgcSite();
        var mission = NewStartedMission(recording);
        var sector = mission.Sectors[0];
        var core = Core();
        var hull = core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();

        var target = NewShooter(hull);
        target.SideId = 0;
        mission.PlaceShip(target, sector);

        var launcher = NewShooter(hull);
        launcher.SideId = 1;
        mission.PlaceShip(launcher, sector);

        byte dtype = PositiveCellDamageType(core);
        float dtm = Dtm(core, dtype, 0);
        float maxHP = target.EffectiveMaxHitPoints();
        float amount = 0.01f * maxHP / dtm;
        var res = target.ReceiveDamage((byte)(DmgIdNoWarn | dtype), amount, mission.LastUpdate, Vector3.Zero, target.Position, launcher);
        Assert.Equal(DamageResult.HullDamage, res);
        Assert.Contains(recording.Calls, c => c.StartsWith("DamageShipEvent("));
        Console.WriteLine("P751-NOWARN: no-warn damage raised DamageShipEvent; host will suppress the alert");
    }

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "Ship Damage Alert Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 778u, DwCookie = 0x778u,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 77200000000000000ul, DrmHash = "staunderattack",
    };

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Attacker = null!, Target = null!;
        public FltBot AttackerBot = null!, TargetBot = null!;
        public Sector Sector = null!;

        public void Dispose()
        {
            TargetBot?.Dispose();
            AttackerBot?.Dispose();
            Server?.Dispose();
        }
    }

    private static Seated Seat(bool targetIsNpc)
    {
        var seat = new Seated();
        seat.Server = new Srv();
        seat.Server.SpawnInitialMinersAtBoot = false;

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            var hull = mission.Core.HullTypes.Values.OrderByDescending(h => h.HitPoints).First();
            var ship = placed == 0
                ? new Ship(id, hull, Vector3.Zero, Vector3.Zero, Orientation.Identity)
                : new Ship(id, hull, new Vector3(0f, 0f, 200f), Vector3.Zero, Orientation.Identity);
            if (placed != 0)
                SetPilotType(ship, targetIsNpc ? Ship.PilotTypeMiner : Ship.PilotTypePlayer);
            ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var m in seat.Server.Missions) { m.Stage = MissionStage.Started; m.AllowFriendlyFire = true; }

        var port = seat.Server.BoundPort;
        seat.AttackerBot = new FltBot();
        seat.TargetBot = new FltBot();
        try
        {
            seat.AttackerBot.Connect(port, "p751_attacker"); seat.AttackerBot.SendLogonRequest(Request("p751_attacker"));
            seat.TargetBot.Connect(port, "p751_target"); seat.TargetBot.SendLogonRequest(Request("p751_target"));
            PumpUntil(seat.Server, () => seat.AttackerBot.ShipAssignment is not null && seat.TargetBot.ShipAssignment is not null, 4000);

            foreach (var bot in new[] { seat.AttackerBot, seat.TargetBot })
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                // AttackerBot is the first connected peer (side A); TargetBot is side B.
                if (ship.SideId == seat.Server.Missions[0].Sides[0].Id)
                {
                    seat.Attacker = ship;
                    seat.AttackerBot = bot;
                }
                else
                {
                    seat.Target = ship;
                    seat.TargetBot = bot;
                }
            }
            Assert.NotNull(seat.Attacker);
            Assert.NotNull(seat.Target);
            Assert.NotEqual(seat.Attacker.SideId, seat.Target.SideId);
            return seat;
        }
        catch
        {
            seat.Dispose();
            throw;
        }
    }

    private static int PumpUntil(Srv server, Func<bool> done, int max)
    {
        int n = 0;
        while (!done() && n < max) { server.TickSim(); n++; Thread.Sleep(1); }
        return n;
    }

    /// <summary>⭐ A live cross-side hit on an NPC ship produces a team
    /// "Under attack" chat on the target's side with the miner voice line.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Cross_side_NPC_hit_sends_under_attack_team_alert()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: true);
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        // Direct non-lethal damage so the DamageShipEvent seam fires (not KillShipEvent).
        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        int pumped = PumpUntil(seat.Server, () => seat.TargetBot.Chats.Any(c => c.Message.Contains("Under attack")), 3000);
        foreach (var c in seat.TargetBot.Chats) Console.WriteLine($"  target chat: {c.Message} (target={c.ChatTarget}, voice={c.VoiceOver})");
        foreach (var c in seat.AttackerBot.Chats) Console.WriteLine($"  attacker chat: {c.Message} (target={c.ChatTarget}, voice={c.VoiceOver})");
        Assert.True(pumped < 3000, $"target never received under-attack alert (got {seat.TargetBot.Chats.Count} chats)");

        var alert = seat.TargetBot.Chats.Single(c => c.Message.Contains("Under attack"));
        Assert.Equal((int)ChatTarget.Team, alert.ChatTarget);
        Assert.Contains("Under attack by", alert.Message);
        Assert.Equal(seat.Target.Id, alert.SidSender);
        Assert.DoesNotContain(seat.AttackerBot.Chats, c => c.Message.Contains("Under attack"));
        Console.WriteLine($"P751-LIVE-ALERT: defender got '{alert.Message}' (voice {alert.VoiceOver})");
    }

    /// <summary>⭐ A live same-side hit sends a private "Watch your fire" to the
    /// launcher, not a team under-attack alert.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_hit_sends_watch_your_fire_to_launcher()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: true);
        seat.Target.SideId = seat.Attacker.SideId; // force same side
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        PumpUntil(seat.Server, () => seat.AttackerBot.Chats.Any(c => c.Message.Contains("Watch your fire")), 3000);

        var warn = seat.AttackerBot.Chats.Single(c => c.Message.Contains("Watch your fire"));
        Assert.Equal((int)ChatTarget.Individual, warn.ChatTarget);
        Assert.Equal(seat.Attacker.Id, warn.OidRecipient);
        Assert.DoesNotContain(seat.TargetBot.Chats, c => c.Message.Contains("Under attack"));
        Console.WriteLine($"P751-LIVE-FF: attacker got '{warn.Message}'");
    }

    /// <summary>⭐ A second hit inside the 10-second throttle window produces no
    /// second alert (FedSrv.CPP:12103,12158).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Second_hit_within_throttle_window_is_silent()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: true);
        seat.TargetBot.ResetChatReceived();

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        PumpUntil(seat.Server, () => seat.TargetBot.Chats.Count == 1, 3000);
        Assert.Single(seat.TargetBot.Chats);
        Assert.True(seat.Target.LastDamageReport > 0, "alert must stamp LastDamageReport");

        seat.Target.ReceiveDamage(0, 5f, seat.Server.SimNow + 1.0, Vector3.Zero, seat.Target.Position, seat.Attacker);
        PumpUntil(seat.Server, () => seat.Server.SimNow > seat.Target.LastDamageReport + 0.5, 3000);
        Assert.Single(seat.TargetBot.Chats);
        Console.WriteLine("P751-LIVE-THROTTLE: second in-window hit produced no new alert");
    }

    /// <summary>⭐ A same-side hit on a player victim produces no alert at all
    /// (FedSrv.CPP:12174-12185 only whispers for allied cross-side).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Same_side_player_victim_is_silent()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: false);
        seat.Target.SideId = seat.Attacker.SideId; // force same side
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        PumpUntil(seat.Server, () => seat.Server.SimNow > seat.Server.SimNow + 0.5, 100);

        Assert.DoesNotContain(seat.AttackerBot.Chats, c => c.Message.Contains("Watch your fire"));
        Assert.DoesNotContain(seat.TargetBot.Chats, c => c.Message.Contains("Under attack"));
        Console.WriteLine("P751-LIVE-FF-PLAYER: same-side player victim produced no alert");
    }

    /// <summary>⭐ An allied cross-side hit on a player victim whispers
    /// "Watch your fire" to the launcher (FedSrv.CPP:12174-12185).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_cross_side_player_victim_sends_watch_your_fire()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: false);
        var mission = seat.Server.Missions[0];
        mission.AlliedSidePairs.Add(((short)seat.Attacker.SideId!, (short)seat.Target.SideId!));
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        int pumped = PumpUntil(seat.Server, () => seat.AttackerBot.Chats.Any(c => c.Message.Contains("Watch your fire")), 3000);
        Assert.True(pumped < 3000, "allied player victim should whisper watch-your-fire to launcher");

        var warn = seat.AttackerBot.Chats.Single(c => c.Message.Contains("Watch your fire"));
        Assert.Equal((int)ChatTarget.Individual, warn.ChatTarget);
        Assert.Equal(seat.Attacker.Id, warn.OidRecipient);
        Assert.Equal(seat.Target.Id, warn.SidSender);
        Console.WriteLine($"P751-LIVE-ALLY-FF-PLAYER: '{warn.Message}'");
    }

    /// <summary>⭐ An allied cross-side hit on an NPC ship sends a team
    /// "Under attack" chat with the "(Ally)" prefix and also whispers
    /// "Watch your fire" to the allied player launcher
    /// (FedSrv.CPP:12120-12145).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_NPC_hit_sends_under_attack_with_ally_prefix_and_watch_your_fire()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: true);
        var mission = seat.Server.Missions[0];
        mission.AlliedSidePairs.Add(((short)seat.Attacker.SideId!, (short)seat.Target.SideId!));
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        int pumped = PumpUntil(seat.Server, () => seat.TargetBot.Chats.Any(c => c.Message.Contains("Under attack")), 3000);
        Assert.True(pumped < 3000, "target never received under-attack alert");

        var alert = seat.TargetBot.Chats.Single(c => c.Message.Contains("Under attack"));
        Assert.Contains("(Ally)", alert.Message);
        Assert.Contains($"Under attack by (Ally) ", alert.Message);

        PumpUntil(seat.Server, () => seat.AttackerBot.Chats.Any(c => c.Message.Contains("Watch your fire")), 3000);
        var warn = seat.AttackerBot.Chats.Single(c => c.Message.Contains("Watch your fire"));
        Assert.Equal(seat.Target.Id, warn.SidSender);
        Console.WriteLine($"P751-LIVE-ALLY-NPC: '{alert.Message}' + '{warn.Message}'");
    }

    /// <summary>⭐ A non-ship launcher (probe) must still trigger the NPC
    /// under-attack alert; the side is read from the launcher model
    /// (FedSrv.CPP:12095).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_launcher_triggers_under_attack_alert()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: true);
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        var probeType = seat.Server.Missions[0].Core.ProbeTypes.Values.First();
        var probe = new Probe(9800, seat.Sector, probeType, seat.Attacker.Position, seat.Server.SimNow, seat.Attacker.SideId);
        seat.Sector.Add(probe);

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, probe);
        int pumped = PumpUntil(seat.Server, () => seat.TargetBot.Chats.Any(c => c.Message.Contains("Under attack")), 3000);
        Assert.True(pumped < 3000, "probe launcher should trigger under-attack alert");

        var alert = seat.TargetBot.Chats.Single(c => c.Message.Contains("Under attack"));
        Assert.Contains("Probe 9800", alert.Message);
        Assert.DoesNotContain(seat.AttackerBot.Chats, c => c.Message.Contains("Watch your fire"));
        Console.WriteLine($"P751-LIVE-PROBE: '{alert.Message}'");
    }

    /// <summary>⭐ A builder NPC uses the constructor station type's
    /// <c>ConstructorUnderAttackSound</c>, not the generic station
    /// <c>UnderAttackSound</c> (FedSrv.CPP:12124).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Builder_under_attack_uses_constructor_sound()
    {
        Require.Artwork();
        using var seat = Seat(targetIsNpc: true);
        seat.AttackerBot.ResetChatReceived();
        seat.TargetBot.ResetChatReceived();

        SetPilotType(seat.Target, Ship.PilotTypeBuilder);
        var stationType = seat.Server.Missions[0].Core.StationTypes.Values.First();
        seat.Target.BuilderStationType = stationType;

        seat.Target.ReceiveDamage(0, 1f, seat.Server.SimNow, Vector3.Zero, seat.Target.Position, seat.Attacker);
        int pumped = PumpUntil(seat.Server, () => seat.TargetBot.Chats.Any(c => c.Message.Contains("Under attack")), 3000);
        Assert.True(pumped < 3000, "builder never received under-attack alert");

        var alert = seat.TargetBot.Chats.Single(c => c.Message.Contains("Under attack"));
        Assert.Equal(stationType.ConstructorUnderAttackSound, alert.VoiceOver);
        Console.WriteLine($"P751-LIVE-BUILDER: voice {alert.VoiceOver} == constructor sound {stationType.ConstructorUnderAttackSound}");
    }
}

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
/// P451 — live loopback proof that a real <b>enemy</b> ordnance hit on a real
/// station now produces the defending team's base-under-attack alert over the
/// wire, exactly as native's <c>FedSrvSiteBase::DamageStationEvent</c>
/// (<c>FedSrv.CPP:12183-12233</c>) does:
/// <list type="number">
/// <item>the DEFENDING side receives a team <see cref="Wire.ChatMessage"/> with
/// the exact under-attack text and <c>VoiceOver == UnderAttackSound</c> for the
/// station type, while the ATTACKER's side receives nothing;</item>
/// <item>a second hit within the 10-second throttle window produces no second
/// alert;</item>
/// <item>once the throttle window has reopened, a hit that drops the (pre-hit)
/// hull fraction below 0.30 produces the critical variant with
/// <c>VoiceOver == CriticalSound</c> and the "critically damaged" text.</item>
/// </list>
/// The ordnance is a real missile fired via <see cref="Ship.FireMissile"/>
/// against a synthetic side-B station placed in sector 0 (mirroring
/// <see cref="AsteroidCombatLiveTests"/>' rocket-against-rock harness), so the
/// whole chain — swept collision (<c>Model.Sector.ResolveCombatCollisions</c>) →
/// <c>Station.ReceiveDamage</c> → the P451 <c>DamageStationEvent</c> seam →
/// the host alert — is exercised through the real combat path.
/// [Collection("LiveNetwork")] keeps this suite sequential against the other
/// live suites.
/// </summary>
[Collection("LiveNetwork")]
public sealed class StationUnderAttackAlertLiveTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;
    private const byte DmgIdNoWarn = 0x20;          // c_dmgidNoWarn (igc.h:559)
    private static readonly Vector3 StationSpot = new(0f, 0f, -5000f);  // away from the origin ship spawns
    private const float FireGap = 100f;             // ahead (−z) of the station; > its radius-40 collision sphere

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
            new Gm { Name = "Station Under Attack Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
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

    /// <summary>A real missile with straight-line reach &gt; FireGap and no
    /// <c>c_dmgidNoWarn</c> flag (so the alert fires), scanned from decoded data.
    /// The alert only cares that <c>Power ≥ 1</c>, which every real missile is.</summary>
    private static DataMissileTypeIGC Missile(StaticCore core)
    {
        foreach (var m in core.MissileTypes.Values)
        {
            if (m.InitialSpeed <= 0f || m.Power < 1f || m.Lifespan < 3f) continue;
            if (m.InitialSpeed * m.Lifespan < 250f) continue;
            if ((m.DamageType & DmgIdNoWarn) != 0) continue;
            return m;
        }
        throw new InvalidOperationException("no usable missile in representative core");
    }

    /// <summary>A real (garrison-family) station type with NON-ZERO decoded
    /// UnderAttackSound and CriticalSound — the voice lines the alert must carry —
    /// rebuilt with a small radius (40) so the attacker can stand just outside its
    /// collision sphere and still be in missile reach, and with plenty of hull so a
    /// single hit never destroys it mid-test.</summary>
    private static Station MakeStation(StaticCore core, short sectorId, short side, short id)
    {
        var baseType = core.StationTypes.Values.First(st => st.CriticalSound != 0 && st.UnderAttackSound != 0);
        var type = baseType with
        {
            Radius = 40,
            MaxArmorHitPoints = 100000f,
            MaxShieldHitPoints = 0f,
        };
        var data = new DataStationIGC(
            Position: [StationSpot.X, StationSpot.Y, StationSpot.Z], Up: [0f, 1f, 0f], Forward: [1f, 0f, 0f],
            Rotation: [0f, 0f, 0f, 1f], ClusterId: sectorId, SideId: side, StationId: id,
            StationTypeId: baseType.StationTypeId, BpHull: 1, BpShield: 1, Name: $"BASE_{id}");
        return new Station(data, type);
    }

    private sealed class Seated : IDisposable
    {
        public Srv Server = null!;
        public Ship Attacker = null!, Defender = null!;
        public FltBot AttackerBot = null!, DefenderBot = null!;
        public Station Station = null!;
        public Sector Sector = null!;
        public StaticCore Core = null!;
        public DataMissileTypeIGC Missile;

        public void Dispose()
        {
            DefenderBot?.Dispose();
            AttackerBot?.Dispose();
            Server?.Dispose();
        }
    }

    /// <summary>A booted 2-bot / 2-side live server. Slot 0 = side A (the ATTACKER,
    /// cross-side to the base); slot 1 = side B (the DEFENDER, who owns the
    /// synthetic station placed in sector 0). The station sits at
    /// <see cref="StationSpot"/>, far from the origin spawns.</summary>
    private static Seated Seat()
    {
        var seat = new Seated();
        var core = Core();
        seat.Core = core;
        seat.Server = new Srv();
        seat.Server.SpawnInitialMinersAtBoot = false;   // keep the arena pristine for the alert assertions

        int placed = 0;
        seat.Server.ShipSpawner = (_, mission, id) =>
        {
            var sector = mission.Sectors[0];
            seat.Sector = sector;
            if (seat.Station is null)
            {
                seat.Station = MakeStation(core, sector.Id, mission.Sides[1].Id, (short)(8800 + placed));
                mission.PlaceStation(seat.Station, sector);
            }
            var ship = placed switch
            {
                0 => new Ship(id, core.HullTypes.Values.OrderBy(h => h.HitPoints).First(), Vector3.Zero, Vector3.Zero, Orientation.Identity),
                _ => new Ship(id, core.HullTypes.Values.OrderBy(h => h.HitPoints).First(), new Vector3(0f, 630f, 730f), Vector3.Zero, Orientation.Identity),
            };
            if (mission.Sides.Count > 0)
                ship.SideId = mission.Sides[placed % mission.Sides.Count].Id;   // round-robin A, B
            mission.PlaceShip(ship, sector);
            placed++;
            return (ship, sector);
        };
        seat.Server.Boot(Config());
        foreach (var __m in seat.Server.Missions) __m.Stage = MissionStage.Started;   // bypass ready-up, immediate flyable

        var port = seat.Server.BoundPort;
        seat.AttackerBot = new FltBot();
        seat.DefenderBot = new FltBot();
        try
        {
            seat.AttackerBot.Connect(port, "p451_attacker"); seat.AttackerBot.SendLogonRequest(Request("p451_attacker"));
            seat.DefenderBot.Connect(port, "p451_defender"); seat.DefenderBot.SendLogonRequest(Request("p451_defender"));
            PumpUntil(seat.Server, () => seat.AttackerBot.ShipAssignment is not null && seat.DefenderBot.ShipAssignment is not null, 4000);

            foreach (var bot in new[] { seat.AttackerBot, seat.DefenderBot })
            {
                var ship = seat.Server.Ships.First(s => s.Ship.Id == bot.ShipAssignment!.Ship.ShipId).Ship;
                // Socket arrival order is not connection/name order. Bind
                // both the ship and its peer to the actual assigned side.
                if (ship.SideId == seat.Station.SideId) { seat.Defender = ship; seat.DefenderBot = bot; }
                else { seat.Attacker = ship; seat.AttackerBot = bot; }
            }
            Assert.NotNull(seat.Attacker);
            Assert.NotNull(seat.Defender);
            Assert.True(seat.Attacker.SideId != seat.Station.SideId, "attacker must be cross-side to the station");
            Assert.Equal(seat.Station.SideId, seat.Defender.SideId);
            seat.Attacker.Velocity = Vector3.Zero;
            seat.Defender.Velocity = Vector3.Zero;
            seat.Missile = Missile(core);
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

    /// <summary>Park the attacker directly ahead (−z, identity nose) of the station
    /// at FireGap and fire one real missile at it through the real combat path.
    /// Returns the live <c>Missile</c> instance so the caller can await its impact
    /// via the station receiving damage is unnecessary — we pump on the alert.</summary>
    private static void FireAtStation(Seated seat)
    {
        seat.Attacker.Position = StationSpot + new Vector3(0f, 0f, FireGap);
        seat.Attacker.FireMissile(seat.Missile, seat.Attacker.Mission!.LastUpdate);
    }

    /// <summary>⭐ A real cross-side missile hit on a real station raises the P451
    /// seam and the DEFENDER (station's side) gets a team <c>ChatMessage</c> with
    /// the under-attack text and <c>VoiceOver == UnderAttackSound</c>; the ATTACKER's
    /// side hears nothing.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Defending_side_gets_under_attack_alert_attacker_side_hears_nothing()
    {
        Require.Artwork();
        using var seat = Seat();
        seat.AttackerBot.ResetChatReceived();
        seat.DefenderBot.ResetChatReceived();

        FireAtStation(seat);

        int got = PumpUntil(seat.Server, () => seat.DefenderBot.Chats.Any(c => c.Message.Contains("under attack")), 6000);
        Assert.True(got < 6000, "defender never received the under-attack alert");

        var alert = seat.DefenderBot.Chats.Single(c => c.Message.Contains("under attack"));
        Assert.Equal((int)ChatTarget.Team, alert.ChatTarget);
        Assert.Equal(seat.Station.Type.UnderAttackSound, alert.VoiceOver);
        Assert.Contains(seat.Station.Data.Name, alert.Message);
        Assert.Contains("under attack by", alert.Message);

        // The attacker's side received nothing.
        Assert.DoesNotContain(seat.AttackerBot.Chats, c => c.Message.Contains("under attack"));
        Assert.DoesNotContain(seat.AttackerBot.Chats, c => c.Message.Contains("critically damaged"));

        Console.WriteLine($"P451-ALERT: defender got under-attack alert '{alert.Message}' (voice {alert.VoiceOver}); attacker silent");
    }

    /// <summary>⭐ A second hit within the 10-second throttle window produces NO
    /// second alert (FedSrv.CPP:12190) — the station's LastDamageReport gates it.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Second_hit_within_throttle_window_produces_no_second_alert()
    {
        Require.Artwork();
        using var seat = Seat();
        seat.DefenderBot.ResetChatReceived();

        FireAtStation(seat);
        int got = PumpUntil(seat.Server, () => seat.DefenderBot.Chats.Count == 1, 20000);
        Assert.True(got < 20000, "first hit must alert the defender");
        double firstStamp = seat.Station.LastDamageReport;
        Assert.True(firstStamp > 0, "the alert must stamp the station's LastDamageReport");

        // A second hit a few ticks later is inside the 10 s window -> silent.
        FireAtStation(seat);
        PumpUntil(seat.Server, () => seat.Station.ArmorFraction < 1.0f, 3000);   // let the second missile land
        Assert.Single(seat.DefenderBot.Chats);
        Console.WriteLine($"P451-THROTTLE: second in-window hit produced no new alert (still {seat.DefenderBot.Chats.Count})");
    }

    /// <summary>⭐ Once the throttle window has reopened, a hit that drops the
    /// (pre-hit) hull fraction below 0.30 produces the CRITICAL variant with
    /// <c>VoiceOver == CriticalSound</c> and the "critically damaged" text
    /// (FedSrv.CPP:12199-12205).</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Sub_30_percent_fraction_produces_critical_alert_once_throttle_reopens()
    {
        Require.Artwork();
        using var seat = Seat();
        seat.DefenderBot.ResetChatReceived();

        FireAtStation(seat);
        int got = PumpUntil(seat.Server, () => seat.DefenderBot.Chats.Count == 1, 20000);
        Assert.True(got < 20000, "first hit must alert the defender");

        // Drop the station's pre-hit hull fraction below 0.30 (the sustained-bombing
        // state native reaches via many hits) and reopen the throttle window, then
        // land one more real hit. The raise precedes the damage mutation, so the
        // handler reads this sub-30% pre-hit fraction and emits the critical line.
        typeof(Station).GetProperty(nameof(Station.ArmorFraction))!
            .SetValue(seat.Station, 0.25f);
        seat.Station.LastDamageReport = double.NegativeInfinity;   // reopen the 10 s window

        FireAtStation(seat);
        int got2 = PumpUntil(seat.Server, () => seat.DefenderBot.Chats.Count == 2, 6000);
        Assert.True(got2 < 6000, "sub-30% hit after the window must produce a second alert");

        var crit = seat.DefenderBot.Chats[1];
        Assert.Equal((int)ChatTarget.Team, crit.ChatTarget);
        Assert.Equal(seat.Station.Type.CriticalSound, crit.VoiceOver);
        Assert.Contains("critically damaged", crit.Message);
        Console.WriteLine($"P451-CRITICAL: defender got critical alert '{crit.Message}' (voice {crit.VoiceOver})");
    }

    /// <summary>⭐ P846 — the native #ALLY branches of
    /// <c>DamageStationEvent</c> (<c>FedSrv.CPP:12192-12229</c>). When the
    /// launcher's side is ALLIED with the station's side (a DIFFERENT side, so the
    /// alert still fires) native:
    /// <list type="number">
    /// <item>prefixes the launcher with <c>(Ally)</c> in the alert text;</item>
    /// <item>sends it as <c>CHAT_ALLIES</c> (the defending side's ally group), so
    /// the attacker's side receives it too;</item>
    /// <item>whispers the private <c>"Watch your fire"</c> to the allied player
    /// launcher.</item>
    /// </list>
    /// Pre-P846 the port had no allied leg at all: no prefix, no whisper, and the
    /// alert stayed a plain defending-side team line.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Allied_cross_side_station_hit_adds_ally_prefix_and_watch_your_fire()
    {
        Require.Artwork();
        using var seat = Seat();
        var mission = seat.Server.Missions[0];
        mission.AllowFriendlyFire = true;                 // native allied damage lands with FF on
        foreach (var side in mission.Sides) side.AllyGroup = 1;   // production alliance model (Side.AllyGroup)
        seat.AttackerBot.ResetChatReceived();
        seat.DefenderBot.ResetChatReceived();

        FireAtStation(seat);

        int got = PumpUntil(seat.Server, () => seat.DefenderBot.Chats.Any(c => c.Message.Contains("under attack")), 6000);
        Assert.True(got < 6000, "defender never received the allied under-attack alert");

        var alert = seat.DefenderBot.Chats.Single(c => c.Message.Contains("under attack"));
        Assert.Equal((int)ChatTarget.Allies, alert.ChatTarget);      // ct = CHAT_ALLIES when the side has allies
        Assert.Contains("under attack by (Ally) ", alert.Message);
        // CHAT_ALLIES audience: the allied attacker's side sees the alert too.
        PumpUntil(seat.Server, () => seat.AttackerBot.Chats.Any(c => c.Message.Contains("under attack")), 3000);
        Assert.Contains(seat.AttackerBot.Chats, c => c.Message.Contains("under attack by (Ally) "));

        PumpUntil(seat.Server, () => seat.AttackerBot.Chats.Any(c => c.Message.Contains("Watch your fire")), 3000);
        var warn = seat.AttackerBot.Chats.Single(c => c.Message.Contains("Watch your fire"));
        Assert.Equal((int)ChatTarget.Individual, warn.ChatTarget);
        Assert.Equal(seat.Attacker.Id, warn.OidRecipient);
        Assert.Equal(-1, warn.SidSender);                 // the station is the sender (NA)
        Console.WriteLine($"P846-ALLY-BASE: '{alert.Message}' (ALLIES) + '{warn.Message}' to the allied launcher");
    }
}

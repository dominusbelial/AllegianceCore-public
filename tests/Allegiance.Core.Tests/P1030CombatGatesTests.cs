using System.Reflection;
using System.Runtime.ExceptionServices;
using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

/// <summary>
/// P1030 — R1b F5: the three combat input handlers that bypassed the port's
/// mount/load/ripcord/lock authority. Every test drives the production inbound
/// dispatcher (<c>TestProcessDatagram</c>) with the bytes a remote peer would
/// send, against a real static core, mission and sector, exactly like
/// <see cref="R1ReviewTests"/>. Native evidence: <c>Igc/dispenserigc.cpp:84-123</c>,
/// <c>Igc/magazineigc.cpp:85-185</c>, <c>Igc/shipIGC.h:1490-1512</c>,
/// <c>FedSrv/FedSrv.CPP:5615-5780,5408-5510,6094-6152</c>.
/// </summary>
[Trait("Requires", "Artwork")]
[Collection("LiveNetwork")]
public sealed class P1030CombatGatesTests
{
    /// <summary>A seeded in-space combat world: two sides, real ships placed in
    /// the map's first sector, every egress frame captured.</summary>
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new();
        public readonly ServerHost Host = new();
        public readonly ServerConfig Config = new()
        {
            StaticCore = "PCore001.igc",
            Games = [new GameConfig { MaxTeamImbalance = 0x7fff, MinimumPlayersPerTeam = 1 }, new GameConfig()],
        };
        public readonly Mission Mission;
        public readonly StaticCore Core;
        public readonly Ship Owner, Mate, Enemy;
        public readonly List<(ConnectionId Peer, byte[] Msg)> Sent = [];
        public readonly ConnectionId A = new(1), B = new(2), C = new(3);

        public World()
        {
            Require.Artwork();
            Core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "PCore001.igc")));
            var map = MissionMap.Load(IgcReader.ParseFile(Path.Combine(TestArtRoot.Value, "XXV_simple.igc")));
            Mission = Mission.Assemble(Core, map, [new SideSpec(0, "Alpha", 1), new SideSpec(1, "Bravo", 3)], Host);
            Mission.Stage = MissionStage.Started;
            Host.Transport = new TcpTransport(Host);
            typeof(TcpTransport).GetField("_isHost", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Host.Transport, true);
            Set("_host", Host); Set("_config", Config); Set("_missions", new List<Mission> { Mission });
            var hull = CombatHull();
            Owner = Place(100, 0, hull);
            Mate = Place(101, 0, hull);
            Enemy = Place(102, 1, hull);
            Server.TestSeedPeers([(A, Owner, Owner.Sector!), (B, Mate, Mate.Sector!), (C, Enemy, Enemy.Sector!)]);
            Mission.MissionOwnerSideId = 0; Mission.Sides[0].LeaderShipId = Owner.Id; Mission.Sides[1].LeaderShipId = Enemy.Id;
            Server._testSendCapture = (c, p) => Sent.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
            Set("_simNow", 0d);
        }

        /// <summary>A hull that can carry a full mine/probe/chaff/missile reserve
        /// (the real corpus hulls clamp a launcher to a handful of rounds).</summary>
        public DataHullTypeIGC CombatHull() =>
            Core.HullTypes.Values.First(h => h.GroupId >= 0 && !h.HasCapability(DataHullTypeIGC.HabmLifepod))
                with { CapacityDispenser = 30000, CapacityChaffLauncher = 30000, CapacityMagazine = 30000, MaxAmmo = 100 };

        public Ship Place(short id, short side, DataHullTypeIGC hull)
        {
            var ship = new Ship(id, hull, new Vector3(5000 + id, 5000, 5000), Vector3.Zero, Orientation.Identity) { SideId = side };
            Mission.PlaceShip(ship, Mission.Sectors[0]);
            return ship;
        }

        public void Set(string field, object value) =>
            typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Server, value);
        public T Get<T>(string field) =>
            (T)typeof(Srv).GetField(field, BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Server)!;
        public object? Call(string method, params object?[] args)
        {
            try { return typeof(Srv).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(Server, args); }
            catch (TargetInvocationException e) { ExceptionDispatchInfo.Capture(e.InnerException!).Throw(); throw; }
        }

        public void Send(ConnectionId from, IFedMessage message) =>
            Server.TestProcessDatagram(from, FedPacket.Pack([message.Encode()]));
        /// <summary>One wire packet carrying <paramref name="count"/> copies of a message.</summary>
        public void SendBurst(ConnectionId from, IFedMessage message, int count) =>
            Server.TestProcessDatagram(from, FedPacket.Pack(Enumerable.Repeat(message.Encode(), count)));
        public IEnumerable<(ConnectionId Peer, byte[] Msg)> Messages(ushort fmid) =>
            Sent.Where(s => FedMessageHeader.Read(s.Msg).Fmid == fmid);

        // ── part construction (construction-only launcher subtypes, no corpus record) ──
        public DataPartTypeIGC MineLauncherPart(short partId = 750) => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "mine", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MineLauncher(Core.MineTypes.Keys.First()));

        public DataPartTypeIGC ChaffLauncherPart(short partId = 751) => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "chaff", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.ChaffLauncher(Core.ChaffTypes.Keys.First()));

        public DataMissileTypeIGC LockMissile() => Core.MissileTypes.Values
            .Where(m => m.MaxLock > 0f && m.LockTime > 0f && m.InitialSpeed > 0f && m.Lifespan >= 1f)
            .OrderBy(m => m.ExpendableTypeId).First();

        public DataPartTypeIGC MissileLauncherPart(short partId = 752) => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "ml", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0f, Signature: 0f,
            PartId: partId, SuccessorPartId: -1, PartMask: 0, InventoryLineMdl: "",
            Type: new DataPartTypeIGC.MissileLauncher(LockMissile().ExpendableTypeId));

        /// <summary>The core's stackable ammo pack with a permissive mask (a real
        /// <c>Pack{ PackType: c_packAmmo }</c>). Registered back into the core so
        /// the handler's <c>Mission.Core.PartTypes</c> lookup resolves the same
        /// permissive record (native <c>pMission-&gt;GetPartType</c>).</summary>
        public DataPartTypeIGC Pack()
        {
            var pack = Core.PartTypes.Values.First(p => p.Type is DataPartTypeIGC.Pack { PackType: 0 })
                with { PartMask = 0, SuccessorPartId = -1 };
            ((Dictionary<short, DataPartTypeIGC>)Core.PartTypes)[pack.PartId] = pack;
            return pack;
        }

        public MountedPart Mount(Ship ship, DataPartTypeIGC type, short amount, byte fraction = ExpandedPartData.FullyMounted)
            => ship.MountPart(type, amount, 0, fraction)!;

        public void Dispose() { Host.Transport!.Shutdown(); Server.Dispose(); }
    }

    private static int Mines(World w) => w.Owner.Sector!.Models.OfType<Mine>().Count();
    private static MountedPart Mounted(World w, EquipmentType et, Ship? ship = null) =>
        (ship ?? w.Owner).Parts.Single(p => p.EquipmentType == et && p.MountId == 0);

    // ── F5.1: FM_C_FIRE_EXPENDABLE bypassed mount/load/ripcord/cooldown ──────

    /// <summary>F5.1 — one UDP packet carrying 30 FIRE_EXPENDABLE(82) requests
    /// used to deploy the whole mine reserve in a single tick: FireDispenser had
    /// none of UpdateDispenser's mount/load/ripcord gates, so each copy fired.
    /// Native's client dispenser refuses while <c>m_mountedFraction &lt; 1</c> or
    /// <c>m_timeLoaded &gt;= now</c> (dispenserigc.cpp:92-107) and arms the
    /// deadline before the callback (:110).</summary>
    [SkippableFact]
    public void One_packet_of_30_fire_expendable_requests_deploys_one_round()
    {
        using var w = new World();
        var part = w.Mount(w.Owner, w.MineLauncherPart(), 8);
        w.SendBurst(w.A, new FireExpendableRequest { Et = EquipmentType.Dispenser }, 30);
        Assert.Equal(1, Mines(w));
        Assert.Equal((short)7, Mounted(w, EquipmentType.Dispenser).Amount);
        Assert.NotEmpty(w.Messages(FireExpendable.Fmid));   // one relay for the one deployment
    }

    /// <summary>F5.1 — a dispenser still mounting (MountedFraction &lt; 1) must not
    /// fire from a wire request (native <c>m_mountedFraction == 1.0f</c>,
    /// dispenserigc.cpp:92-100; undocking starts every part at a fraction).</summary>
    [SkippableFact]
    public void Fire_expendable_is_refused_while_the_dispenser_is_still_mounting()
    {
        using var w = new World();
        var part = w.Mount(w.Owner, w.MineLauncherPart(), 4, fraction: 0);
        Assert.True(part.MountedFraction < 1f);
        w.Send(w.A, new FireExpendableRequest { Et = EquipmentType.Dispenser });
        Assert.Equal(0, Mines(w));
        Assert.Equal((short)4, Mounted(w, EquipmentType.Dispenser).Amount);
    }

    /// <summary>F5.1 — the load deadline is enforced with the port's 0.5 s wire
    /// tolerance (native <c>m_timeLoaded &lt; now</c>, dispenserigc.cpp:103;
    /// FedSrv.CPP:5433-5434 for the magazine's matching early-shot rule), and
    /// the cooldown then re-arms so the legitimate fire rate is unchanged.</summary>
    [SkippableFact]
    public void Fire_expendable_obeys_the_load_deadline_and_rearms()
    {
        using var w = new World();
        w.Mount(w.Owner, w.MineLauncherPart(), 5);
        double now = w.Mission.LastUpdate;
        double load = w.Core.MineTypes.Values.First().LoadTime;
        Assert.True(load >= 1f);

        // 1 s early: outside the tolerance, refused (no deployment, no ammo).
        Assert.Null(w.Owner.FireDispenser(Mounted(w, EquipmentType.Dispenser), now - 1.0));
        Assert.Equal((short)5, Mounted(w, EquipmentType.Dispenser).Amount);
        // 0.4 s early: inside the tolerance, the shot succeeds and arms from the
        // pending deadline (it is never shortened).
        Assert.NotNull(w.Owner.FireDispenser(Mounted(w, EquipmentType.Dispenser), now - 0.4));
        Assert.Equal((short)4, Mounted(w, EquipmentType.Dispenser).Amount);
        Assert.Equal(load, Mounted(w, EquipmentType.Dispenser).TimeLoaded, 5);
        // The re-armed deadline refuses every request until it passes.
        Assert.Null(w.Owner.FireDispenser(Mounted(w, EquipmentType.Dispenser), now + load - 0.6));
        Assert.Equal((short)4, Mounted(w, EquipmentType.Dispenser).Amount);
        Assert.NotNull(w.Owner.FireDispenser(Mounted(w, EquipmentType.Dispenser), now + load));
        Assert.Equal((short)3, Mounted(w, EquipmentType.Dispenser).Amount);
    }

    /// <summary>F5.1 — mines/probes are refused mid-ripcord while chaff is not
    /// (native <c>(et == ET_ChaffLauncher) || GetRipcordModel() == NULL</c>,
    /// dispenserigc.cpp:108-110).</summary>
    [SkippableFact]
    public void Ripcord_refuses_mines_but_allows_chaff_over_the_wire()
    {
        using var w = new World();
        w.Mount(w.Owner, w.MineLauncherPart(), 3);
        w.Mount(w.Owner, w.ChaffLauncherPart(), 3);
        w.Owner.SetRipcordModel(new Ship.RipcordTarget(1, 5, 0));

        w.Send(w.A, new FireExpendableRequest { Et = EquipmentType.Dispenser });
        Assert.Empty(w.Owner.Sector!.Models.OfType<Mine>());
        Assert.Equal((short)3, Mounted(w, EquipmentType.Dispenser).Amount);

        w.Send(w.A, new FireExpendableRequest { Et = EquipmentType.ChaffLauncher });
        Assert.Contains(w.Owner.Sector.Models.OfType<Chaff>(), _ => true);
        Assert.Equal((short)2, Mounted(w, EquipmentType.ChaffLauncher).Amount);
    }

    /// <summary>F5.1 — a dead ship and a ship with no real side cannot fire a
    /// dispenser (native's handler resolves a live, sided ship,
    /// FedSrv.CPP:5428,5615-5656; the mine/probe carry <c>GetSide()</c>).</summary>
    [SkippableTheory]
    [InlineData("dead")]
    [InlineData("lobby")]
    public void Dead_or_lobbied_ships_cannot_fire_expendables(string gate)
    {
        using var w = new World();
        w.Mount(w.Owner, w.MineLauncherPart(), 3);
        if (gate == "dead") w.Owner.SetFraction(0f); else w.Owner.SideId = -2;
        w.Send(w.A, new FireExpendableRequest { Et = EquipmentType.Dispenser });
        Assert.Empty(w.Owner.Sector!.Models.OfType<Mine>());
        Assert.Equal((short)3, Mounted(w, EquipmentType.Dispenser).Amount);
    }

    // ── F5.2: FM_CS_FIRE_MISSILE bypassed mount readiness, dud deadline and lock ──

    /// <summary>F5.2 — firing while the rack is still mounting used to create a
    /// LIVE missile and overwrite <c>TimeLoaded</c> with <c>now + LoadTime</c>,
    /// shortening the mount deadline so the next shot fired armed before the rack
    /// came up. Native the magazine's own Update fires only once
    /// <c>m_mountedFraction == 1</c> (magazineigc.cpp:85-103), and the server
    /// marks an early player request a dud without shortening a pending
    /// <c>TimeLoaded</c> (FedSrv.CPP:5433-5468).</summary>
    [SkippableFact]
    public void Fire_missile_while_the_rack_is_mounting_is_a_dud_and_keeps_the_mount_deadline()
    {
        using var w = new World();
        w.Mount(w.Owner, w.MissileLauncherPart(), 9, fraction: 0);
        var launcher = Mounted(w, EquipmentType.Magazine);
        double mountDeadline = launcher.TimeLoaded;
        Assert.True(mountDeadline > w.Mission.LastUpdate + 0.5, "the rack must still be mounting");

        w.Send(w.A, new FireMissile { Lock = 1f });
        var missile = Assert.Single(w.Owner.Sector!.Models.OfType<Missile>());
        Assert.True(missile.IsDud);
        Assert.Equal(0f, missile.LockFraction);
        Assert.Equal((short)8, Mounted(w, EquipmentType.Magazine).Amount);
        Assert.True(Mounted(w, EquipmentType.Magazine).TimeLoaded >= mountDeadline,
            $"a dud shot shortened the mount deadline ({Mounted(w, EquipmentType.Magazine).TimeLoaded} < {mountDeadline})");
    }

    /// <summary>F5.2 — the server's own per-tick lock tracking bounds the lock a
    /// player claims. With no tracked lock the reported 1.0 fires at zero lock;
    /// once the server tracks a full lock (UpdateMountedMissileLock,
    /// magazineigc.cpp:104-176) the same request produces a fully locked missile.</summary>
    [SkippableFact]
    public void Fire_missile_lock_is_bounded_by_the_server_tracked_lock()
    {
        using var w = new World();
        var mt = w.LockMissile();
        w.Mount(w.Owner, w.MissileLauncherPart(), 9);
        var target = w.Place(103, 1, w.CombatHull());
        target.Position = w.Owner.Position + new Vector3(300, 0, 0);
        target.SideVisibility.SetSideVisibility(0, true);
        w.Owner.Orientation.Set((target.Position - w.Owner.Position).Normalize());
        w.Owner.CommandTarget = target;

        // No tick has built a lock yet: the claimed 1.0 must not become a lock.
        Assert.Equal(0f, w.Owner.MissileLock);
        w.Send(w.A, new FireMissile { Lock = 1f });
        var untracked = w.Owner.Sector!.Models.OfType<Missile>().First();
        Assert.Equal(0f, untracked.LockFraction);

        // The server builds its own lock against the same command target; an
        // honest client (which echoes the broadcast lock) gets the full lock.
        w.Owner.UpdateMissileLock(mt, mt.LockTime);
        Assert.True(w.Owner.MissileLock >= 0.999f, $"the server must track a full lock (got {w.Owner.MissileLock})");
        w.Send(w.A, new FireMissile { Lock = 1f });
        Assert.Equal(mt.MaxLock, w.Owner.Sector.Models.OfType<Missile>().Last().LockFraction, 4);
    }

    /// <summary>F5.2 — a live missile requires an alive ship on a real side
    /// (native's <c>dm.pLauncher</c> is the player's flying, sided ship,
    /// FedSrv.CPP:5416-5426).</summary>
    [SkippableTheory]
    [InlineData("dead")]
    [InlineData("lobby")]
    public void Dead_or_lobbied_ships_cannot_fire_missiles(string gate)
    {
        using var w = new World();
        w.Mount(w.Owner, w.MissileLauncherPart(), 3);
        if (gate == "dead") w.Owner.SetFraction(0f); else w.Owner.SideId = -2;
        w.Send(w.A, new FireMissile { Lock = 0f });
        Assert.Empty(w.Owner.Sector!.Models.OfType<Missile>());
        Assert.Equal((short)3, Mounted(w, EquipmentType.Magazine).Amount);
    }

    // ── F5.3: FM_C_TREASURE_ACK armed a pickup instantly and lost cargo stacks ──

    /// <summary>F5.3 — a part accepted into an active mount used to arrive fully
    /// mounted (instant shield/weapon arm-up). Native <c>CreateAndAddPart</c>
    /// leaves <c>m_mountedFraction = 0.0f</c> (Igc/shipIGC.h:1490-1512); only the
    /// dock BuyLoadout arms a part.</summary>
    [SkippableFact]
    public void A_part_accepted_into_an_active_mount_starts_unmounted()
    {
        using var w = new World();
        var pack = w.Pack();
        w.Owner.PendingTreasureOffer = (pack.PartId, 20);
        w.Send(w.A, new TreasureAck { MountId = 0 });

        var accepted = Assert.Single(w.Owner.Parts);
        Assert.Equal(pack.PartId, accepted.PartId);
        Assert.Equal(0, accepted.FractionMounted);
        Assert.True(accepted.MountedFraction < 1f, $"a salvage pickup arrived mounted ({accepted.MountedFraction})");
        Assert.NotEmpty(w.Messages(AddPart.Fmid));                    // ADD_PART(164) still broadcast
    }

    /// <summary>F5.3 — a stackable pickup acked into an occupied cargo slot used
    /// to search only <c>Parts</c>, miss the <c>_cargo</c> stack, be refused by
    /// MountPart's occupied-cargo guard and destroy the buffered offer. Native
    /// <c>GetMountedPart</c> reads <c>m_mountedCargos</c> for a negative mount
    /// (Igc/shipIGC.cpp:1516-1549) and merges the amount
    /// (FedSrv.CPP:6101-6103).</summary>
    [SkippableFact]
    public void A_stackable_pickup_into_an_occupied_cargo_slot_merges_the_cargo_stack()
    {
        using var w = new World();
        var pack = w.Pack();
        Assert.True(w.Owner.TryMountCargoPart(pack, 40, -1, 0));
        Assert.Equal((short)40, w.Owner.CargoHold[0]!.Amount);

        w.Owner.PendingTreasureOffer = (pack.PartId, 20);
        w.Send(w.A, new TreasureAck { MountId = unchecked((byte)(sbyte)-1) });

        Assert.Null(w.Owner.PendingTreasureOffer);
        Assert.Equal((short)60, w.Owner.CargoHold[0]!.Amount);       // merged into the cargo stack
        Assert.Empty(w.Owner.Parts);                                 // not mounted, not lost
        Assert.NotEmpty(w.Messages(AddPart.Fmid));                   // merged stack echoed to the sector
    }
}

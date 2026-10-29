using System.Reflection;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using FltBot = global::Allegiance.Bot.Bot;

namespace Allegiance.Core.Tests;

/// <summary>
/// P865b — SIM-0663: the carrier-relaunch autopilot re-task, the port of native
/// <c>LandOnCarrierEvent</c>'s tail (<c>FedSrv.CPP:13020-13028</c>, re-grepped):
/// after the RELAUNCH_SHIP send, an autopiloting lander whose ACCEPTED slot held
/// <c>c_cidGoto</c> onto the carrier it just landed on is re-tasked to
/// <c>c_cidDefend</c> that carrier (accepted slot always; the plan slot too for
/// a player-piloted lander), and every other autopiloting lander has its plan
/// cleared so a drone re-decides from the accepted order it keeps ("drones will
/// do c_cmdAccepted"). The whole point is observable: without the Goto→Defend
/// conversion, a carrier-bound lander would fly straight back into the bay it
/// just relaunched from and re-land forever. Expressed in the port's order
/// model — <see cref="Ship.AcceptedShipTarget"/>/<see cref="Ship.AcceptedShipCommandId"/>
/// (P760b), <see cref="Ship.DefendTarget"/> (P166) and the plan-slot content
/// (<see cref="Ship.PlanTarget"/>, P403 <see cref="Ship.GotoWaypoint"/>, the
/// P652 retreat and P767 ammo legs via <c>ClearRelaunchPlan</c>).
/// Real in-process server, real carrier bay geometry (the LiveRelaunchTests
/// recipe), no bots needed — the assertions are server-side order state.
/// </summary>
[Trait("Requires", "Artwork")]
public sealed class P865bCarrierRetaskTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    // XXV_simple (measured, P111): 1052 = no-station fly sector.
    private const short FlySector = 1052;

    // Real carrier-local inside point for the fig22 lander in cap502's bay
    // (measured in P117/LiveRelaunchTests).
    private static readonly Vector3 Cap502Inside = new(-60f, -32f, 70f);

    private static Cfg Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "P865b Carrier Retask", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static LogonRequest Request(string name) => new()
    {
        CharacterName = name, ZoneTicket = [0x01, 0x02, 0x03, 0x04],
        CDKey = "ABCDEFGHIJKL", MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer, Time = 865u, DwCookie = uint.MaxValue,
        CrcFileList = -1, SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0, SteamID = 86500000000000001ul, DrmHash = "p865b",
    };

    private sealed class Spawn { public Ship Carrier = null!, Lander = null!, BotShip = null!; }

    /// <summary>One seated peer (its ship parks in another sector), plus a real
    /// cap502 carrier and a fig22 lander hull (cloned with exactly
    /// <c>c_habmLandOnCarrier</c>) constructed directly into the fly sector —
    /// far apart, so no landing triggers until the test parks the lander. The
    /// lander is an NPC (pilotType Builder — a builder with no configured
    /// station type stands still, so the parked geometry holds) unless
    /// <paramref name="npc"/> is false.</summary>
    private static Spawn Inject(Srv server, bool npc)
    {
        var sp = new Spawn();
        server.ShipSpawner = (core, mission, shipId) =>
        {
            var home = mission.Sectors.First(s => s.StationRuntimes.Any());
            sp.BotShip = new Ship(shipId, core.HullTypes.Values.First(h => h.HasCapability(DataHullTypeIGC.HabmFighter)),
                new Vector3(3000f, 3000f, 3000f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
            mission.PlaceShip(sp.BotShip, home);
            return (sp.BotShip, home);
        };
        return sp;
    }

    private static (Srv server, Spawn sp) Boot(bool npc)
    {
        var server = new Srv();
        var sp = Inject(server, npc);
        server.Boot(Config());
        foreach (var __m in server.Missions) __m.Stage = MissionStage.Started;

        var bot = new FltBot();
        bot.Connect(server.BoundPort, "OBS");
        bot.SendLogonRequest(Request("OBS"));
        int n = 0;
        while (bot.ShipAssignment is null && n++ < 4000) { server.TickSim(); Thread.Sleep(1); }
        Assert.NotNull(sp.BotShip);

        var mission = server.Missions[0];
        var fly = mission.Sectors.First(s => s.Id == FlySector);
        sp.Carrier = new Ship(9500,
            mission.Core.HullTypes.Values.First(h => h.ModelName == "cap502"),
            new Vector3(500f, 950f, 930f), Vector3.Zero, Orientation.Identity) { SideId = 0 };
        mission.PlaceShip(sp.Carrier, fly);
        var fig = mission.Core.HullTypes.Values.First(h => h.ModelName == "fig22");
        sp.Lander = new Ship(9501,
            fig with { HabmCapabilities = DataHullTypeIGC.HabmLandOnCarrier },
            new Vector3(500f, 1350f, 930f), Vector3.Zero, Orientation.Identity)
        { SideId = 0, IsNpc = npc, PilotType = Ship.PilotTypeBuilder };
        mission.PlaceShip(sp.Lander, fly);
        return (server, sp);
    }

    /// <summary>Park the lander inside the carrier's real bay and pump ticks
    /// (re-parking each tick so a stray steering nudge cannot spoil the
    /// geometry) until <paramref name="done"/> — the re-task's observable
    /// end-state — holds.</summary>
    private static void PumpLanding(Srv server, Spawn sp, Func<Ship, bool> done, int maxTicks = 200)
    {
        var bay = sp.Carrier.Sector!.LandingBays!.Bay(sp.Carrier.HullType);
        Assert.NotNull(bay);
        for (int i = 0; i < maxTicks; i++)
        {
            var lander = sp.Lander;
            lander.Position = sp.Carrier.Position + Cap502Inside;
            lander.Velocity = Vector3.Zero;
            server.TickSim();
            if (lander.StationId is null && (lander.Position - sp.Carrier.Position).Length() > 5f && done(lander))
                return;   // relaunched (moved out of the bay) AND re-tasked
        }
        Assert.Fail($"no carrier relaunch with the expected re-task within {maxTicks} ticks " +
            $"(final: pos=({sp.Lander.Position.X:F0},{sp.Lander.Position.Y:F0},{sp.Lander.Position.Z:F0}), " +
            $"stationId={sp.Lander.StationId}, acceptedCid={sp.Lander.AcceptedShipCommandId}, " +
            $"acceptedTarget={(sp.Lander.AcceptedShipTarget?.Id.ToString() ?? "null")}, " +
            $"defend={(sp.Lander.DefendTarget?.Id.ToString() ?? "null")}, goto={(sp.Lander.GotoWaypoint is null ? "null" : "set")})");
    }

    private static void Write(Ship ship, string field, object? value) =>
        Field(ship, field).SetValue(ship, value);

    private static object? Read(Ship ship, string field) =>
        Field(ship, field).GetValue(ship);

    private static FieldInfo Field(Ship ship, string field) =>
        typeof(Ship).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)
        ?? typeof(Ship).GetField($"<{field}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException($"field {field} not found");

    // ── the Goto branch — FedSrv.CPP:13021-13024 ─────────────────────────────

    /// <summary>⭐ SIM-0663 Goto branch — a lander whose ACCEPTED slot held
    /// c_cidGoto onto the carrier it just landed on is re-tasked to
    /// c_cidDefend that carrier: the accepted slot becomes (carrier, Defend),
    /// the plan slot becomes the defend (this port's DefendTarget), and the
    /// P403 goto plan is consumed. Without this the lander would re-land
    /// forever.</summary>
    [SkippableFact]
    public void Relaunch_retasks_an_accepted_goto_onto_the_carrier_to_defend()
    {
        Require.Artwork();
        var (server, sp) = Boot(npc: true);
        using var __ = server;
        var (carrier, lander) = (sp.Carrier, sp.Lander);

        lander.SetCommand(OrderChange.CmdAccepted, carrier, OrderChange.CidGoto);   // FedSrv.CPP:13021's condition
        lander.SetGoto(new GotoOrder(GotoKind.Cluster, FlySector, carrier.Position));

        PumpLanding(server, sp, s =>
            s.AcceptedShipCommandId == OrderChange.CidDefend && s.DefendTarget == carrier);

        Assert.Equal(OrderChange.CidDefend, lander.AcceptedShipCommandId);   // FedSrv.CPP:13022
        Assert.Same(carrier, lander.AcceptedShipTarget);
        Assert.Same(carrier, lander.DefendTarget);                           // the plan leg, :13023-13024
        Assert.Null(lander.GotoWaypoint);                                    // the plan slot holds the defend now
        Assert.Null(lander.StationId);                                       // relaunched, not docked
    }

    /// <summary>SIM-0663 — the plan leg for a PLAYER-piloted lander: native
    /// writes c_cmdPlan(c_cidDefend) only when pilotType ≥ c_ptPlayer
    /// (FedSrv.CPP:13023-13024); the port expresses that plan on
    /// DefendTarget as well, for exactly the same net flight.</summary>
    [SkippableFact]
    public void Player_piloted_relaunch_gets_the_defend_plan_leg()
    {
        Require.Artwork();
        var (server, sp) = Boot(npc: false);
        using var __ = server;
        var (carrier, lander) = (sp.Carrier, sp.Lander);

        lander.SetCommand(OrderChange.CmdAccepted, carrier, OrderChange.CidGoto);
        lander.SetGoto(new GotoOrder(GotoKind.Cluster, FlySector, carrier.Position));

        PumpLanding(server, sp, s =>
            s.AcceptedShipCommandId == OrderChange.CidDefend && s.DefendTarget == carrier);

        Assert.Equal(OrderChange.CidDefend, lander.AcceptedShipCommandId);
        Assert.Same(carrier, lander.DefendTarget);
        Assert.Null(lander.GotoWaypoint);
    }

    // ── the else branch — FedSrv.CPP:13026-13027 ─────────────────────────────

    /// <summary>⭐ SIM-0663 else branch — an autopiloting lander whose accepted
    /// slot does NOT name the carrier loses only its PLAN (here the P403 goto
    /// and a P652 damage retreat toward the carrier): the accepted order
    /// survives untouched so the drone re-decides from it ("drones will do
    /// c_cmdAccepted", FedSrv.CPP:13027). The retreat end matters observably:
    /// a damaged drone that landed on its retreat carrier must not fly straight
    /// back into the bay after the relaunch repaired it.</summary>
    [SkippableFact]
    public void Relaunch_clears_the_plan_but_keeps_the_accepted_order()
    {
        Require.Artwork();
        var (server, sp) = Boot(npc: true);
        using var __ = server;
        var (carrier, lander) = (sp.Carrier, sp.Lander);

        // A plan: an active P403 goto, plus the P652 damage-retreat state aimed
        // at this very carrier (private fields — the same reflection the other
        // NPC-state tests use). The accepted slot deliberately names the
        // carrier with a non-Goto command, so the Goto branch must NOT fire.
        lander.SetGoto(new GotoOrder(GotoKind.Cluster, FlySector, carrier.Position));
        Write(lander, "IsRunningAway", true);
        Write(lander, "_retreatRepairShip", carrier);
        Write(lander, "_orderBeforeRetreat", null);
        lander.SetCommand(OrderChange.CmdAccepted, carrier, OrderChange.CidAttack);

        PumpLanding(server, sp, s => s.GotoWaypoint is null && !s.IsRunningAway);

        Assert.Null(lander.GotoWaypoint);                                    // plan cleared (13027)
        Assert.False(lander.IsRunningAway);                                  // retreat ended (the plan-write resets, shipIGC.h:755-762)
        Assert.Null(Read(lander, "_retreatRepairShip"));
        Assert.Equal(OrderChange.CidAttack, lander.AcceptedShipCommandId);   // accepted survives — 13027's comment
        Assert.Same(carrier, lander.AcceptedShipTarget);
        Assert.Null(lander.DefendTarget);                                    // no defend order was issued
    }

    // ── the GetAutopilot gate — FedSrv.CPP:13020 ─────────────────────────────

    /// <summary>SIM-0663 gate — native re-tasks only an AUTOPILOTING lander
    /// (FedSrv.CPP:13020). A non-autopiloting player lander (this port's
    /// server-visible autopilot state is an active P403 goto) keeps both its
    /// accepted slot and its plan exactly as they were.</summary>
    [SkippableFact]
    public void Non_autopilot_lander_is_not_retaken()
    {
        Require.Artwork();
        var (server, sp) = Boot(npc: false);
        using var __ = server;
        var (carrier, lander) = (sp.Carrier, sp.Lander);

        lander.SetCommand(OrderChange.CmdAccepted, carrier, OrderChange.CidGoto);
        Assert.Null(lander.GotoWaypoint);   // no active goto ⇒ no server-visible autopilot

        PumpLanding(server, sp, s => (s.Position - sp.Carrier.Position).Length() > 5f);

        Assert.Equal(OrderChange.CidGoto, lander.AcceptedShipCommandId);   // untouched
        Assert.Null(lander.DefendTarget);
    }
}

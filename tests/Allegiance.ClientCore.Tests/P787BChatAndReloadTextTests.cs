using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Xunit;
using Orientation = Allegiance.Core.Sim.Orientation;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P787b — the two settled callback rows:
/// <list type="bullet">
/// <item><b>SIM-0172</b> — ReceiveChat's default-on unknown-VC filter
/// (native <c>WinTrek/trekigc.cpp:4494-4496</c>, <c>m_bFilterUnknownChats</c>
/// default true, trekigc.cpp:2464): a HUMAN sender's null-text sonic chat
/// whose caption is "Unknown chat" is dropped entirely — no event-log line, no
/// sound event, no callback. Drone senders and known captions pass, like
/// native (<c>ppi-&gt;IsHuman()</c> gate).</item>
/// <item><b>SIM-0195</b> — the resource-pool notification texts (native
/// <c>Igc/shipIGC.h:597-650</c>, posted through IIgcSite.PostNotificationText
/// and shown only for the local ship, trekigc.cpp:2994-3018): the port derives
/// the same four texts from client-observable state — the own ship's wire
/// ammo/fuel fraction 0-crossing arms a decision; a matching RELOAD(129) relay
/// resolves it as "Reloading ammo..."/"Reloading fuel..."; the 0.35 s grace
/// elapsing resolves it as "Ammo depleted."/"Fuel depleted.".</item>
/// </list>
/// </summary>
public sealed class P787BChatAndReloadTextTests
{
    internal static ClientWorld World()
    {
        var world = new ClientWorld();
        world.Apply(new SetCluster { SectorId = 1, Ship = ServerShipUpdate.FromValues(10, 0, default, 0, Allegiance.Core.Collision.Vector3.Zero,
            Orientation.Identity, Allegiance.Core.Collision.Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1) });
        world.Apply(new ExportShip { ShipId = 10, SideId = 0, CharacterName = "p787b" });
        world.Apply(new ExportShip { ShipId = -1, SideId = 0 });
        return world;
    }

    private static PlayerInfo Human(short shipId) => new() { ShipId = shipId, ISide = 0, CharacterName = "human" };
    private static PlayerInfo Drone(short shipId) => new() { ShipId = shipId, ISide = 0, CharacterName = "drone", DtidDrone = 7 };

    private static ChatMessage UnknownVc(short sender) => new()
    {
        SidSender = sender,
        ChatTarget = (int)ChatTarget.Everyone,
        VoiceOver = 990,                       // not in the sonic-chat catalog → "Unknown chat"
        CommandID = -1,
        MessageIsAbsent = true,
    };

    // ── SIM-0172 ──────────────────────────────────────────────────────────

    [Fact]
    public void Unknown_vc_from_a_human_sender_is_dropped_entirely()
    {
        var world = World();
        world.Apply(Human(11));
        world.Apply(new ChatMessage { Message = "seed" });   // so the single() below is meaningful
        world.TakeSoundEvents();

        world.Apply(UnknownVc(11));

        Assert.Single(world.Events.Chats);                   // no event-log line
        Assert.Empty(world.TakeSoundEvents());               // no sound event
        int calls = 0;
        world.ChatReceived += () => calls++;
        world.Apply(UnknownVc(11));
        Assert.Equal(0, calls);                              // no ReceiveChat callback either
        Assert.Single(world.Events.Chats);
    }

    [Fact]
    public void Unknown_vc_from_a_drone_sender_passes()
    {
        var world = World();
        world.Apply(Drone(11));
        world.TakeSoundEvents();

        world.Apply(UnknownVc(11));

        Assert.Single(world.Events.Chats);
    }

    [Fact]
    public void Known_sonic_caption_from_a_human_passes()
    {
        var world = World();
        world.Apply(Human(11));
        world.TakeSoundEvents();

        // Sonic chat 0 = "Goto aleph" — a real caption, filter never applies.
        world.Apply(new ChatMessage
        {
            SidSender = 11,
            ChatTarget = (int)ChatTarget.Everyone,
            VoiceOver = 0,
            CommandID = -1,
            MessageIsAbsent = true,
        });

        var chat = Assert.Single(world.Events.Chats);
        Assert.Equal("Goto aleph", chat.Message);            // caption substituted, line kept
    }

    // ── SIM-0195 ──────────────────────────────────────────────────────────

    private static uint _tick;

    private static SingleShipUpdate OwnUpdate(float ammoFrac, float fuelFrac) => new()
    {
        OidTarget = 10,
        Ship = ServerShipUpdate.FromValues(10, 0, ControlData.Neutral, ++_tick,
            Allegiance.Core.Collision.Vector3.Zero, Orientation.Identity, Allegiance.Core.Collision.Vector3.Zero,
            [0f, 0f, 0f], 1f, 1f, ammoFrac, fuelFrac, 1f, 0f, 1f),
    };

    [Fact]
    public void Ammo_zero_crossing_without_a_relay_posts_ammo_depleted_after_the_grace()
    {
        var world = World();
        double now = 100;
        world.NowSeconds = () => now;

        world.Apply(OwnUpdate(1f, 1f));
        world.UpdateOwnStatusAlarms();                       // first sample — never arms
        world.Apply(OwnUpdate(0f, 1f));
        world.UpdateOwnStatusAlarms();                       // crossing armed, window running
        Assert.Empty(world.TakeFeedbackLines());

        now += ClientWorld.ReloadFeedbackGraceSeconds - 0.01;
        world.UpdateOwnStatusAlarms();
        Assert.Empty(world.TakeFeedbackLines());             // still inside the grace

        now += 0.02;
        world.UpdateOwnStatusAlarms();
        Assert.Equal("Ammo depleted.", Assert.Single(world.TakeFeedbackLines()).Text);
    }

    [Fact]
    public void Ammo_zero_crossing_that_recovers_within_the_grace_posts_reloading_ammo()
    {
        var world = World();
        double now = 100;
        world.NowSeconds = () => now;

        world.Apply(OwnUpdate(1f, 1f));
        world.UpdateOwnStatusAlarms();
        world.Apply(OwnUpdate(0f, 1f));
        world.UpdateOwnStatusAlarms();          // crossing armed (native's SetAmmo(0) + reload attempt)

        // The pool refills synchronously on the server; the wire fraction
        // recovers inside the grace — native's success leg (shipIGC.h:614-619).
        now += 0.1;
        world.Apply(OwnUpdate(0.6f, 1f));
        world.UpdateOwnStatusAlarms();
        Assert.Equal("Reloading ammo...", Assert.Single(world.TakeFeedbackLines()).Text);

        now += ClientWorld.ReloadFeedbackGraceSeconds + 1;
        world.UpdateOwnStatusAlarms();
        Assert.DoesNotContain(world.TakeFeedbackLines(), l => l.Text == "Ammo depleted.");
    }

    [Fact]
    public void Fuel_zero_crossing_that_recovers_within_the_grace_posts_reloading_fuel()
    {
        var world = World();
        double now = 100;
        world.NowSeconds = () => now;

        world.Apply(OwnUpdate(1f, 1f));
        world.UpdateOwnStatusAlarms();
        world.Apply(OwnUpdate(1f, 0f));
        world.UpdateOwnStatusAlarms();

        now += 0.1;
        world.Apply(OwnUpdate(1f, 0.5f));
        world.UpdateOwnStatusAlarms();
        Assert.Equal("Reloading fuel...", Assert.Single(world.TakeFeedbackLines()).Text);
    }

    [Fact]
    public void Fuel_zero_crossing_without_a_relay_posts_fuel_depleted()
    {
        var world = World();
        double now = 100;
        world.NowSeconds = () => now;

        world.Apply(OwnUpdate(1f, 1f));
        world.UpdateOwnStatusAlarms();
        world.Apply(OwnUpdate(1f, 0f));
        world.UpdateOwnStatusAlarms();

        now += ClientWorld.ReloadFeedbackGraceSeconds + 0.01;
        world.UpdateOwnStatusAlarms();
        Assert.Equal("Fuel depleted.", Assert.Single(world.TakeFeedbackLines()).Text);
    }
}

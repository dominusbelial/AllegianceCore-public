using Allegiance.Core.Collision;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>HUD-6b — pins the DATA SOURCES behind the own-ship status readouts
/// and the native anchor/part-signature rules, not just the HudModel switches.</summary>
public sealed class Hud6bOwnStatusTests
{
    private static void Feed(ConnectionSession session, params IFedMessage[] messages) =>
        ((ITransportSite)session).OnReceive(ConnectionId.Server,
            FedPacket.Pack(messages.Select(m => m.Encode()).ToArray()));

    private static ServerShipUpdate Pose(short id) => ServerShipUpdate.FromValues(id, 0, default, 0,
        Vector3.Zero, Allegiance.Core.Sim.Orientation.Identity, Vector3.Zero, [0, 0, 0], 1, 1, 1, 1, 1, 0, 1);

    [Fact]
    public void Turret_gunner_reads_flag_artifact_and_signature_from_the_source_ship()
    {
        using var session = new ConnectionSession();
        Feed(session,
            new MissionDef { Stage = MissionStageMessage.StageStarted },
            new SideRoster { Sides = [new(0, 1, 2, "Alpha")] },
            new SetCluster { SectorId = 1, Ship = Pose(10) },
            new ExportShip { ShipId = 10, SideId = 0, HullId = 5, CharacterName = "Gunner" },
            new ExportShip { ShipId = 11, SideId = 0, HullId = 6, CharacterName = "Carrier" },
            new PlayerInfo { ShipId = 10, ISide = 0, CharacterName = "Gunner" },
            new PlayerInfo { ShipId = 11, ISide = 0, CharacterName = "Carrier" },
            // The gunner rides the carrier (ParentId), which carries a real flag.
            new ShipStatusMessage
            {
                ShipId = 10, SectorId = 1, HullId = 5, ParentId = 11,
                State = ShipStatusMessage.StateTurret, Detected = true
            },
            new ShipStatusMessage
            {
                ShipId = 11, SectorId = 1, HullId = 6, State = ShipStatusMessage.StateFlying,
                Detected = true
            },
            // The gunner's own entity carries the artifact; the carrier carries
            // side 1's flag. Native shows the SOURCE ship's state, so the HUD
            // must show FLAG and not ARTIFACT.
            new GainFlag { ShipidRecipient = 10, SideidFlag = Side.ArtifactSentinelFlag },
            new GainFlag { ShipidRecipient = 11, SideidFlag = 1 });

        var world = session.World;
        var own = world.Ships[10];
        var carrier = world.Ships[11];
        var status = OwnStatusData.FromClientState(own, world.Ships,
            autoPilot: true, ShipControlState.CoastButton);

        Assert.Same(carrier, status.SourceShip);
        Assert.True(status.AutopilotEngaged);          // trekClient.autoPilot() (console.cpp:793)
        Assert.True(status.VectorLockEngaged);         // StateM & CoastButton (consoledata.cpp:465)
        Assert.True(status.CarriedFlag);               // carrier's GetFlag() == 1
        Assert.False(status.CarriedArtifact);          // own artifact must not leak through

        var idle = OwnStatusData.FromClientState(own, world.Ships,
            autoPilot: false, stateM: 0);
        Assert.False(idle.AutopilotEngaged);
        Assert.False(idle.VectorLockEngaged);
        Assert.True(idle.CarriedFlag);
        Assert.False(idle.CarriedArtifact);
    }

    [Fact]
    public void Non_turret_ship_reads_its_own_flag_and_artifact()
    {
        using var session = new ConnectionSession();
        Feed(session,
            new MissionDef { Stage = MissionStageMessage.StageStarted },
            new SideRoster { Sides = [new(0, 1, 2, "Alpha")] },
            new SetCluster { SectorId = 1, Ship = Pose(10) },
            new ExportShip { ShipId = 10, SideId = 0, HullId = 5, CharacterName = "Scout" },
            new ShipStatusMessage
            {
                ShipId = 10, SectorId = 1, HullId = 5, ParentId = -1,
                State = ShipStatusMessage.StateFlying, Detected = true
            },
            new GainFlag { ShipidRecipient = 10, SideidFlag = Side.ArtifactSentinelFlag });

        var world = session.World;
        var own = world.Ships[10];
        var status = OwnStatusData.FromClientState(own, world.Ships, false, 0);
        Assert.Same(own, status.SourceShip);
        Assert.False(status.CarriedFlag);
        Assert.True(status.CarriedArtifact);
    }

    [Fact]
    public void Signature_uses_native_active_part_rules_and_the_cloak_factor()
    {
        // Factory: hull 10, shield 3, launcher 6, weapon 5, afterburner 4, cloak 2.
        var parts = new Dictionary<short, DataPartTypeIGC>
        {
            [1] = Part(1, EquipmentType.Weapon, 5),
            [2] = Part(2, EquipmentType.Shield, 3),
            [3] = Part(3, EquipmentType.Afterburner, 4),
            [4] = Part(4, EquipmentType.Cloak, 2),
            [5] = Part(5, EquipmentType.Magazine, 6),
            [6] = Part(6, EquipmentType.Weapon, 9),   // cargo, must never count
        };
        var loadout = new List<ExpandedPartData>
        {
            new(1, 1, 1, ExpandedPartData.FullyMounted),    // weapon, fixed mount 1
            new(2, 1, 0, ExpandedPartData.FullyMounted),    // shield, always
            new(3, 1, 3, ExpandedPartData.FullyMounted),    // afterburner
            new(4, 1, 4, ExpandedPartData.FullyMounted),    // cloak
            new(5, 1, 0, ExpandedPartData.FullyMounted),    // launcher, always
            new(6, 1, 255, ExpandedPartData.FullyMounted),  // cargo (signed mount -1)
        };
        DataPartTypeIGC? Resolve(short id) => parts.TryGetValue(id, out var p) ? p : null;

        float? Percent(bool firing, bool group, int mount, bool ab, bool cloak) =>
            OwnStatusData.SignaturePercent(10, loadout, Resolve, firing, group, mount, ab, cloak, 50f);

        // 10 hull + 3 shield + 6 launcher = 19 -> 38%; inactive weapon,
        // afterburner, cloak and the cargo weapon contribute nothing.
        Assert.Equal(38f, Percent(false, false, 0, false, false));
        // Group fire activates the mounted weapon: +5 -> 48%.
        Assert.Equal(48f, Percent(true, group: true, mount: 0, ab: false, cloak: false));
        // Single fire only activates the selected fixed mount (weapon at mount 1).
        Assert.Equal(38f, Percent(true, group: false, mount: 0, ab: false, cloak: false));
        Assert.Equal(48f, Percent(true, group: false, mount: 1, ab: false, cloak: false));
        // Afterburner adds only while active: +4 -> 46%.
        Assert.Equal(46f, Percent(false, false, 0, ab: true, cloak: false));
        // Cloak multiplies the whole signature by GetCloaking() (shipIGC.h:268-270);
        // the wire only exposes the engaged bit, whose stable end state is 0.
        Assert.Equal(0f, Percent(false, false, 0, ab: false, cloak: true));
        // A side with no c_gaSignature leaves the readout unknown.
        Assert.Null(OwnStatusData.SignaturePercent(10, loadout, Resolve, false, false, 0, false, false, 0f));
    }

    [Theory]
    // Native dialog.mdl:2607-2621 TranslateImage points, as top-left corners.
    [InlineData("Kill bonus", 281, 102)]
    [InlineData("Signature percentage", 281, 133)]
    [InlineData("Detection eye", 390, 55)]
    [InlineData("Cloak active", 120, 80)]
    [InlineData("Autopilot status", 177, 342)]
    [InlineData("Vector lock status", 442, 337)]
    [InlineData("Carried flag", 427, 350)]
    [InlineData("Carried artifact", 159, 350)]
    public void Status_anchor_is_the_native_top_left_corner(string id, int nativeX, int nativeY)
    {
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
        {
            float spread = OwnStatusLayout.Spread(h);
            var p = OwnStatusLayout.ScreenPos(id, w, h);
            Assert.Equal(w / 2f + (nativeX - 320) * spread, p.X, 3);
            Assert.Equal(h / 2f + (nativeY - 240) * spread, p.Y, 3);
        }
    }

    [Fact]
    public void Kill_counter_uses_the_native_experience_group_inner_offsets()
    {
        float spread = OwnStatusLayout.Spread(1080);
        var anchor = OwnStatusLayout.ScreenPos("Kill bonus", 1920, 1080);
        Assert.Equal(new Microsoft.Xna.Framework.Vector2(-23 * spread, -65 * spread),
            OwnStatusLayout.KillCounterBitmapOffset(1080));
        Assert.Equal(new Microsoft.Xna.Framework.Vector2(0, 5 * spread),
            OwnStatusLayout.KillCounterNumberOffset(1080));
        Assert.Equal(1920 / 2f - 39 * spread, anchor.X, 3);
        Assert.Equal(1080 / 2f - 138 * spread, anchor.Y, 3);
    }

    private static DataPartTypeIGC Part(short id, EquipmentType type, float signature)
        => new(
            Price: 0, TimeToBuild: 0, ModelName: "", IconName: "", Name: "", Description: "",
            GroupId: 0, TtbmRequired: [], TtbmEffects: [], Mass: 0, Signature: signature,
            PartId: id, SuccessorPartId: 0, PartMask: 0, InventoryLineMdl: "",
            Type: type switch
            {
                EquipmentType.Weapon => new DataPartTypeIGC.Weapon(0, 0, 0, 0, 0, 0, 0, 0, 0),
                EquipmentType.Shield => new DataPartTypeIGC.Shield(0, 0, 0, 0, 0),
                EquipmentType.Cloak => new DataPartTypeIGC.Cloak(0, 0, 0, 0, 0, 0),
                EquipmentType.Afterburner => new DataPartTypeIGC.Afterburner(0, 0, 0, 0, 0, 0),
                EquipmentType.Magazine => new DataPartTypeIGC.MissileLauncher(0),
                _ => throw new ArgumentOutOfRangeException(nameof(type)),
            });
}

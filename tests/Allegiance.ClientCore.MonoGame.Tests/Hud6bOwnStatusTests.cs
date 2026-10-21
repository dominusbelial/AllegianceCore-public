using System.IO;
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

    /// <summary>FONT-1c — the native DefaultCenterHudOn (dialog.mdl:2604-2623)
    /// TranslateImage points, each citing the line it comes from. The pane is
    /// 640x480 with a y-UP origin at its BOTTOM-left (engine/context.cpp:552;
    /// the same rule as HUD-14b's gauges, ClassicGaugeSpec.ConsoleOffset), so
    /// the centre-relative screen anchor is (x-320, 240-y).</summary>
    public static IEnumerable<object[]> NativeStatusAnchors =>
    [
        // dialog.mdl:2617 TranslateImage(ExperienceImage, Point(281, 102))
        new object[] { "Kill bonus", 281, 102, 2617 },
        // dialog.mdl:2621 TranslateImage(SignatureImage, Point(281, 133))
        new object[] { "Signature percentage", 281, 133, 2621 },
        // dialog.mdl:2618 TranslateImage(PaneImage(RadarDetectPane, ...), Point(390, 55))
        new object[] { "Detection eye", 390, 55, 2618 },
        // dialog.mdl:2609 TranslateImage(CloakOn, Point(120, 80))
        new object[] { "Cloak active", 120, 80, 2609 },
        // dialog.mdl:2608 TranslateImage(cortAutopilotOnImage, Point(177, 342))
        new object[] { "Autopilot status", 177, 342, 2608 },
        // dialog.mdl:2607 TranslateImage(cortVectorLockOn, Point(442, 337))
        new object[] { "Vector lock status", 442, 337, 2607 },
        // dialog.mdl:2619 TranslateImage(CaptureFlag, Point(427, 350))
        new object[] { "Carried flag", 427, 350, 2619 },
        // dialog.mdl:2620 TranslateImage(Artifact, Point(159, 350))
        new object[] { "Carried artifact", 159, 350, 2620 },
    ];

    [Theory]
    [MemberData(nameof(NativeStatusAnchors))]
    public void Status_anchor_is_the_native_translate_point(
        string id, int nativeX, int nativeY, int mdlLine)
    {
        Assert.InRange(mdlLine, 2604, 2623);
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440) })
        {
            float spread = OwnStatusLayout.Spread(h);
            var p = OwnStatusLayout.ScreenPos(id, w, h);
            // X = consoleX-320; Y = 240-consoleY (native y-up pane).
            Assert.Equal(w / 2f + (nativeX - 320) * spread, p.X, 3);
            Assert.Equal(h / 2f + (240 - nativeY) * spread, p.Y, 3);
        }
    }

    [Fact]
    public void Status_anchors_are_not_y_flipped_about_the_viewport_centre()
    {
        // FONT-1c fix 1 — the four cluster elements the review measured. With
        // the y-up rule SIG/medal sit BELOW the reticle (positive screen y), as
        // do the low pane-corner eye (console y=55) and cloak (y=80), exactly
        // as native dialog.mdl:2609-2621 places them in the 640x480 pane.
        float spread = OwnStatusLayout.Spread(1080);
        Assert.Equal(1080 / 2f + 138 * spread,
            OwnStatusLayout.ScreenPos("Kill bonus", 1920, 1080).Y, 3);
        Assert.Equal(1080 / 2f + 107 * spread,
            OwnStatusLayout.ScreenPos("Signature percentage", 1920, 1080).Y, 3);
        Assert.Equal(1080 / 2f + 185 * spread,
            OwnStatusLayout.ScreenPos("Detection eye", 1920, 1080).Y, 3);
        Assert.Equal(1080 / 2f + 160 * spread,
            OwnStatusLayout.ScreenPos("Cloak active", 1920, 1080).Y, 3);
    }

    [Fact]
    public void Kill_counter_uses_the_native_experience_group_inner_offsets()
    {
        const int H = 128;                    // decoded killcounterbmp.mdl size
        const float g = 0.72f;                // the group's art scale
        float spread = OwnStatusLayout.Spread(1080);
        var anchor = OwnStatusLayout.ScreenPos("Kill bonus", 1920, 1080);
        // Bitmap: native lower-left Point(-23,-65) (dialog.mdl:2565) is y-up, so
        // the screen top-left is (-23, 65-H)*g from the group anchor.
        Assert.Equal(new Microsoft.Xna.Framework.Vector2(-23 * g, (65 - H) * g),
            OwnStatusLayout.KillCounterBitmapOffset(g, H));
        // Integer: native box top Point(0,5) (dialog.mdl:2559-2564), so from the
        // bitmap top it is (23, H-70)*g — (23, 58)*0.72, on the medal disc,
        // NOT the old restated 70*g that put it below the disc.
        Assert.Equal(new Microsoft.Xna.Framework.Vector2(23 * g, (H - 70) * g),
            OwnStatusLayout.KillCounterNumberFromBitmap(g, H));
        // Absolute offset from the anchor: the number's native top is 5 above
        // the ExperienceImage anchor in y-up, i.e. -5*g in screen y.
        var absolute = OwnStatusLayout.KillCounterBitmapOffset(g, H)
            + OwnStatusLayout.KillCounterNumberFromBitmap(g, H);
        Assert.Equal(0f, absolute.X, 3);
        Assert.Equal(-5f * g, absolute.Y, 3);
        // FONT-1c fix 1: the ExperienceImage anchor is (281-320, 240-102).
        Assert.Equal(1920 / 2f - 39 * spread, anchor.X, 3);
        Assert.Equal(1080 / 2f + 138 * spread, anchor.Y, 3);
    }

    private static string HudSource()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        return File.ReadAllText(Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Rendering", "HudRenderer.cs"));
    }

    private static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "missing " + signature);
        int open = source.IndexOf('{', start), depth = 0;
        for (int i = open; i < source.Length; i++)
        {
            if (source[i] == '{') depth++;
            else if (source[i] == '}' && --depth == 0) return source[open..(i + 1)];
        }
        throw new InvalidOperationException("unbalanced " + signature);
    }

    public static IEnumerable<object[]> StatusIconCases =>
    [
        // (id, texture w, h): the 32 px CortUI PNGs and the large MDL canvases.
        new object[] { "Detection eye", 128, 128 },
        new object[] { "Cloak active", 128, 128 },
        new object[] { "Autopilot status", 32, 32 },
        new object[] { "Vector lock status", 32, 32 },
        new object[] { "Carried flag", 128, 128 },
        new object[] { "Carried artifact", 128, 128 },
    ];

    /// <summary>FONT-1c review — a status bitmap's native TranslateImage point is
    /// its LOWER-left corner (y-up bounds Rect(Point(0,0),size),
    /// engine/image.cpp:184-190; engine/paneimage.cpp:91-97), so the drawn
    /// rectangle's BOTTOM edge sits on the anchor and the art rises above it.</summary>
    [Theory]
    [MemberData(nameof(StatusIconCases))]
    public void Status_icon_draw_rect_bottom_edge_sits_on_the_native_anchor(string id, int texW, int texH)
    {
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (1280, 720) })
        {
            var anchor = OwnStatusLayout.ScreenPos(id, w, h);
            float scale = OwnStatusLayout.StatusIconScale(texW);
            var topLeft = OwnStatusLayout.StatusIconTopLeft(id, texW, texH, w, h);
            Assert.Equal(anchor.X, topLeft.X, 3);                     // lower-LEFT: same x
            Assert.Equal(anchor.Y, topLeft.Y + texH * scale, 3);      // bottom edge = anchor
            Assert.True(topLeft.Y < anchor.Y);                        // art rises above it
        }
    }

    [Fact]
    public void DrawStatusIcon_draws_the_bitmap_from_its_lower_left_anchor()
    {
        // Fails on the pre-fix renderer, which passed the anchor itself as the
        // bitmap's TOP-left (_sb.Draw(tex, pos, ...)).
        var body = Method(HudSource(), "void DrawStatusIcon(");
        Assert.Contains("OwnStatusLayout.StatusIconTopLeft(id, tex.Width, tex.Height", body);
        Assert.Contains("_sb.Draw(tex, topLeft,", body);
        Assert.DoesNotContain("_sb.Draw(tex, pos,", body);
    }

    [Fact]
    public void Cloak_strength_text_hangs_below_the_cloak_art()
    {
        // The port-only cloak readout is text (hangs below its point); the
        // concloakbmp art spans [anchor - H*0.72, anchor]. The text top must be
        // below the art's bottom edge, at every supported viewport.
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (1280, 720) })
        {
            var cloak = OwnStatusLayout.ScreenPos("Cloak active", w, h);
            var art = OwnStatusLayout.StatusIconTopLeft("Cloak active", 128, 128, w, h);
            var text = OwnStatusLayout.ScreenPos("Cloak strength and drained state", w, h);
            Assert.Equal(cloak.X, text.X, 3);
            Assert.True(text.Y > cloak.Y, $"{w}x{h}: text {text.Y} must start below art bottom {cloak.Y}");
            Assert.True(text.Y > art.Y + 128 * 0.72f);
        }
    }

    [Fact]
    public void Kill_bonus_group_shares_the_signature_box_centre_and_the_font_floor()
    {
        // Native: both the kill-bonus number and SIG are 80-wide JustifyCenter
        // boxes at x=281 (dialog.mdl:2559-2564, 2595-2602, 2617, 2621), so
        // they share one centre.
        const float g = 0.72f;
        foreach (var (w, h) in new[] { (1920, 1080), (2560, 1440), (1280, 720) })
        {
            float spread = OwnStatusLayout.Spread(h);
            var anchor = OwnStatusLayout.ScreenPos("Kill bonus", w, h);
            var sig = OwnStatusLayout.ScreenPos("Signature percentage", w, h);
            float sigCentre = sig.X + OwnStatusLayout.NativeTextWidth / 2f * spread;
            var origin = OwnStatusLayout.KillBonusGroupOrigin(anchor, spread, g);
            // The number box (group x 0..80·g) — via the bitmap offsets, so the
            // native ExperienceImage maths (dialog.mdl:2556-2566) is unchanged.
            var box = origin + OwnStatusLayout.KillCounterBitmapOffset(g, 128)
                + OwnStatusLayout.KillCounterNumberFromBitmap(g, 128);
            Assert.Equal(sigCentre, box.X + OwnStatusLayout.NativeTextWidth / 2f * g, 3);
            Assert.Equal(anchor.Y - 5f * g, box.Y, 3);
            // The medal canvas (128 wide from -23·g) centres there too, ±1 native px.
            float medalCentre = origin.X - 23f * g + 128f * g / 2f;
            Assert.InRange(medalCentre - sigCentre, -2f, 2f);
        }
        // FONT-1 floors every text scale at 1 (NativeFonts.Select): the medal's
        // 0.72 group scale must not shrink the number below the native em.
        Assert.Equal(1f, NativeFonts.Select(g).Residual);
        var body = Method(HudSource(), "void DrawOwnStatus(");
        Assert.DoesNotContain("artScale * killFs", body);
        Assert.Contains("OwnStatusLayout.KillBonusGroupOrigin(anchor, spread, artScale)", body);
        Assert.Contains("Color.Black, killFs, boxScale: artScale", body);
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

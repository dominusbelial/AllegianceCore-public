using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame.Tests;

[Trait("Tier", "Fast")]
public sealed class P1096HudTextLayoutTests
{
    [Fact]
    public void Mixed_obstacles_and_panels_match_the_linear_placer_across_frames()
    {
        var actual = new HudTextLayout();
        var oracle = new LinearHudTextLayout();
        var random = new Random(1096);
        for (int frame = 0; frame < 48; frame++)
        {
            int w = random.Next(80, 1280), h = random.Next(48, 720);
            if (frame % 8 == 0) (w, h) = (7, 8); // Degenerate boxes on a tiny viewport.
            actual.Reset(w, h);
            oracle.Reset(w, h);
            for (int i = 0; i < 96; i++)
            {
                int x = random.Next(-100, w + 100), y = random.Next(-80, h + 80);
                var bounds = new Rectangle(x, y, random.Next(-4, w / 3 + 5), random.Next(-2, 52));
                if (i % 13 == 0) bounds = new(x, y, 2 * w, 2 * h);
                string key = $"frame-{frame}-box-{i}";
                switch (i % 8)
                {
                    case 0:
                        actual.Reserve(bounds);
                        oracle.Reserve(bounds);
                        break;
                    case 1:
                        actual.ReserveInstrument(bounds);
                        oracle.ReserveInstrument(bounds);
                        break;
                    case 2:
                        var parts = new List<Rectangle> { new(-4, -2, 14, 6), new(1, 16, 32, 7) };
                        var referenceParts = parts.ToList();
                        Assert.Equal(oracle.PlacePanel(bounds, referenceParts), actual.PlacePanel(bounds, parts));
                        Assert.Equal(referenceParts, parts);
                        break;
                    case 3:
                    case 4:
                        Assert.Equal(oracle.PlaceBlock(key, bounds), actual.PlaceBlock(key, bounds));
                        break;
                    default:
                        var size = new Vector2(bounds.Width + .4f, bounds.Height + .7f);
                        var anchor = new Vector2(x + .5f, y - .5f);
                        bool instrument = i % 8 == 5, fixedAnchor = i % 8 == 6;
                        Assert.Equal(oracle.Place(key, size, anchor, instrument, fixedAnchor),
                            actual.Place(key, size, anchor, instrument, fixedAnchor));
                        break;
                }
            }
            Assert.Equal<HudTextBox>(oracle.TextBoxes, actual.TextBoxes);
        }
    }

    [Theory]
    [InlineData(HudStyle.Classic, false)]
    [InlineData(HudStyle.Classic, true)]
    [InlineData(HudStyle.Modern, false)]
    [InlineData(HudStyle.Modern, true)]
    public void Admitted_contacts_and_merge_tags_keep_every_ordered_box(HudStyle style, bool declutter)
    {
        var actual = new HudTextLayout();
        var oracle = new LinearHudTextLayout();
        var settings = new HudSettings { Style = style, RadarDeclutter = declutter };
        var plan = style == HudStyle.Classic ? RadarLabelDeclutterOptions.Off : settings.RadarLabelPlan(1);
        var selector = new RadarLabelDeclutter();
        for (int frame = 0; frame < 4; frame++)
        {
            var contacts = new HudContact[150];
            for (int i = 0; i < contacts.Length; i++)
            {
                var point = frame switch
                {
                    0 => new HudPoint(64, 64),                         // Coincident, then overflow.
                    1 => new HudPoint(4 + (i % 12) * 32, 32),          // One row crossing tile boundaries.
                    2 => new HudPoint(i % 2 == 0 ? -20 : 430, i * 2),  // Edge-clamped.
                    _ => new HudPoint(i % 5 * 64, i % 7 * 32)
                };
                contacts[i] = new(HudContactKind.Ship, point, frame != 2, 1, null, i == 0, false,
                    Name: $"contact-{i}", Range: 300, IsEnemy: i % 2 == 0, IsSameSide: i % 2 != 0,
                    AcceptedOrder: i % 12 == 0, QueuedOrder: i % 12 == 1);
            }

            actual.Reset(400, 240);
            oracle.Reset(400, 240);
            selector.Select(contacts, RadarMode.Default, false, plan);
            Assert.True(selector.IsLabelled(12) && selector.IsLabelled(13));
            if (frame == 0 && plan.Enabled) Assert.NotEmpty(selector.Tags);
            int index = 0;
            foreach (var contact in contacts)
            {
                if (!selector.IsLabelled(index++)) continue;
                var desired = new Rectangle((int)contact.Position.X, (int)contact.Position.Y,
                    frame == 3 ? 800 : 72, frame == 3 ? 500 : 24);
                Assert.Equal(oracle.PlaceBlock(contact.Name!, desired), actual.PlaceBlock(contact.Name!, desired));
            }
            foreach (var tag in selector.Tags)
            {
                var rect = new Rectangle((int)tag.Point.X, (int)tag.Point.Y, 24, 14);
                string text = RadarLabelDeclutter.CountTag(tag.Count);
                Assert.Equal(oracle.PlaceBlock(text, rect), actual.PlaceBlock(text, rect));
            }
            Assert.Equal<HudTextBox>(oracle.TextBoxes, actual.TextBoxes);
        }
    }

    [Fact]
    public void Repeated_crowded_frames_reuse_placement_storage()
    {
        var layout = new HudTextLayout();
        void Frame()
        {
            layout.Reset(960, 540);
            layout.ReserveInstrument(new(64, 30, 160, 120));
            layout.Place("anchor", new(28, 24), new(40, 20), fixedAnchor: true);
            for (int i = 0; i < 72; i++) layout.PlaceBlock("contact", new(460, 250, 72, 24));
        }
        for (int i = 0; i < 10; i++) Frame();
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10; i++) Frame();
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }
}

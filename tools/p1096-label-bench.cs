// Run with .NET 10 on Linux. The report records the configuration and inputs.
#:project ../src/Allegiance.ClientCore.Game/Allegiance.ClientCore.Game.csproj
#:package MonoGame.Framework.DesktopGL@3.8.5.1
#:property PublishAot=false
using System.Runtime.InteropServices;
using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;

Console.WriteLine("style,declutter,layout,contacts,boxes,run,cpu_us_per_frame,bytes_per_frame");
foreach (var style in new[] { HudStyle.Classic, HudStyle.Modern })
foreach (bool enabled in new[] { false, true })
foreach (var (spread, count) in new[] { (true, 72), (false, 24), (false, 72), (false, 150) })
{
    var contacts = new HudContact[count];
    for (int i = 0; i < count; i++)
    {
        var point = spread ? new HudPoint(8 + 79 * (i % 12), 12 + 80 * (i / 12)) : new HudPoint(460, 250);
        contacts[i] = new(HudContactKind.Ship, point, true, 1, null, false, false,
            Name: $"ship-{i}", Range: 500, IsSameSide: i % 2 == 0, IsEnemy: i % 2 != 0, AcceptedOrder: i % 8 == 0);
    }

    // Precompute the draw stream to time just Reset and PlaceBlock. Classic's
    // renderer always selects Off. Modern resolves the saved setting each draw.
    var settings = new HudSettings { Style = style, RadarDeclutter = enabled };
    var plan = style == HudStyle.Classic ? RadarLabelDeclutterOptions.Off : settings.RadarLabelPlan(1);
    var selector = new RadarLabelDeclutter();
    selector.Select(contacts, RadarMode.Default, false, plan);
    var blocks = new List<(string Text, Rectangle Desired)>();
    for (int i = 0; i < count; i++)
        if (selector.IsLabelled(i))
            blocks.Add((contacts[i].Name!, Box(contacts[i].Position, 72, 24)));
    foreach (var tag in selector.Tags)
        blocks.Add((RadarLabelDeclutter.CountTag(tag.Count), Box(tag.Point, 24, 14)));

    var layout = new HudTextLayout();
    void Frame()
    {
        layout.Reset(960, 540);
        foreach (var block in blocks)
            layout.PlaceBlock(block.Text, block.Desired);
    }

    for (int warm = 0; warm < 30; warm++) Frame();
    for (int run = 1; run <= 3; run++)
    {
        const int frames = 250;
        long start = ThreadNanos();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int frame = 0; frame < frames; frame++) Frame();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        double us = (ThreadNanos() - start) / (frames * 1000.0);
        Console.WriteLine(FormattableString.Invariant(
            $"{style},{enabled},{(spread ? "spread" : "coincident")},{count},{blocks.Count},{run},{us:F3},{allocated / (double)frames:F2}"));
    }
}

static Rectangle Box(HudPoint p, int width, int height) => new((int)p.X, (int)p.Y, width, height);

static long ThreadNanos()
{
    if (clock_gettime(3 /* Linux CLOCK_THREAD_CPUTIME_ID */, out var time) != 0)
        throw new InvalidOperationException("Unable to read calling-thread CPU time");
    return time.Seconds * 1_000_000_000L + time.Nanoseconds;
}

[DllImport("libc")]
static extern int clock_gettime(int clock, out Timespec time);

[StructLayout(LayoutKind.Sequential)]
struct Timespec { public nint Seconds, Nanoseconds; }

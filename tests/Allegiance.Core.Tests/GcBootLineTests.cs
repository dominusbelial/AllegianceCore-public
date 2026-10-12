using System;
using System.IO;
using System.Runtime;
using Srv = global::Allegiance.Server.Server;
using Cfg = global::Allegiance.Server.ServerConfig;
using Gm = global::Allegiance.Server.GameConfig;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// P619 — the server boots with Server GC + concurrent GC by default
/// (<c>&lt;ServerGarbageCollection&gt;</c> / <c>&lt;ConcurrentGarbageCollection&gt;</c>
/// in <c>Allegiance.Server.csproj</c>), and the active GC is printed at boot as
/// <c>[boot] gc = server/concurrent</c>. This test pins the CONTRACT between the
/// boot line and the runtime's actual GC state (not a hardcoded value): whatever
/// <see cref="GCSettings.IsServerGC"/> and <see cref="GCLatencyMode"/> report in
/// the process that boots the server, the boot banner must say exactly that.
/// A run that slips back to workstation GC (e.g. a host env override) is therefore
/// visible in the boot banner instead of silently assumed server.
/// </summary>
[Collection("LiveNetwork")]
public class GcBootLineTests
{
    private static readonly string ArtRoot = TestArtRoot.Value;

    private static Cfg Config(int port = 0) => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = port,
        UpdatesPerSecond = 20,
        Games =
        [
            new Gm { Name = "GC Boot Line Test", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["Alpha", "Bravo"] },
        ],
    };

    private static string BootCapture()
    {
        var sw = new StringWriter();
        var orig = Console.Out;
        Console.SetOut(sw);
        try
        {
            using var server = new Srv();
            server.Boot(Config());
        }
        finally { Console.SetOut(orig); }
        return sw.ToString();
    }

    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Boot_line_reports_the_active_gc_mode()
    {
        Require.Artwork();
        string boot = BootCapture();

        // The banner must exist.
        Assert.Contains("[boot] gc = ", boot);

        // ...and must MATCH the runtime's actual GC state (contract, not snapshot).
        string mode = GCSettings.IsServerGC ? "server" : "workstation";
        string concurrent = GCSettings.LatencyMode == GCLatencyMode.Batch ? "non-concurrent" : "concurrent";
        Assert.Contains($"[boot] gc = {mode}/{concurrent}", boot);
    }
}

using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P454 — pure-domain (no sockets) proof of <see cref="Probe.CanRipcord"/>,
/// the port of <c>CprobeIGC::GetCanRipcord</c> (<c>probeigc.h:248-254</c>).
/// Three conjuncts are exercised against a real probe type cloned with the
/// delay/lifespan the test needs (a <c>with</c> on a real PCore001 record, so
/// the field layout is genuine, not fabricated):
///   (1) it is a teleport probe — <c>DtRipcord &gt;= 0</c>;
///   (2) its activation delay has elapsed — <c>(now − DeployTime) &gt;= dt</c>;
///   (3) it will not expire before the ripcord lands —
///       <c>(now + (ripcordSpeed + 1)) &lt;= TimeExpire</c>.
/// A non-teleport probe (<c>dt &lt; 0</c>) is never ripcordable at any time.
/// </summary>
public sealed class ProbeCanRipcordUnitTests
{
    private static readonly Lazy<StaticCore> RepresentativeCore = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.VersionAndDataSize) continue;
            return StaticCore.Load(file);
        }
        throw new InvalidOperationException("no static core found in corpus");
    });

    private static readonly Lazy<MissionMap> SomeMap = new(() =>
    {
        foreach (var path in Corpus.Files)
        {
            IgcFile file;
            try { file = IgcReader.ParseFile(path); }
            catch { continue; }
            if (file.Wrapper != IgcWrapper.DataSize) continue;
            var map = MissionMap.Load(file);
            if (map.Clusters.Count == 0) continue;
            return map;
        }
        throw new InvalidOperationException("no non-empty map found in corpus");
    });

    private static DataProbeTypeIGC Base() => RepresentativeCore.Value.ProbeTypes.Values.First();

    private static Probe Make(DataProbeTypeIGC t, double deployNow = 100.0)
    {
        var sector = new Sector(SomeMap.Value.Clusters[0]);
        return new Probe(1, sector, t, new Vector3(0f, 0f, 0f), deployNow, sideId: 0, launcher: null);
    }

    /// <summary>⭐ Conjunct 2 — a teleport probe is ripcordable only AFTER its
    /// activation delay has elapsed: at deploy time and before the delay it is
    /// not a valid anchor; at and beyond the delay it is.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Teleport_probe_ripcordable_after_activation_delay_only()
    {
        Require.Artwork();
        var probe = Make(Base() with { DtRipcord = 10f, Lifespan = 60f }, deployNow: 100.0);
        const float ripcordSpeed = 10f;

        Assert.False(probe.CanRipcord(100.0, ripcordSpeed), "at deploy time the delay has not elapsed");
        Assert.False(probe.CanRipcord(109.9, ripcordSpeed), "0.1s short of the delay");
        Assert.True(probe.CanRipcord(110.0, ripcordSpeed), "exactly at the delay");
        Assert.True(probe.CanRipcord(130.0, ripcordSpeed), "well past the delay");
    }

    /// <summary>⭐ Conjunct 1 — a NON-teleport probe (<c>DtRipcord &lt; 0</c>) is
    /// never a valid ripcord anchor, no matter how much time passes.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Non_teleport_probe_is_never_ripcordable()
    {
        Require.Artwork();
        var probe = Make(Base() with { DtRipcord = -1f, Lifespan = 600f }, deployNow: 100.0);
        Assert.False(probe.CanRipcord(100.0, 10f));
        Assert.False(probe.CanRipcord(700.0, 10f));   // long after deploy; still not teleport
    }

    /// <summary>⭐ Conjunct 3 — a teleport probe that would expire before the
    /// ripcord lands (within <c>ripcordSpeed + 1</c> of now) stops being a valid
    /// anchor; one comfortably before expiry remains valid.</summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Probe_about_to_expire_is_not_ripcordable()
    {
        Require.Artwork();
        // Deploy at 100 with a 30s lifespan → TimeExpire = 130. RipcordSpeed 10
        // → usable while now + 11 <= 130, i.e. now <= 119.
        var probe = Make(Base() with { DtRipcord = 0f, Lifespan = 30f }, deployNow: 100.0);
        const float ripcordSpeed = 10f;

        Assert.True(probe.CanRipcord(119.0, ripcordSpeed), "now+11 == 130 == expiry is still legal");
        Assert.False(probe.CanRipcord(119.05, ripcordSpeed), "now+11 > expiry → the ripcord would never land");
    }
}

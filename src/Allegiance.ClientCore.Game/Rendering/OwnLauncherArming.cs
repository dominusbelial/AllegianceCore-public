using Allegiance.Core;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P786 (PAR-5) — the own-ship launcher arming clocks behind the flight
/// inventory's <c>ReadyState 2</c> arming bar (native
/// <c>PartWrapper::GetReadyState</c> case 2 + <c>GetArmedFraction</c>,
/// <c>WinTrek/consoledata.cpp:1157-1177,1189-1202</c>).
///
/// Native computes the armed fraction entirely on the CLIENT: its own ship's
/// magazine/dispenser parts are simulated locally, so
/// <c>CmagazineIGC::Update</c> resets <c>m_timeLoaded = now + GetLoadTime()</c>
/// the instant the ship fires (<c>Igc/magazineigc.cpp:100-121</c>) and
/// <c>Activate()</c> sets <c>m_timeLoaded = lastUpdate + 1/mountRate +
/// loadTime</c> when the part mounts (<c>magazineigc.h:73</c>; the dispenser's
/// mount/load rules are identical, <c>dispenserigc.h:72-76,148-152</c>). This
/// client is a pure renderer, so the same two clocks are replayed from the
/// wire events the native client would have acted on — all of which it
/// already receives, no new wire data:
/// <list type="bullet">
/// <item>a magazine fire = the own <c>FM_CS_FIRE_MISSILE(70)</c> salvo count
///       (<see cref="Allegiance.ClientCore.State.ClientWorld.OwnMissilesFired"/>);
///       native's player fire path is exactly this message
///       (<c>FedSrv.CPP:5413-5507</c>), the high-word state bit never crosses
///       the 16-bit <c>CompactState</c> slot;</item>
/// <item>a dispenser fire = the own creation EXPORTs (probe <c>dp.ShipId</c> /
///       mine <c>dm.LauncherId</c> equal to the own ship —
///       <see cref="Allegiance.ClientCore.State.ClientWorld.OwnDispenserDeployed"/>);</item>
/// <item>a mount = the loadout's per-part <c>FractionMounted</c> first
///       observation / transition to <see cref="ExpandedPartData.FullyMounted"/>.</item>
/// </list>
/// Ships mount at most one magazine and one dispenser (native reads them as
/// <c>GetMountedPart(ET_Magazine, 0)</c> / <c>ET_Dispenser, 0</c>), so the
/// clocks are keyed by equipment type, not mount id.
///
/// All times are the caller's local monotonic seconds — the same clock at
/// update and query (the delays are constants, so local-time skew is inert).
/// Update and query touch only fields; the frame path stays allocation-free.
/// </summary>
public sealed class OwnLauncherArming
{
    private long _missilesFired = -1;
    private long _dispenserDeployed = -1;
    private readonly byte[] _lastMountedFraction = [0, 0];   // [0]=magazine, [1]=dispenser
    private readonly double[] _timeLoaded =
        [double.NegativeInfinity, double.NegativeInfinity];

    private const int MagazineSlot = 0;
    private const int DispenserSlot = 1;

    /// <summary>The local monotonic instant of the most recent
    /// <see cref="Update"/> — the query basis so every consumer in a frame
    /// reads the same clock (NegativeInfinity before the first update).</summary>
    public double NowLocalSeconds { get; private set; } = double.NegativeInfinity;

    /// <summary>Replay one frame of wire events. <paramref name="loadout"/> is
    /// the own ship's MOUNTED loadout (cargo rows never arm),
    /// <paramref name="resolve"/> the catalogue part lookup,
    /// <paramref name="loadTimeOf"/> the expendable load time in seconds for a
    /// launcher part (magazine → its missile type, dispenser → its
    /// mine/probe type, <c>Ship.DispenserLoadTime</c> semantics),
    /// <paramref name="mountRate"/> the <c>c_fcidMountRate</c> float constant
    /// (index 10, default 1.0) and <paramref name="nowLocalSeconds"/> the local
    /// monotonic instant.</summary>
    public void Update(
        long missilesFired, long dispenserDeployed,
        IReadOnlyList<ExpandedPartData> loadout,
        Func<ExpandedPartData, (EquipmentType Type, double LoadTime)?> resolve,
        double mountRate, double nowLocalSeconds)
    {
        NowLocalSeconds = nowLocalSeconds;
        if (loadout is { Count: > 0 })
        {
            for (int i = 0; i < loadout.Count; i++)
            {
                var entry = loadout[i];
                if (resolve(entry) is not { } resolved) continue;
                int slot = resolved.Type switch
                {
                    EquipmentType.Magazine => MagazineSlot,
                    EquipmentType.Dispenser => DispenserSlot,
                    _ => -1,
                };
                if (slot < 0) continue;
                byte fraction = entry.FractionMounted;
                if (slot == MagazineSlot) MagazineLoadTime = resolved.LoadTime;
                else DispenserLoadTime = resolved.LoadTime;
                // magazineigc.h:73 — Activate() stamps the load clock at mount
                // time with the mount rate plus the expendable load time. The
                // client observes the mount through the loadout stream, so the
                // stamp lands on the first sighting / the completion edge.
                if (_lastMountedFraction[slot] == 0 && fraction > 0)
                    _timeLoaded[slot] = nowLocalSeconds + 1.0 / mountRate + resolved.LoadTime;
                if (fraction > _lastMountedFraction[slot]) _lastMountedFraction[slot] = fraction;
            }
        }

        // magazineigc.cpp:100-121 — the fire resets the clock to now + loadTime.
        if (missilesFired != _missilesFired)
        {
            _missilesFired = missilesFired;
            if (_timeLoaded[MagazineSlot] != double.NegativeInfinity)
                _timeLoaded[MagazineSlot] = nowLocalSeconds + MagazineLoadTime;
        }
        if (dispenserDeployed != _dispenserDeployed)
        {
            _dispenserDeployed = dispenserDeployed;
            if (_timeLoaded[DispenserSlot] != double.NegativeInfinity)
                _timeLoaded[DispenserSlot] = nowLocalSeconds + DispenserLoadTime;
        }
    }

    /// <summary>The load times captured by the most recent
    /// <see cref="Update"/> (0 = none yet). The armed-fraction query needs the
    /// same value the clock was stamped with.</summary>
    public double MagazineLoadTime { get; private set; }
    public double DispenserLoadTime { get; private set; }

    /// <summary>Native <c>GetArmedFraction</c> for one equipment type
    /// (magazine or dispenser; anything else reads 1 — native only defines the
    /// fraction for launchers, <c>Igc/igc.h:3993-3999</c>). 0 while mounting,
    /// 1 while no clock exists (steady state), otherwise the magazineigc.h
    /// ramp. Evaluated at the last <see cref="Update"/> instant.</summary>
    public float ArmedFraction(EquipmentType type, float mountedFraction)
    {
        int slot = type switch
        {
            EquipmentType.Magazine => MagazineSlot,
            EquipmentType.Dispenser => DispenserSlot,
            _ => -1,
        };
        if (slot < 0) return 1f;
        double loadTime = slot == MagazineSlot ? MagazineLoadTime : DispenserLoadTime;
        return HudWireValues.LauncherArmedFraction(mountedFraction, _timeLoaded[slot], loadTime, NowLocalSeconds);
    }

    /// <summary>Test/proof seam: force a clock (local seconds basis).</summary>
    internal void StampForTest(EquipmentType type, double timeLoadedLocalSeconds, double loadTimeSeconds, double nowLocalSeconds)
    {
        int slot = type == EquipmentType.Magazine ? MagazineSlot : DispenserSlot;
        _timeLoaded[slot] = timeLoadedLocalSeconds;
        if (slot == MagazineSlot) MagazineLoadTime = loadTimeSeconds;
        else DispenserLoadTime = loadTimeSeconds;
        _lastMountedFraction[slot] = ExpandedPartData.FullyMounted;
        NowLocalSeconds = nowLocalSeconds;
    }
}

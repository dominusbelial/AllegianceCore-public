using System;
using System.IO;
using System.Linq;
using Allegiance.ClientCore.Flight;
using Allegiance.ClientCore.MonoGame.Audio;
using Allegiance.Core;
using Allegiance.Core.Audio;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P1003 — the own-ship afterburner loop must sound only while an
/// <c>ET_Afterburner</c> part is MOUNTED and its power ramp is above zero, the
/// two native conditions behind <c>PlaySoundIf(..., AfterburnerPower() &gt; 0.0f)</c>
/// (<c>WinTrek/trekigc.cpp:1320-1322</c> interior, <c>:1344-1347</c> exterior;
/// id from <c>GetAfterburnerSoundID</c>, <c>:1115-1130</c>; power from
/// <c>:820-825</c>). The v0.1.6 report — "press shift in a pod and you hear the
/// boost, no actual boost" — is the port passing the <c>AfterburnerButton</c>
/// <c>StateM</c> bit instead of that state.
/// <para>
/// The three required cases run through the SAME power source production uses:
/// <see cref="OwnShipSimulator.Power"/>, the own-ship client simulation's ramp
/// (<c>CafterburnerIGC::m_power</c>, <c>afterburnerIGC.cpp:103-164</c>), stepped
/// with the exact state mask the client sends. Pod = no part mounted; empty tank
/// = part mounted but power never ramps; burning = part mounted, tank non-empty,
/// button held.
/// </para>
/// </summary>
public class P1003BoosterSoundGateTests
{
    private const short InteriorId = 680;   // afterburner1InSound → iafter
    private const short ExteriorId = 700;   // afterburner1OutSound → xafter

    private static readonly ShipDeadReckoner.HullStats Hull = new(
        Thrust: 1000f, MaxSpeed: 100f, Mass: 100f,
        SideMultiplier: 1f, BackMultiplier: 1f,
        MaxTurnRateYaw: 1f, MaxTurnRatePitch: 1f, MaxTurnRateRoll: 1f,
        TurnTorqueYaw: 1f, TurnTorquePitch: 1f, TurnTorqueRoll: 1f);

    private static DataPartTypeIGC.Afterburner Booster(float fuelConsumption = 2f) => new(
        FuelConsumption: fuelConsumption, MaxThrust: 200f,
        OnRate: 1f, OffRate: 1f, InteriorSound: InteriorId, ExteriorSound: ExteriorId);

    /// <summary>A stepped own-ship simulation: the production client path for the
    /// local ship's afterburner power. <paramref name="booster"/> null = nothing
    /// mounted (a pod, whose parts died with the ship, P998).</summary>
    private static OwnShipSimulator Stepped(DataPartTypeIGC.Afterburner? booster,
        float fuel, int stateM, float seconds = 0.5f)
    {
        var sim = new OwnShipSimulator();
        sim.SetHull(Hull, booster?.MaxThrust ?? 0f);
        sim.SetAfterburner(booster, mounted: booster is null ? (byte)0 : (byte)255,
            fuel: fuel, mountRate: 1f, power: 0f);
        sim.Step(seconds, new ControlData(0f, 0f, 0f, 1f), stateM);
        return sim;
    }

    // ── needed cases: pod, empty tank, burning ────────────────────────────────

    /// <summary>⭐ Pod + booster held: the lifepod has NO parts (P998), so there
    /// is nothing to mount and the power ramp cannot exist — native's
    /// <c>GetMountedPart(ET_Afterburner, 0)</c> is NULL and the loop is NA.
    /// This is the reported bug: the button bit alone used to play it.</summary>
    [Fact]
    public void Pod_with_booster_held_plays_no_afterburner_loop()
    {
        var sim = Stepped(booster: null, fuel: 1000f,
            stateM: ShipControlState.AfterburnerButton);
        Assert.Equal(0f, sim.Power);

        var loop = new AfterburnerLoop(Mounted: false, Power: sim.Power,
            InteriorSoundId: AfterburnerLoop.NA, ExteriorSoundId: AfterburnerLoop.NA);
        Assert.False(loop.Plays);
        Assert.Equal(AfterburnerLoop.NA, loop.InteriorId);
        Assert.Equal(AfterburnerLoop.NA, loop.ExteriorId);
    }

    /// <summary>⭐ Fighter with an EMPTY tank: the part is mounted, but
    /// <c>CafterburnerIGC::Update</c> activates only on
    /// <c>button &amp;&amp; fuel != 0</c> (<c>afterburnerIGC.cpp:127</c>), so the power
    /// ramp never leaves 0 and native plays only the "out of gas" VO
    /// (<c>salNoFuelSound</c>, trekigc.cpp:829-845) — never the loop.</summary>
    [Fact]
    public void Fighter_with_empty_tank_and_button_held_plays_no_loop()
    {
        var sim = Stepped(Booster(), fuel: 0f, stateM: ShipControlState.AfterburnerButton);
        Assert.Equal(0f, sim.Power);   // no activation, no ramp

        var loop = new AfterburnerLoop(true, sim.Power, InteriorId, ExteriorId);
        Assert.False(loop.Plays);
        // The ids still resolve (a part IS mounted); the power gate is what
        // silences both beds, exactly native's AfterburnerPower() > 0.
        Assert.Equal(InteriorId, loop.InteriorId);
        Assert.Equal(ExteriorId, loop.ExteriorId);
    }

    /// <summary>⭐ Fighter burning: mounted part, tank non-empty, button held —
    /// the ramp rises above zero and BOTH native beds play, each from the
    /// mounted part's own interior/exterior SoundID.</summary>
    [Fact]
    public void Fighter_burning_plays_the_loop_with_the_parts_own_sounds()
    {
        var sim = Stepped(Booster(), fuel: 1000f, stateM: ShipControlState.AfterburnerButton);
        Assert.True(sim.Power > 0f);

        var loop = new AfterburnerLoop(true, sim.Power, InteriorId, ExteriorId);
        Assert.True(loop.Plays);
        Assert.Equal(InteriorId, loop.InteriorId);
        Assert.Equal(ExteriorId, loop.ExteriorId);
        Assert.Equal("iafter", NativeSoundCatalog.Resolve(loop.InteriorId)!.Waves[0]);
        Assert.Equal("xafter", NativeSoundCatalog.Resolve(loop.ExteriorId)!.Waves[0]);
    }

    /// <summary>Holding the button without a mounted part must stay silent even
    /// if a stale ramp value is handed in: native returns <c>NA</c> for the id
    /// and 0 for the power whenever <c>GetMountedPart</c> is NULL, so BOTH
    /// conditions are load-bearing.</summary>
    [Theory]
    [InlineData(false, 0.75f)]   // stale power, nothing mounted
    [InlineData(false, 0f)]
    [InlineData(true, 0f)]       // mounted, ramp not started
    [InlineData(true, 0.0001f)]  // any positive ramp plays
    public void Gate_requires_mounted_part_and_positive_power(bool mounted, float power)
    {
        var loop = new AfterburnerLoop(mounted, power, InteriorId, ExteriorId);
        Assert.Equal(mounted && power > 0f, loop.Plays);
        Assert.Equal(mounted ? InteriorId : AfterburnerLoop.NA, loop.InteriorId);
        Assert.Equal(mounted ? ExteriorId : AfterburnerLoop.NA, loop.ExteriorId);
    }

    /// <summary>Released boost: the ramp decays to zero and the loop stops —
    /// the button bit is not what keeps it alive either.</summary>
    [Fact]
    public void Released_boost_decays_to_silence()
    {
        var sim = new OwnShipSimulator();
        sim.SetHull(Hull, Booster().MaxThrust);
        sim.SetAfterburner(Booster(), mounted: 255, fuel: 1000f, mountRate: 1f);
        // Burn 0.2 s at full on-rate, then release for long enough to reach 0.
        sim.Step(0.2f, new ControlData(0f, 0f, 0f, 1f), ShipControlState.AfterburnerButton);
        Assert.True(sim.Power > 0f);
        sim.Step(3f, new ControlData(0f, 0f, 0f, 1f), 0);
        Assert.Equal(0f, sim.Power);
        Assert.False(new AfterburnerLoop(true, sim.Power, InteriorId, ExteriorId).Plays);
    }

    /// <summary>The production loop lifecycle through native's
    /// <c>PlaySoundIf</c> state machine (<see cref="PlaySoundIfSlot"/>, the port
    /// of <c>trekigc.cpp:930-951</c>): a pod holding the button never starts a
    /// bed, a burning fighter starts BOTH beds with its own ids, and releasing
    /// the boost (power back to 0) stops them. This is exactly what
    /// <c>SoundManager.Update</c> drives each frame.</summary>
    [Fact]
    public void Loop_slots_start_only_while_the_part_burns_and_stop_on_release()
    {
        var interior = new PlaySoundIfSlot();
        var exterior = new PlaySoundIfSlot();

        // Pod (no part) with the booster button held: nothing mounted, no power.
        var pod = new AfterburnerLoop(Mounted: false, Power: 0f,
            InteriorSoundId: AfterburnerLoop.NA, ExteriorSoundId: AfterburnerLoop.NA);
        Assert.Equal(PlaySoundIfSlot.Action.None, interior.Update(pod.Plays, pod.InteriorId));
        Assert.Equal(PlaySoundIfSlot.Action.None, exterior.Update(pod.Plays, pod.ExteriorId));

        // Burning fighter: both native beds start from the part's own ids.
        var burning = new AfterburnerLoop(true, 0.8f, InteriorId, ExteriorId);
        Assert.Equal(PlaySoundIfSlot.Action.Start, interior.Update(burning.Plays, burning.InteriorId));
        Assert.Equal(PlaySoundIfSlot.Action.Start, exterior.Update(burning.Plays, burning.ExteriorId));
        Assert.Equal(InteriorId, interior.PlayingId);
        Assert.Equal(ExteriorId, exterior.PlayingId);
        // Held: no restart (native's "if (!pSound)").
        Assert.Equal(PlaySoundIfSlot.Action.None, interior.Update(burning.Plays, burning.InteriorId));

        // Released: the ramp is back to 0 → both loop instances stop.
        var released = burning with { Power = 0f };
        Assert.Equal(PlaySoundIfSlot.Action.Stop, interior.Update(released.Plays, released.InteriorId));
        Assert.Equal(PlaySoundIfSlot.Action.Stop, exterior.Update(released.Plays, released.ExteriorId));
        Assert.Null(interior.PlayingId);
        Assert.Null(exterior.PlayingId);
    }

    /// <summary>Wiring pin: the production call site must hand the loop the
    /// MOUNTED part (own loadout, lifepod-aware) and the own-ship simulation's
    /// REAL power ramp — never the <c>AfterburnerButton</c> <c>StateM</c> bit,
    /// which is the v0.1.6 bug. Reads the source like
    /// <see cref="SoundAssetManifestTests"/> does for the bundled asset list.</summary>
    [Fact]
    public void Production_call_site_passes_mounted_state_and_real_power()
    {
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.Game")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string source = File.ReadAllText(
            Path.Combine(root, "src", "Allegiance.ClientCore.Game", "Game1.FlightInput.cs"));

        int at = source.IndexOf("new Audio.AfterburnerLoop(", StringComparison.Ordinal);
        Assert.True(at >= 0, "own-ship update must build the native afterburner loop state");
        string construction = source.Substring(at, 400);
        Assert.Contains("Mounted: ownAfterburner", construction);   // mounted ET_Afterburner, lifepod-aware
        Assert.Contains("Power: _ownSim.Power", construction);      // the real CafterburnerIGC::m_power analogue
        Assert.DoesNotContain("afterburnerActive:", source);        // the old button-bit gate is gone
    }

    // ── the retail core really carries those per-part ids ─────────────────────
    /// <summary>Pins the per-part sound ids the loop resolves against the REAL
    /// retail core. PCore001's fourteen <c>ET_Afterburner</c> parts carry six
    /// DIFFERENT interior ids (514/515/516/518/519/680) and two exterior ids
    /// (529/700) — the civilizations' own waves (<c>dn_ptaftion</c>,
    /// <c>dn_ptaftlgt</c>, <c>dn_ptaftcrs</c>, <c>dn_ptafthvy</c>,
    /// <c>dn_ptafthpr</c>) plus the generic <c>afterburner1</c> pair
    /// (<c>iafter</c>/<c>xafter</c>). The pre-P1003 port always played
    /// <c>iafter</c>, so every civ but one boosted with the wrong wave; the
    /// per-mount resolution must honour the core. Each wave must also ship in
    /// the bundled content, or that bed would be silently missing at runtime.
    /// </summary>
    [Trait("Requires", "Artwork")]
    [SkippableFact]
    public void Retail_core_afterburner_parts_resolve_to_their_own_bundled_beds()
    {
        Require.Artwork();
        string artwork = ExternalPaths.Resolve(ExternalResourceKind.Artwork);
        var core = Allegiance.Core.Model.StaticCore.Load(
            IgcReader.ParseFile(Path.Combine(artwork, "PCore001.igc")));

        var parts = core.PartTypes.Values
            .Where(p => p.Type is DataPartTypeIGC.Afterburner)
            .Select(p => (DataPartTypeIGC.Afterburner)p.Type)
            .ToArray();
        Assert.Equal(14, parts.Length);

        var interior = parts.Select(p => p.InteriorSound).Distinct().OrderBy(id => id).ToArray();
        var exterior = parts.Select(p => p.ExteriorSound).Distinct().OrderBy(id => id).ToArray();
        Assert.Equal(new short[] { 514, 515, 516, 518, 519, 680 }, interior);
        Assert.Equal(new short[] { 529, 700 }, exterior);

        // Every bed a retail afterburner names must be a playable native
        // template, and every wave must be in the bundled content set
        // (Content.mgcb builds the SoundEffect the numeric dispatch loads).
        string root = AppContext.BaseDirectory;
        while (!Directory.Exists(Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("Repository not found");
        string content = Path.Combine(root, "src", "Allegiance.ClientCore.MonoGame", "Content");
        string manifest = File.ReadAllText(Path.Combine(content, "Content.mgcb"));

        foreach (short id in interior.Concat(exterior))
        {
            var template = NativeSoundCatalog.Resolve(id);
            Assert.NotNull(template);
            Assert.True(template!.Playable, $"id {id} has no wave");
            string stem = template.Waves[0];
            Assert.True(File.Exists(Path.Combine(content, "Sounds", stem + ".ogg")), stem + ".ogg bundled");
            Assert.Contains($"/build:Sounds/{stem}.ogg", manifest);

            // The numeric dispatch must resolve the id and treat it as the ASR
            // loop native starts (LoopSeconds from the sounddef template), or
            // PlaySoundIf would start a one-shot instead of a bed.
            Assert.True(NativeSoundDispatch.TryPlan(id, null, null, out var plan),
                $"id {id} is not a startable native SoundID");
            Assert.Equal(stem, plan.Stem);
            Assert.True(NativeSoundTemplateSettings.Resolve(id).LoopSeconds > 0f,
                $"id {id} must carry an ASR loop range");
        }

        // And the loop state built from any one of them plays that part's pair.
        var heavy = parts.First(p => p.InteriorSound == 518);
        var loop = new AfterburnerLoop(true, 0.5f, heavy.InteriorSound, heavy.ExteriorSound);
        Assert.Equal("dn_ptafthvy", NativeSoundCatalog.Resolve(loop.InteriorId)!.Waves[0]);
        Assert.Equal("xafter", NativeSoundCatalog.Resolve(loop.ExteriorId)!.Waves[0]);
        Assert.True(loop.Plays);
    }
}

using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;
using Allegiance.Tools.McpSpawner;
using Xunit;
using Xunit.Abstractions;
using Srv = global::Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

/// <summary>
/// P801 — loopback proof against the port's REAL station damage model (not a
/// reimplementation): boot a real server, take two real garrisons out of the
/// simulated mission, and feed the SAME SRM Anti-Base 2 rounds on two schedules.
/// <list type="bullet">
/// <item>massed: four bombers release on one go time (2 rounds each, 4 s apart)</item>
/// <item>trickle: the same four bombers released one minute apart (the old
///       finale relaunch stream)</item>
/// </list>
/// The massed group kills the garrison; the trickle does not, because the native
/// armour/shield regeneration (<c>CstationIGC::Update</c>, stationIGC.cpp:192-210)
/// outpaces it. The pure planner's replay must agree with the real station's
/// fractions. Deterministic and fast (no network, no wall-clock waits).
/// </summary>
[Collection("LiveNetwork")]
public sealed class P801StrikeLoopbackLiveTests(ITestOutputHelper output)
{
    private static readonly string ArtRoot = ExternalPaths.Resolve(ExternalResourceKind.Artwork);

    private static global::Allegiance.Server.ServerConfig Config() => new()
    {
        ArtPath = ArtRoot,
        StaticCore = "PCore001.igc",
        Port = 0,
        LiveSettings = new global::Allegiance.Server.LiveSettingsConfig
        {
            DebugMode = true,
            AutoNextMatch = new() { Enabled = false },
        },
        Games =
        [
            new global::Allegiance.Server.GameConfig
            {
                Name = "P801 strike mass", Map = "training_6.igc", Teams = 2,
                CivIds = [25, 3], SideNames = ["Alpha", "Bravo"], MaxPlayersPerTeam = 40,
            },
        ],
    };

    private static StrikeVolley.Loadout StealthAb2() => new(23,
        [new StrikeVolley.Mount(8, 500, 2, 4)], CloakCapable: true);

    private sealed record Run(bool Dead, int Hits, float Armor, float Shield);

    /// <summary>Apply a release schedule to a real <see cref="Station"/>: one
    /// <c>ReceiveDamage</c> per round (the exact native entry point the combat
    /// pass uses) with <see cref="Station.Regenerate"/> between rounds.</summary>
    private static Run Apply(Station station, IReadOnlyList<(double At, StrikeVolley.Shot Shot)> schedule,
        double horizon)
    {
        int hits = 0;
        double now = 0;
        foreach (var (at, shot) in schedule.OrderBy(s => s.At))
        {
            station.Regenerate((float)(at - now));
            now = at;
            station.ReceiveDamage(shot.DamageType, shot.Power, at, station.Center, station.Center, null);
            hits++;
            if (station.Dead) break;
        }
        if (!station.Dead) station.Regenerate((float)(horizon - now));
        return new(station.Dead, hits, station.ArmorFraction, station.ShieldFraction);
    }

    [SkippableFact]
    public void Massed_finale_release_kills_a_real_garrison_and_the_same_trickle_does_not()
    {
        Require.Artwork();
        var core = Catalog.RepresentativeCore();
        using var server = new Srv();
        server.Boot(Config());
        for (int i = 0; i < 300 && (server.Missions.Count == 0
            || server.Missions[0].Sectors.SelectMany(s => s.StationRuntimes).Count() < 2); i++)
            server.TickSim();
        var mission = server.Missions[0];
        var garrisons = mission.Sectors.SelectMany(s => s.StationRuntimes)
            .Where(s => core.StationTypes.TryGetValue(s.Type.StationTypeId, out var t) && t.ClassId == 0)
            .OrderBy(s => s.StationId).ToArray();
        Assert.True(garrisons.Length >= 2, "training_6 must field two real garrisons");
        var massedStation = garrisons[0];
        // P864c applies each side's native station-tech replacement, so two
        // authored garrisons can now have different durability. Compare the
        // same station type and side attributes on both schedules.
        var trickleData = massedStation.Data with
        {
            StationId = checked((short)(mission.Sectors.SelectMany(s => s.StationRuntimes).Max(s => s.StationId) + 1)),
        };
        var trickleStation = new Station(trickleData, massedStation.Type);
        mission.PlaceStation(trickleStation, mission.Sectors.Single(s => s.Id == massedStation.ClusterId));
        var profile = StrikeVolley.StationProfile.From(core,
            core.StationTypes[massedStation.Type.StationTypeId]);
        Assert.True(profile.MaxArmor >= 20000f);
        Assert.True(profile.MaxShield >= 10000f);
        output.WriteLine($"garrison types: massed={massedStation.Type.StationTypeId} " +
            $"trickle={trickleStation.Type.StationTypeId} armor={profile.MaxArmor} shield={profile.MaxShield} " +
            $"regen={profile.ArmorRegen}/{profile.ShieldRegen} defense={profile.ArmorDefense}/{profile.ShieldDefense}");

        var bomber = StealthAb2();
        // How many of these bombers the real station needs (3 for a plain
        // garrison, 4 for the Str garrison). The SAME number is used for both
        // schedules: only the release timing differs.
        int bombers = StrikeVolley.Compute(profile, [bomber], hullsAvailable: 12).KillBombers;
        var massedSchedule = new List<(double At, StrikeVolley.Shot Shot)>();
        for (int i = 0; i < bombers; i++)
            foreach (var shot in StrikeVolley.Schedule(bomber))
                massedSchedule.Add((shot.Time, shot));
        var trickleSchedule = new List<(double At, StrikeVolley.Shot Shot)>();
        for (int wave = 0; wave < bombers; wave++)
            foreach (var shot in StrikeVolley.Schedule(bomber))
                trickleSchedule.Add((wave * 60 + shot.Time, shot));

        var massed = Apply(massedStation, massedSchedule, horizon: 30);
        var trickle = Apply(trickleStation, trickleSchedule, horizon: 240);
        var pureMassed = StrikeVolley.Replay(profile, massedSchedule.Select(s => (s.At, bomber, s.Shot)).ToArray(), 30);
        var pureTrickle = StrikeVolley.Replay(profile, trickleSchedule.Select(s => (s.At, bomber, s.Shot)).ToArray(), 240);
        output.WriteLine($"massed real: {massed}  pure: {pureMassed}");
        output.WriteLine($"trickle real: {trickle}  pure: {pureTrickle}");

        Assert.True(massed.Dead, "the massed group must destroy the real garrison");
        Assert.False(trickle.Dead, "the 60 s trickle must NOT destroy the same garrison");
        Assert.True(massed.Hits <= massedSchedule.Count);
        Assert.True(trickle.Hits == trickleSchedule.Count);
        // Both the planner's replay (raw station-type durability) and the real
        // station (GA-scaled EffectiveMax*) must agree on the OUTCOME. The real
        // fractions can differ slightly because the live station applies the
        // side's Global Attributes to max armour/shield/regen (Station.cs:698+),
        // which the pure planner deliberately does not model.
        Assert.Equal(pureMassed.Killed, massed.Dead);
        Assert.Equal(pureTrickle.Killed, trickle.Dead);
        Assert.True(trickle.Armor > 0f);
        // Native regenerates armor before shields (stationIGC.h:189-213).
        // This compatible garrison survives with armor still below full, so its
        // depleted shield has not begun recovering yet.
        Assert.Equal(0f, trickle.Shield);
        // Hits inside one 20 s volley window: the massed run lands its whole
        // kill volley, the trickle lands only the first bomber's pair.
        int massedInWindow = massedSchedule.Count(s => s.At <= StrikeVolley.VolleyWindowSeconds);
        int trickleInWindow = trickleSchedule.Count(s => s.At <= StrikeVolley.VolleyWindowSeconds);
        output.WriteLine($"hitsInWindow massed={massedInWindow} trickle={trickleInWindow}");
        Assert.True(massedInWindow >= bombers * 2);
        Assert.True(trickleInWindow <= 2);
    }
}

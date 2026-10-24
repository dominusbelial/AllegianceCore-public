using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P802 — the server half of the team lobby's civilization picker
/// (<c>FM_CS_CHANGE_TEAM_CIV(51)</c>, <c>Inc/Messages.h:407-411</c>).
///
/// <para>Native's handler (<c>FedSrv/FedSrv.CPP:7344-7366</c>) forwards the
/// message whole-mission and then calls <c>CFSMission::SetSideCiv</c>
/// (<c>FedSrv/fsmission.cpp:5268-5284</c>). The forwarding + leader/stage gates
/// already lived in <c>Server.Dispatch.cs</c> (P186); this file adds the parts
/// P186 deferred:</para>
/// <list type="bullet">
/// <item><b>Random factions</b> — <c>random</c> is honoured: the port resolves
/// it to a concrete civilization instead of ignoring it. Native resolves the
/// random pick on the CLIENT (<c>WinTrek/teamscreen.cpp:1062-1075</c>
/// <c>randomInt(0, numCivilizations-1)</c> over the legal civ list, then sends
/// that civID with <c>random=true</c>) and the mission owner re-rolls random
/// sides every 5 s until the game starts (<c>teamscreen.cpp:2662-2695</c>).
/// Nullable sides at mission creation are likewise filled from a random legal
/// civ (<c>fsmission.cpp:175-198</c>, <c>InitializeCivList</c> +
/// <c>randomInt</c>). The port makes the pick server-authoritative so every
/// peer, the authoritative side data and the relayed message agree without the
/// owner re-roll loop; the legal pool and the <c>&lt; 300</c> "prevent special
/// civs" rule are the native client's (<c>teamscreen.cpp:2667</c>,
/// <c>:897-916</c>; <c>civID &lt; 300</c>).</item>
/// <item><b>The <c>SetSideCiv</c> side effects that matter pre-start</b> — the
/// native per-ship persist-score reset (<c>fsmission.cpp:5274-5283</c>) and the
/// <c>IsideIGC::SetCivilization</c> work that makes the side follow the new civ
/// (<c>Igc/sideigc.h:76-102</c>: development techs = the new civ's base techs,
/// building techs dropped, every station turned into the new civ's initial
/// station type). The port also re-selects the side's research plan
/// (<c>mission.ResearchPlans</c>, keyed by civ) so the research commander and
/// the briefing follow the pick instead of the boot-time civ.</item>
/// </list>
/// </summary>
public sealed partial class Server
{
    /// <summary>
    /// <c>teamscreen.cpp:2667</c>/<c>:897-916</c> — the native client's
    /// "NYI hack to prevent special civs" cut: only civs whose object id is
    /// below 300 are offered (and randomly re-rolled) to non-cheat players.
    /// </summary>
    internal const short SpecialCivIdMin = 300;

    /// <summary>Every civilization the lobby picker offers: the mission core's
    /// civs below <see cref="SpecialCivIdMin"/>, in id order
    /// (<c>teamscreen.cpp:897-916</c>). Used by the random resolver so a
    /// <c>random=true</c> request never resolves to a special/bonus civ.</summary>
    internal static DataCivilizationIGC[] LegalLobbyCivs(Mission mission) =>
        mission.Core.Civilizations.Values
            .Where(c => c.CivilizationId >= 0 && c.CivilizationId < SpecialCivIdMin)
            .OrderBy(c => c.CivilizationId)
            .ToArray();

    /// <summary>
    /// Resolve a <c>random=true</c> CHANGE_TEAM_CIV to a concrete civ: native's
    /// <c>randomInt(0, numCivilizations-1)</c> over the legal list
    /// (<c>teamscreen.cpp:1065-1067</c>, <c>:2683-2685</c>). Drawn from the
    /// server's seeded <see cref="_rand"/> (the same stream mission-creation civ
    /// randomisation uses, <c>fsmission.cpp:191</c>). Returns false when the
    /// core offers no legal civ (a malformed core — nothing to pick).
    /// </summary>
    private bool TryPickRandomCiv(Mission mission, out DataCivilizationIGC civ)
    {
        var legal = LegalLobbyCivs(mission);
        if (legal.Length == 0)
        {
            civ = default;
            return false;
        }
        civ = legal[_rand.Next(legal.Length)];
        return true;
    }

    /// <summary>
    /// Resolve a <c>CHANGE_TEAM_CIV</c> request to the civilization the side will
    /// actually take and the civ id the whole-mission relay carries. A direct
    /// request must name a civ present in the mission core (native
    /// <c>TrapHackBoot(pciv)</c>, <c>FedSrv.CPP</c>:7361-7362); a random request
    /// resolves through the legal lobby pool instead of trusting its civID, and
    /// the relay carries that resolved id (the shape native's client already
    /// sends — <c>teamscreen.cpp:1065-1075</c>).
    /// </summary>
    private bool TryResolveTeamCiv(Mission mission, ChangeTeamCiv request,
        out DataCivilizationIGC civ, out short resolvedCivId)
    {
        if (request.Random)
        {
            if (!TryPickRandomCiv(mission, out civ))
            {
                resolvedCivId = request.CivId;
                return false;
            }
            resolvedCivId = civ.CivilizationId;
            return true;
        }
        resolvedCivId = request.CivId;
        return mission.Core.Civilizations.TryGetValue(request.CivId, out civ);
    }

    /// <summary>
    /// Apply a civilization change to <paramref name="side"/> with the native
    /// <c>CFSMission::SetSideCiv</c> + <c>IsideIGC::SetCivilization</c> side
    /// effects that matter before the game starts:
    /// <list type="number">
    /// <item>the side's civilization (<c>fsmission.cpp:5270</c>
    /// <c>pside-&gt;SetCivilization(pciv)</c>);</item>
    /// <item>development techs := the new civ's base techs, the native
    /// <c>sideigc.h:88</c> <c>m_data.ttbmDevelopmentTechs =
    /// GetCivilization()-&gt;GetBaseTechs()</c> — so the docked shop, the
    /// investment pane and the buy gate follow the new civ;</item>
    /// <item>every station of the side retyped to the new civ's initial station
    /// type, the native <c>sideigc.h:92-100</c> loop
    /// (<c>pstation-&gt;SetBaseStationType(pciv-&gt;GetInitialStationType())</c>),
    /// via the port's <see cref="Station.SetBaseStationType"/> (which also
    /// fires <c>StationTypeChange</c> and re-notifies the side's building techs
    /// — native <c>stationIGC.cpp:210-276</c>);</item>
    /// <item>the native per-player persist-score reset
    /// (<c>fsmission.cpp:5274-5283</c>) per ship on the side —
    /// <see cref="PlayerScore.ResetToPersistScoreForCiv"/>.</item>
    /// </list>
    /// Building techs need no explicit clear: the port derives
    /// <see cref="Side.BuildingTechs"/> from the owned stations
    /// (recompute-on-access), so retyping them is the clear (native's
    /// <c>m_ttbmBuildingTechs.ClearAll()</c>, <c>sideigc.h:89</c>).
    /// <para><b>Out of scope, stated:</b> native's <c>SetCivilization</c> only
    /// reverts the side's stations while the game has not started, and career
    /// persistence does not exist in this port at all (P193/P583) — so the
    /// "persist score for this civ" copy collapses to the neutral
    /// zero score, and no career rank/leaderboard data is touched.</para>
    /// </summary>
    private void ApplySideCivilization(Mission mission, Side side, DataCivilizationIGC civ)
    {
        short previous = side.Civilization.CivilizationId;
        side.Civilization = civ;
        side.DevelopmentTechs = TechTreeBitMask.FromBytes(civ.TtbmBaseTechs);

        int retyped = 0;
        if (mission.Core.StationTypes.TryGetValue(civ.InitialStationTypeId, out var homeType))
            foreach (var station in mission.Sectors.SelectMany(s => s.StationRuntimes))
                if (station.SideId == side.Id && station.Type.StationTypeId != homeType.StationTypeId)
                {
                    station.SetBaseStationType(homeType);
                    retyped++;
                }

        int reset = ResetSidePlayerScores(mission, side.Id);
        Console.WriteLine($"[changeciv] side {side.Id}: civ {previous} '{SideCivName(mission, previous)}' -> " +
                          $"{civ.CivilizationId} '{civ.Name}'; devTechs={side.DevelopmentTechs.PopCount} bits " +
                          $"(civ base techs); stations retyped={retyped} to {civ.InitialStationTypeId}; " +
                          $"player scores reset={reset}");
    }

    private static string SideCivName(Mission mission, short civId) =>
        mission.Core.Civilizations.TryGetValue(civId, out var civ) ? civ.Name : $"civ{civId}";

    /// <summary>
    /// Native <c>SetSideCiv</c>'s per-ship score loop
    /// (<c>fsmission.cpp:5274-5283</c>): every ship on the side gets
    /// <c>ppso-&gt;SetPersist(pfsship-&gt;GetPlayer()->GetPersistPlayerScore(civID))</c>
    /// — its live score becomes that player's persistent score for the NEW
    /// civilization. There is no persistent per-civ score store in this port
    /// (P193/P583: connection-lifetime only), so the copy is the native
    /// default-constructed score and this resets the live counters to neutral.
    /// In a pre-start lobby those counters are already zero — the call is the
    /// native ordering, not a behaviour change for a normal lobby. Only
    /// player-owned peers have a score object (NPC ships do not).
    /// </summary>
    private int ResetSidePlayerScores(Mission mission, short sideId)
    {
        int reset = 0;
        foreach (var live in _peerShips.Values)
            if (ReferenceEquals(live.Ship.Mission, mission) && live.Ship.SideId == sideId
                && live.Ship.PilotType == Ship.PilotTypePlayer)
            {
                live.PlayerScore.ResetToPersistScoreForCiv(mission.Sides.First(s => s.Id == sideId).Civilization.CivilizationId);
                reset++;
            }
        return reset;
    }

    /// <summary>
    /// P802 — re-select the side's research plan for its (new) civilization and
    /// push the refreshed briefing to the side's peers.
    /// <para>Plans are keyed by civ (<c>data/showcase-plans/&lt;core&gt;/civ&lt;id&gt;-*.json</c>)
    /// and were picked once at mission assembly from the boot-time
    /// <c>games[].civIds</c> (<c>SelectShowcasePlans</c>,
    /// <c>Server.Lifecycle.cs</c>). A lobby civ change must therefore pick the
    /// new civ's plan before the game starts, otherwise the P703 research
    /// commander keeps executing the old civ's plan. This is the port's "pick
    /// the plan at game start" rule: the replacement happens while the mission
    /// is still <c>STAGE_NOTSTARTED</c> (the only stage a civ change is allowed
    /// in), and the game-over rotation rebuilds from the config's civ ids
    /// exactly as native's <c>SetSideCiv</c> leaves
    /// <c>misparms.rgCivID</c> untouched (the assignment is commented out,
    /// <c>fsmission.cpp:5272</c>).</para>
    /// <para>No-op outside research/showcase configurations (no
    /// <c>ResearchPlans</c> entry exists there). A plan that cannot be resolved
    /// for the new civ is logged and the previous plan is kept — refusing the
    /// whole lobby action over plan-directory contents would leave the client's
    /// picker and the server's side disagreeing.</para>
    /// </summary>
    private void ReselectSideResearchPlan(Mission mission, Side side)
    {
        if (!mission.ResearchPlans.ContainsKey(side.Id)) return;   // not a research/showcase game
        if (!_missionConfigs.TryGetValue(mission, out var game)) return;
        bool research = _config.Showcase.TechMode == "research";
        if (!research && game.ResearchPlanDirectory is null) return;
        try
        {
            var selection = ResearchPlanCatalog.Select(mission.Core,
                Path.GetFileNameWithoutExtension(_config.StaticCore), side.Civilization.CivilizationId,
                game.ResearchPlanDirectory ?? Path.Combine(AppContext.BaseDirectory, "showcase-plans"),
                game.ResearchPlans.GetValueOrDefault(side.Id, "auto"), ResearchSettingsFor(game));
            mission.ResearchPlans[side.Id] = selection.Plan;
            Console.WriteLine($"[changeciv] side {side.Id}: research plan -> civ{selection.Plan.CivId} '{selection.Plan.Name}': {selection.Message}");
        }
        catch (Exception ex) when (ex is InvalidDataException or KeyNotFoundException or ArgumentException)
        {
            Console.WriteLine($"[changeciv] side {side.Id}: civ {side.Civilization.CivilizationId} has no usable research plan ({ex.Message}) — keeping the previous plan");
            return;
        }
        // The client executor (P703) and the bot research commander read the
        // briefing, so hand them the re-selected plan now.
        foreach (var peer in _peerShips
                     .Where(kv => ReferenceEquals(kv.Value.Ship.Mission, mission) && kv.Value.Ship.SideId == side.Id)
                     .Select(kv => kv.Key).ToArray())
            SendResearchBriefing(peer, mission, side);
    }

    /// <summary>The P703 briefing payload for one side, or null when the side
    /// has no selected plan (a non-research mission). Extracted from the join
    /// burst so a lobby civ change can re-send it.</summary>
    private ResearchBriefing? ResearchBriefingFor(Mission mission, Side side) =>
        mission.ResearchPlans.TryGetValue(side.Id, out var plan)
            ? new ResearchBriefing
            {
                Epoch = mission.ResearchEpoch, SideId = side.Id, Plan = plan,
                Settings = mission.Research, IncomeMultiplier = _config.Showcase.IncomeMultiplier,
                TechMode = _config.Showcase.TechMode, ResearchTimeScale = mission.ShowcaseResearchTimeScale,
                Loop = _config.Showcase.LoopResearchPlans,
                CompletedDevelopments = side.Buckets.Where(b => b.Completed && b.Development.HasValue)
                    .Select(b => b.Development!.Value.DevelopmentId).ToList(),
            }
            : null;

    /// <summary>Send one side's research production identities + briefing to a
    /// single peer (the join-sync body, reusable after a plan re-selection).</summary>
    private void SendResearchBriefing(ConnectionId to, Mission mission, Side side)
    {
        if (ResearchBriefingFor(mission, side) is not { } briefing) return;
        foreach (var ship in mission.Sectors.SelectMany(s => s.Models).OfType<Ship>().Where(s => s.IsNpc && s.SideId == side.Id))
            SendPeer(to, FedPacket.Pack(ProductionIdentity(ship).Encode()));
        SendPeer(to, FedPacket.Pack(briefing.Encode()));
        Console.WriteLine($"[changeciv] peer {to}: RESEARCH_BRIEFING re-sent side {side.Id} civ{briefing.Plan.CivId} '{briefing.Plan.Name}'");
    }
}

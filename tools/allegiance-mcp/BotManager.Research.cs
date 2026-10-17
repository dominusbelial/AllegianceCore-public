using Allegiance.ClientCore.Session;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Tools.McpSpawner;

internal sealed partial class BotManager
{
    private readonly object _researchGate = new();
    private readonly Dictionary<short, ResearchCommander> _research = new();
    private readonly Dictionary<short, short> _researchRocks = new();
    private Thread? _researchThread;

    /// <summary>P703 side-keyed live research telemetry; worker zero owns all commanders.</summary>
    internal ResearchCommander.Report[] ResearchStatus()
    {
        // Constructor-free fixtures exercise get_perf_stats as well.
        if (_researchGate is null) return [];
        lock (_researchGate) return _research.Values.Select(c => c.Telemetry).OfType<ResearchCommander.Report>().OrderBy(r => r.Side).ToArray();
    }

    /// <summary>Independent low-frequency loop: seating a commander must not block combat decisions.</summary>
    private void ResearchLoop()
    {
        double nextLog = 0;
        var cores = new Dictionary<string, StaticCore>();
        while (!_loopCts.IsCancellationRequested)
        {
            try
            {
                ResearchBriefing? probeBriefing;
                lock (Probe.Gate) probeBriefing = Probe.World.ResearchBriefing;
                if (probeBriefing is not null)
                {
                    ReadyResearchPeer(Probe);
                    foreach (var side in ValidSides().OrderBy(s => s == ProbeSide ? 1 : 0))
                    {
                        if (_loopCts.IsCancellationRequested) break;
                        var peer = EnsureCommander(side);
                        ReadyResearchPeer(peer);
                        peer.SendPing();
                        lock (peer.Gate)
                        {
                            var briefing = peer.World.ResearchBriefing;
                            var initial = peer.World.InitialTechsFor(side);
                            if (briefing is null || briefing.SideId != side || initial is null) continue;
                            if (!cores.TryGetValue(briefing.Plan.Core, out var core) || core.Version != briefing.Plan.CoreVersion)
                            {
                                if (Path.GetFileName(briefing.Plan.Core) != briefing.Plan.Core)
                                    throw new InvalidDataException("research core must be a file basename");
                                core = StaticCore.Load(IgcReader.ParseFile(Path.Combine(Catalog.ArtRoot, briefing.Plan.Core + ".igc")));
                                cores[briefing.Plan.Core] = core;
                            }
                            if (peer.World.Mission?.Stage == MissionStageMessage.StageStarted
                                && peer.World.MyShipId is { } self && peer.World.AutoDonateTargetByShipId.ContainsKey(self))
                                peer.SendAutoDonate(-1); // Keep our own payday share, native opt-out (FedSrv.CPP:5535-5544).
                            lock (_researchGate)
                            {
                                if (!_research.TryGetValue(side, out var commander) || commander.Epoch != briefing.Epoch)
                                {
                                    commander?.ReleaseReservations();
                                    commander = new(side, briefing.Epoch, new ResearchTree(core, briefing.Plan.CivId, briefing.Settings),
                                        briefing.Plan, initial.Value, _researchRocks, briefing.Loop, briefing.ResearchTimeScale);
                                    _research[side] = commander;
                                }
                                foreach (var action in commander.Tick(ResearchSnapshot(peer, side)))
                                    switch (action.Kind)
                                    {
                                        // These are ordinary player actions. No grant, CanBuy bypass or direct sim mutation.
                                        case "donate": peer.SendBucketDonate(action.Id, action.Amount); break;
                                        case "goto":
                                        case "build": peer.SendChat("", ChatTarget.Individual, oidRecipient: action.Id,
                                            commandId: action.Kind == "goto" ? OrderChange.CidGoto : OrderChange.CidBuild, otTarget: (short)IgcObjectType.Asteroid, oidTarget: action.Target); break;
                                        case "chat": peer.SendChat(action.Text, ChatTarget.Team); break;
                                    }
                            }
                        }
                    }
                }
                else
                    lock (_researchGate)
                    {
                        foreach (var commander in _research.Values) commander.ReleaseReservations();
                        _research.Clear();
                    }
                if (Now() >= nextLog)
                {
                    foreach (var row in ResearchStatus()) Console.WriteLine($"[research] side={row.Side} plan={row.Plan} step={row.Step}/{row.TotalSteps} {row.Action} '{row.Name}' progress={row.ProgressPercent:F1}% wallet={row.Money} eta={row.EtaSeconds?.ToString("F1") ?? "unknown"}s techs=[{string.Join(",", row.CompletedTechs)}] stations=[{string.Join(",", row.Stations)}] status={row.Status}");
                    nextLog = Now() + 10;
                }
            }
            catch (OperationCanceledException) when (_loopCts.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (Now() >= nextLog) { Console.WriteLine($"[research] waiting/retrying: {ex.Message}"); nextLog = Now() + 10; }
            }
            _loopCts.Token.WaitHandle.WaitOne(250);
        }
    }

    /// <summary>Ready fresh/rotated seats through PLAYER_READY, as the ordinary MCP probe
    /// does (native fsmission.cpp:5449-5471). A research-only manager has no combat loop.</summary>
    private static void ReadyResearchPeer(ConnectionSession peer)
    {
        lock (peer.Gate)
            if (peer.World.Mission?.Stage == MissionStageMessage.StageNotStarted && peer.World.MyShipId is { } id
                && !peer.World.Roster.ReadyByShipId.GetValueOrDefault(id)) peer.SendPlayerReady(true, id);
    }

    /// <summary>Take one consistent native wire observation under peer.Gate. No socket in the policy.</summary>
    internal static ResearchCommander.Snapshot ResearchSnapshot(ConnectionSession peer, short side)
    {
        var world = peer.World;
        var stations = world.Stations.Values.Where(s => s.SideId == side).ToArray();
        var homeSectors = stations.Select(s => s.ClusterId).ToHashSet();
        var units = world.ResearchProduction.Values.Where(p => p.SideId == side).Select(p =>
            new ResearchCommander.Unit(p.ShipId, p.DroneTypeId, p.StationTypeId,
                world.IsResearchProductionAlive(p.ShipId),
                world.Ships.TryGetValue(p.ShipId, out var ship) ? ship.SectorId : (short)-1)).ToArray();
        var rocks = world.Asteroids.Values.OrderByDescending(a => homeSectors.Contains(a.ClusterId)).ThenBy(a => a.AsteroidId)
            .Select(a => new ResearchCommander.Rock(a.AsteroidId, a.AabmCapabilities,
                !world.BuildingEffects.ContainsKey(a.AsteroidId), world.BuildingEffects.ContainsKey(a.AsteroidId), a.ClusterId)).ToArray();
        double time = world.SimulationTimeSeconds;
        return new(time, world.Mission?.Stage == MissionStageMessage.StageStarted, world.MyMoney ?? 0,
            world.DevelopmentTechsFor(side) ?? TechTreeBitMask.Empty, stations.Select(s => s.StationTypeId).ToArray(),
            new(world.CompletedDevelopments), units, rocks,
            world.BucketFundingFor(side).ToDictionary(kv => kv.Key,
                kv => new ResearchCommander.Funding(kv.Value.InvestedMoney, kv.Value.InvestedTimeMs)));
    }
}

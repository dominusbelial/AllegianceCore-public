using Allegiance.Core.Collision;
using Allegiance.Core.Sim;

namespace Allegiance.Core.Model;

public sealed partial class Sector
{
    internal void InitializeTrainingVisibility(short playerSide)
    {
        foreach (var model in _models)
            if (model is Ship or Probe or Mine or Treasure) model.SideVisibility.BindTraining(playerSide);
        foreach (var station in _stationRuntimes) station.SideVisibility.BindTraining(playerSide);
        foreach (var rock in _mineable) rock.SideVisibility.BindTraining(playerSide);
        foreach (var warp in _warps) warp.SideVisibility.BindTraining(playerSide);
    }

    private void BindTrainingVisibility(SideVisibilityState state)
    {
        if (Mission is { IsTraining: true, TrainingPlayerShipId: { } trainee } training &&
            training.GetShip(trainee)?.SideId is { } playerSide)
            state.BindTraining(playerSide);
    }

    // The embedded host is the local training IGC. Use WinTrek's ThingSite
    // here, leaving FedSrv's multiplayer per-side sweep unchanged.
    // WinTrek/trekigc.cpp:283-314,1996-2010,2057-2197,2216-2249.
    private void ResolveTrainingVisibility(List<VisibilityParticipant> targets,
        Dictionary<short, List<VisibilityParticipant>> scannersBySide,
        List<VisibilityMath.SightBody> occluders, short playerSide)
    {
        var scanners = new Dictionary<ISideVisible, VisibilityParticipant>();
        var ownScanners = new List<VisibilityParticipant>();
        var enemyScanners = new List<VisibilityParticipant>();
        foreach (var group in scannersBySide.Values)
            foreach (var scanner in group)
            {
                // Legacy graph fixtures can retain repeated sector entries.
                // Match native scanner identity, not the bookkeeping copies
                // (Igc/shipIGC.h:200-227 replaces cluster membership).
                if (!scanners.TryAdd(scanner.Body, scanner)) continue;
                // AddScanner includes allies in the player pool regardless of
                // bAllowAlliedViz. WinTrek/trekigc.cpp:289-293.
                if (Mission!.AreSidesFriendly(playerSide, scanner.SideId!.Value)) ownScanners.Add(scanner);
                else enemyScanners.Add(scanner);
            }

        foreach (var target in targets)
        {
            var state = target.Body.SideVisibility;
            state.BindTraining(playerSide);
            bool ownEye = target.SideId == playerSide ||
                (Mission!.AllowAlliedViz && target.SideId is { } owner && Mission.AreSidesFriendly(playerSide, owner));
            if (!ownEye && state.TrainingSpotterFor(playerSide) is { } lastOwn &&
                scanners.TryGetValue(lastOwn, out var previousOwn) &&
                (previousOwn.SideId == playerSide || Mission!.AllowAlliedViz &&
                    Mission.AreSidesFriendly(playerSide, previousOwn.SideId!.Value)))
                ownEye = InTrainingScannerRange(previousOwn, target, occluders);
            if (!ownEye)
                foreach (var scanner in ownScanners)
                    if (InTrainingScannerRange(scanner, target, occluders))
                    {
                        ownEye = true;
                        // The player loop deliberately keeps the LAST successful
                        // scanner; the enemy loop below breaks at the FIRST.
                        // WinTrek/trekigc.cpp:2103-2138,2183-2192.
                        state.SetTrainingSpotter(playerSide, scanner.Body, scanner.Ot, scanner.Oid);
                    }
            state.UpdateTrainingEye(playerSide, ownEye);

            // Native's comparison includes sideless models and allies. It does
            // not check the cached enemy spotter's side, nor clear it on loss.
            // WinTrek/trekigc.cpp:2172-2197.
            const short enemySide = short.MinValue;
            bool enemyEye = target.SideId != playerSide;
            if (!enemyEye && state.TrainingSpotterFor(enemySide) is { } lastEnemy &&
                scanners.TryGetValue(lastEnemy, out var previousEnemy))
                enemyEye = InTrainingScannerRange(previousEnemy, target, occluders);
            if (!enemyEye)
                foreach (var scanner in enemyScanners)
                    if (InTrainingScannerRange(scanner, target, occluders))
                    {
                        enemyEye = true;
                        state.SetTrainingSpotter(enemySide, scanner.Body, scanner.Ot, scanner.Oid);
                        break;
                    }
            state.UpdateTrainingEye(enemySide, enemyEye);
        }
    }

    private bool InTrainingScannerRange(in VisibilityParticipant scanner, in VisibilityParticipant target,
        List<VisibilityMath.SightBody> occluders)
    {
        if (target.Body is Ship { Flag: not null }) return true;
        if (scanner.Body is Ship ship)
        {
            // Igc/shipIGC.h:351-369: ship scanners have a quarter rear range.
            // Station/probe scanners use the spherical range instead.
            Vector3 delta = ship.Orientation.TimesInverse(scanner.Position - target.Position);
            if (delta.Z < 0) delta = delta with { Z = delta.Z * 4 };
            float range = VisibilityMath.DetectionRadius(scanner.Radius, scanner.ScannerRange, target.Radius, target.Signature);
            return delta.LengthSquared() <= range * range && VisibilityMath.LineOfSightExist(scanner.Position,
                new(target.Position, target.Radius), occluders);
        }
        return VisibilityMath.InScannerRange(new(scanner.Position, scanner.Radius), scanner.ScannerRange, Id,
            new(target.Position, target.Radius), target.Signature, Id, occluders);
    }
}

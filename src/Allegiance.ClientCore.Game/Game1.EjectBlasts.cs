using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly EjectBlastCues _ejectBlasts = new();
    private readonly List<ExplosionEntity> _sceneExplosions = new();
    private readonly List<ExplosionSoundEntity> _ejectSoundEvents = new();
    private long _ejectBlastCueCount, _ejectBlastSoundCount;

    // One render-frame snapshot serves the director, effect lights and every
    // explosion quality tier. Called before the camera chooses its subjects.
    private void PrepareEjectBlasts(ClientWorld world, short sector)
    {
        lock (_session.Gate)
        {
            long now = ClientNowMilliseconds;
            _ejectBlasts.BeginFrame(world, sector, now);
            world.PruneExpiredExplosions(now);
            _sceneExplosions.Clear();
            foreach (var blast in world.Explosions)
                if (blast.ClusterId == sector) _sceneExplosions.Add(blast);
            _ejectBlasts.AppendExplosions(world, _sceneExplosions);
            _ejectSoundEvents.Clear();
            _ejectBlasts.TakeSoundEvents(world, _ejectSoundEvents);
            _ejectBlastCueCount += _ejectSoundEvents.Count;
            // Use the same listener, falloff, tier mapping and SoundManager
            // entry point as 207 blasts (WinTrek/trekigc.cpp:90-101,180-192).
            if (_ejectSoundEvents.Count > 0 && _sound is { } sound
                && world.MyShipId is { } me && world.Ships.TryGetValue(me, out var own) && own.Latest is { } pose)
                foreach (var (type, tier, volume, position) in Audio.ExplosionSoundRouter.Resolve(
                    _ejectSoundEvents, own.SectorId, pose.Position))
                {
                    sound.PlayWorldExplosion(tier, volume, position);
                    _ejectBlastSoundCount++;
                    Console.WriteLine($"[eject-blast] cues={_ejectBlastCueCount} sounds={_ejectBlastSoundCount} type={type} tier={tier} position={position}");
                }
        }
    }

    private void ResetEjectBlasts()
    {
        _ejectBlasts.Reset();
        _sceneExplosions.Clear();
        _ejectSoundEvents.Clear();
    }
}

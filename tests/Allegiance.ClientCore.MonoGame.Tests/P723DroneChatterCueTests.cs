using Xunit;

namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>
/// P723 — pins the native drone / constructor chatter SoundID → audio-stem
/// resolver (<see cref="Allegiance.Core.Sim.DroneChatterCue"/>). The numeric
/// SoundIDs start at 1001, including removed slots
/// (~/Development/Allegiance/src/Igc/igc.h:456-464); the stems are the waves bound
/// to each (sounddef.mdl:731-753). The server broadcasts these team chats with
/// the SoundID as the wire <c>ChatMessage.VoiceOver</c>, and the client now
/// resolves it back to a clip so the chatter is audible. Failing-first:
/// <c>DroneChatterCue</c> did not exist before P723, so this test cannot
/// compile on the pre-change tree (CS0246).
/// </summary>
public class P723DroneChatterCueTests
{
    [Theory]
    [InlineData(1323, "vo_miner_enemyonscope")]   // droneEnemyOnScopeSound (sounds.h:376)
    [InlineData(1327, "vo_miner_dontgetpaid")]    // droneTooMuchDamageSound (sounds.h:380)
    [InlineData(1328, "vo_request_miner")]        // droneWhereToSound (sounds.h:381)
    [InlineData(1329, "vo_sal_minerpartial")]     // droneComingHomeEmptySound (sounds.h:382)
    [InlineData(1331, "vo_request_minefield")]    // droneWhereToLayMinefieldSound (sounds.h:384)
    [InlineData(1332, "vo_request_tower")]        // droneWhereToLayTowerSound (sounds.h:385)
    [InlineData(1337, "vo_miner_intransit")]      // droneInTransitSound (sounds.h:390)
    [InlineData(1342, "vo_request_miner")]        // droneTooCrowdedSound (sounds.h:395) — same wave as droneWhereTo
    [InlineData(1343, "vo_cover_builder")]        // constructorRunningSound (sounds.h:396)
    public void DroneChatterIds_ResolveToTheirRealWaves(short id, string stem)
        {
        Assert.Equal(stem, Allegiance.Core.Sim.DroneChatterCue.Resolve(id));
        Assert.Equal(stem, Allegiance.Core.Audio.NativeVoiceCatalog.Sounds[id].Stem);
    }

    [Theory]
    [InlineData(1023)] // weapon-ready cue is handled by WeaponReadyCue, not this family
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(3300)]
    [InlineData(327)] // old zero-based ordinal is not a native SoundID
    [InlineData(341)]
    public void NonChatterIds_ResolveNull(short id)
        => Assert.Null(Allegiance.Core.Sim.DroneChatterCue.Resolve(id));
}

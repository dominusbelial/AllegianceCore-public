using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P676WeaponReadyLiveTests
{
    [SkippableFact]
    public void Actual_weapon_mount_completion_sends_owner_only_ready_text_and_sound_once()
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P676-owner", 67601);
        using var observer = P660NextMatchLiveTests.Connect(server, "P676-observer", 67602);
        var ship = server.Ships.Single(p => p.Ship.Id == owner.World.MyShipId).Ship;
        var type = ship.Mission!.Core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
        var part = ship.MountPart(type, 1, 0, 0)!;
        ship.StationId = null;
        void Advance() => typeof(Ship).GetMethod("AdvanceFlightMounts", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, [1000f]);
        Advance(); Advance();
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(owner,
            () => owner.World.Events.Chats.Any(c => c.VoiceOver == WeaponReadyCue.SoundId)));
        var ready = P660NextMatchLiveTests.Read(owner, () =>
            owner.World.Events.Chats.Count(c => c.VoiceOver == WeaponReadyCue.SoundId && c.Message == $"{part.Type.Name} ready.") == 1);
        Assert.True(ready);
        lock (owner.Gate)
        {
            var cue = Assert.Single(owner.World.Events.Chats, c => c.VoiceOver == WeaponReadyCue.SoundId);
            Assert.Equal(OrderChange.CidNone, cue.CommandID);
            Assert.Equal((short)-1, cue.OidTarget);
            Assert.Equal((short)-1, cue.OtTarget);
        }
        // A ping round trip drains the observer's preceding ordered stream.
        observer.TakePingEchoes();
        observer.SendPing();
        P660NextMatchLiveTests.Pump(server, () => observer.TakePingEchoes().Count > 0);
        Assert.True(P660NextMatchLiveTests.Read(observer, () => observer.World.Events.Chats.All(c => c.VoiceOver != WeaponReadyCue.SoundId)));
    }
}

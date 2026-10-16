using System.Reflection;
using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.ClientCore.Tests;

[Collection("LiveNetwork")]
public sealed class P690EquipmentReadyLiveTests
{
    // Native local notification: afterburnerIGC.cpp:119-120,
    // cloakigc.cpp:75-76, shieldIGC.cpp:82-83. The port carries it over the
    // same real CHATMSG path used for weapon readiness. WinTrek/trekigc.cpp:
    // 2994-2996 gates text to the owner; 4975-4982 includes source crew in audio.
    [SkippableTheory]
    [InlineData(EquipmentType.Cloak)] [InlineData(EquipmentType.Shield)] [InlineData(EquipmentType.Afterburner)]
    public void Utility_mount_completion_delivers_owner_text_and_source_crew_sound_and_rearms(EquipmentType kind)
    {
        Require.Artwork(); using var server = new Srv(); server.SpawnInitialMinersAtBoot = false;
        server.Boot(P660NextMatchLiveTests.Config(false));
        using var owner = P660NextMatchLiveTests.Connect(server, "P690-owner", 69001);
        using var observer = P660NextMatchLiveTests.Connect(server, "P690-observer", 69002);
        using var crew = P660NextMatchLiveTests.Connect(server, "P690-crew", 69003);
        var ship = server.Ships.Single(p => p.Ship.Id == owner.World.MyShipId).Ship;
        var crewShip = server.Ships.Single(p => p.Ship.Id == crew.World.MyShipId).Ship;
        crewShip.ParentShip = ship;
        var type = ship.Mission!.Core.PartTypes.Values.First(p => p.EquipmentType == kind);
        // Use an actual hull socket that accepts this real core part so the
        // cargo round trip exercises legal mounting, including cloak/AB masks.
        ship.ChangeHull(ship.Mission.Core.HullTypes.Values.First(h => h.CanMountPart(type, 0)));
        ship.MountPart(type, 1, 0, 0);
        ship.StationId = null;
        string text = $"{type.Name} ready.";
        void Advance()
        {
            var method = kind == EquipmentType.Afterburner ? "UpdateAfterburner" : "AdvanceFlightMounts";
            object[] args = kind == EquipmentType.Afterburner ? [1000f, true] : [1000f];
            typeof(Ship).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(ship, args);
        }
        int Received()
        {
            lock (owner.Gate)
                return owner.World.Events.Chats.Count(c => c.VoiceOver == WeaponReadyCue.SoundId && c.Message == text);
        }
        Advance(); Advance();
        P660NextMatchLiveTests.Pump(server, () => Received() >= 1);
        Assert.Equal(1, Received());
        Assert.True(ship.TrySwapPartWithCargo(kind, 0, -1));
        Assert.True(ship.TrySwapPartWithCargo(kind, -1, 0));
        Advance(); Advance();
        P660NextMatchLiveTests.Pump(server, () => Received() >= 2);
        Assert.Equal(2, Received());
        P660NextMatchLiveTests.Pump(server, () => P660NextMatchLiveTests.Read(crew,
            () => crew.World.Events.Chats.Count(c => c.VoiceOver == WeaponReadyCue.SoundId) >= 2));
        lock (crew.Gate)
        {
            var sounds = crew.World.Events.Chats.Where(c => c.VoiceOver == WeaponReadyCue.SoundId).ToArray();
            Assert.Equal(2, sounds.Length);
            Assert.All(sounds, cue =>
            {
                Assert.Empty(cue.Message);
                Assert.Equal(crewShip.Id, cue.OidRecipient);
            });
        }
        lock (owner.Gate)
            Assert.All(owner.World.Events.Chats.Where(c => c.Message == text), cue =>
            {
                Assert.Equal(WeaponReadyCue.SoundId, cue.VoiceOver);
                Assert.Equal((int)ChatTarget.Individual, cue.ChatTarget);
                Assert.Equal(ship.Id, cue.OidRecipient);
                Assert.Equal(OrderChange.CidNone, cue.CommandID);
                Assert.Equal((short)-1, cue.OidTarget);
                Assert.Equal((short)-1, cue.OtTarget);
            });
        // A ping echo drains the observer's preceding ordered stream.
        observer.TakePingEchoes(); observer.SendPing();
        P660NextMatchLiveTests.Pump(server, () => observer.TakePingEchoes().Count > 0);
        Assert.True(P660NextMatchLiveTests.Read(observer,
            () => observer.World.Events.Chats.All(c => c.VoiceOver != WeaponReadyCue.SoundId)));
    }
}

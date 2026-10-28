using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Allegiance.Core.Identity;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.Server;
using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
[Trait("Requires", "Artwork")]
public sealed class P864cDuplicateLogonTests
{
    private sealed class World : IDisposable
    {
        public readonly Srv Server = new() { SpawnInitialMinersAtBoot = false };
        public readonly List<(ConnectionId Conn, byte[] Message)> Output = [];
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly string _dir;
        public World(bool refuse = false)
        {
            Require.Artwork();
            _dir = Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "p864c-identity-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
            string path = Path.Combine(_dir, "public.pem"); File.WriteAllText(path, _key.ExportSubjectPublicKeyInfoPem());
            Server.Boot(new ServerConfig
            {
                ArtPath = TestArtRoot.Value, StaticCore = "PCore001.igc", Port = 0,
                AlwaysRefuseDuplicateLogons = refuse,
                Identity = new() { Issuer = "issuer", Audience = "server", PublicKeyFile = path, AllowAnonymous = true },
                Reconnect = new() { Enabled = true, GraceSeconds = 5 },
                Games = [new() { Name = "P864c", Map = "XXV_simple.igc", Teams = 2, CivIds = [1, 3], SideNames = ["A", "B"] }],
            });
            Server._testSendCapture = (c, p) => Output.AddRange(FedPacket.Unpack(p).Select(m => (c, m)));
        }
        public object? Call(string method, params object[] args) => typeof(Srv).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Server, args);
        public void Send(int conn, IFedMessage message) => Call("ProcessDatagram", new ConnectionId((uint)conn), FedPacket.Pack([message.Encode()]));
        public LogonRequest Request(string? account, string name = "Pilot") => new()
        {
            CharacterName = name, FedsrvVer = LogonRequest.MsgVer, CDKey = "same-untrusted-cd-key", SteamID = 12345,
            ZoneTicket = account is null ? null : Encoding.UTF8.GetBytes(JoinTokens.Issue(_key, "issuer", "server", account, name, DateTimeOffset.UtcNow)),
        };
        public LogonAck Logon(int conn, LogonRequest request)
        {
            Send(conn, request);
            return LogonAck.Decode(Output.Last(o => o.Conn.Value == conn && FedMessageHeader.Read(o.Message).Fmid == LogonAck.Fmid).Message);
        }
        public void Select(int conn)
        {
            Send(conn, new PositionReq { SideId = -1 }); Call("AdvanceSideSelection", Server.SimNow);
        }
        public QuitMission[] Quits(int conn) => Output.Where(o => o.Conn.Value == conn && FedMessageHeader.Read(o.Message).Fmid == QuitMission.Fmid)
            .Select(o => QuitMission.Decode(o.Message)).ToArray();
        public void Dispose() { Server.Dispose(); _key.Dispose(); Directory.Delete(_dir, true); }
    }

    [SkippableFact]
    public void Server_file_map_boot_runs_native_empty_sector_population()
    {
        using var w = new World();
        var mission = w.Server.Missions[0];
        Assert.Empty(mission.Map.Asteroids); // XXV_simple's authored empty sectors
        Assert.All(mission.Sectors, s => Assert.Equal(s.Cluster.HomeSector ? 23 : 25, s.Asteroids.Count));
        Assert.All(mission.Sectors.SelectMany(s => s.Mineable).Where(a => a.CanMineHe3), a => Assert.True(float.IsFinite(a.OreRemaining) && a.OreRemaining > 0));
    }

    [SkippableFact]
    public void Same_verified_account_boots_old_player_with_native_notice_and_admits_new_player()
    {
        using var w = new World();
        var first = w.Logon(1, w.Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")); Assert.True(first.FValidated); w.Select(1);
        var ship = Assert.Single(w.Server.Ships).Ship;
        var second = w.Logon(2, w.Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "PILOT"));
        Assert.True(second.FValidated); Assert.NotEqual(first.ShipID, second.ShipID);
        Assert.Equal(QuitSideReason.DuplicateLocalLogon, Assert.Single(w.Quits(1)).Reason);
        Assert.Null(w.Server.AccountId(new ConnectionId(1)));
        Assert.Equal("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", w.Server.AccountId(new ConnectionId(2)));
        Assert.Empty(w.Server.Ships);
        Assert.Equal(0, w.Server.ReconnectParkedShipCount); // a deliberate boot cannot park
        Assert.DoesNotContain(ship, w.Server.Missions[0].Sectors.SelectMany(s => s.Models.OfType<Allegiance.Core.Sim.Ship>()));
        w.Select(2);
        Assert.Equal(second.ShipID, Assert.Single(w.Server.Ships).Ship.Id);
        Assert.True(w.Logon(3, w.Request("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", "Observer")).FValidated);
        w.Select(3);
        Assert.True((bool)w.Call("RemovePlayerByName", w.Server.Missions[0], "pilot", QuitSideReason.AdminBooted, "moderated")!);
        Assert.Equal("moderated", Assert.Single(w.Quits(2)).Message);
        Assert.Equal("moderated", Assert.Single(w.Quits(3)).Message);
        Assert.False((bool)w.Call("RemovePlayerByName", w.Server.Missions[0], "missing", QuitSideReason.AdminBooted, "")!);
    }

    [SkippableTheory]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", null, false)]
    [InlineData(null, "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", false)]
    [InlineData(null, null, false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    public void Different_or_unverified_accounts_and_opt_in_always_refuse_cannot_evict(string? oldAccount, string? newAccount, bool refuse)
    {
        using var w = new World(refuse);
        var first = w.Logon(1, w.Request(oldAccount)); Assert.True(first.FValidated); w.Select(1);
        var second = w.Logon(2, w.Request(newAccount, "PILOT"));
        Assert.False(second.FValidated); Assert.Equal(0, second.ShipID);
        Assert.Contains("already in use", second.CharNameOrFailureReason);
        Assert.Empty(w.Quits(1)); Assert.Null(w.Server.AccountId(new ConnectionId(2)));
        w.Select(2);
        Assert.Equal(first.ShipID, Assert.Single(w.Server.Ships).Ship.Id);
    }

    [SkippableFact]
    public void Pending_name_reservation_and_failed_authentication_or_version_never_boot_an_existing_player()
    {
        using var w = new World();
        var firstRequest = w.Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");
        var first = w.Logon(1, firstRequest);
        Assert.False(w.Logon(2, w.Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "PILOT")).FValidated);
        w.Select(1);
        Assert.False(w.Logon(3, firstRequest).FValidated); // consumed token
        var wrongVersion = w.Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"); wrongVersion.FedsrvVer = 0;
        Assert.False(w.Logon(4, wrongVersion).FValidated);
        var forged = w.Request("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"); forged.ZoneTicket = Encoding.UTF8.GetBytes("forged.token.signature");
        Assert.False(w.Logon(5, forged).FValidated);
        Assert.Empty(w.Quits(1));
        Assert.Equal(first.ShipID, Assert.Single(w.Server.Ships).Ship.Id);
    }
}

public sealed class P864cDuplicateLogonConfigTests
{
    [Fact]
    public void Default_json_and_environment_select_the_duplicate_logon_policy()
    {
        var config = new ServerConfig(); Assert.False(config.AlwaysRefuseDuplicateLogons);
        config = JsonSerializer.Deserialize("{\"alwaysRefuseDuplicateLogons\":true}", ServerJsonContext.Default.ServerConfig)!;
        Assert.True(config.AlwaysRefuseDuplicateLogons);
        config.ApplyDuplicateLogonEnvironment(""); Assert.True(config.AlwaysRefuseDuplicateLogons);
        config.ApplyDuplicateLogonEnvironment("0"); Assert.False(config.AlwaysRefuseDuplicateLogons);
        config.ApplyDuplicateLogonEnvironment("1"); Assert.True(config.AlwaysRefuseDuplicateLogons);
        config.ApplyDuplicateLogonEnvironment("false"); Assert.False(config.AlwaysRefuseDuplicateLogons);
        Assert.Throws<InvalidDataException>(() => config.ApplyDuplicateLogonEnvironment("invalid"));
    }
}

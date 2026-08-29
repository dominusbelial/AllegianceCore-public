using System.Linq;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

public class LogonRoundTripTests
{
    private static LogonRequest SampleRequest() => new()
    {
        CharacterName = "Komrad",
        ZoneTicket = new byte[] { 0x01, 0x02, 0x03, 0xAA, 0xBB },
        CDKey = "DOMINIUS",
        MissionPassword = "secret",
        FedsrvVer = 216,
        Time = 987654321u,
        DwCookie = 0xCAFE_BABEu,
        CrcFileList = -12345,
        SteamAuthTicket = new byte[1024],
        SteamAuthTicketLength = 999,
        SteamID = 76561198000000000ul,
        DrmHash = "drmhash123",
    };

    [Fact]
    public void LogonRequest_RoundTrips_AllValues_IncludingVariableItems()
    {
        var req = SampleRequest();
        req.SteamAuthTicket![0] = 0xAB; req.SteamAuthTicket[1023] = 0xCD;

        var decoded = LogonRequest.Decode(req.Encode());

        Assert.Equal(req.CharacterName, decoded.CharacterName);
        Assert.Equal(req.ZoneTicket, decoded.ZoneTicket);
        Assert.Equal(req.CDKey, decoded.CDKey);
        Assert.Equal(req.MissionPassword, decoded.MissionPassword);
        Assert.Equal(req.FedsrvVer, decoded.FedsrvVer);
        Assert.Equal(req.Time, decoded.Time);
        Assert.Equal(req.DwCookie, decoded.DwCookie);
        Assert.Equal(req.CrcFileList, decoded.CrcFileList);
        Assert.Equal(req.SteamAuthTicket, decoded.SteamAuthTicket);
        Assert.Equal(req.SteamAuthTicketLength, decoded.SteamAuthTicketLength);
        Assert.Equal(req.SteamID, decoded.SteamID);
        Assert.Equal(req.DrmHash, decoded.DrmHash);
    }

    [Fact]
    public void ZtsString_TrailingSpacesTrimmed_And_EmptyString_GetsSingleNul()
    {
        // "Komrad   " -> "Komrad\0" (cb = 7); empty "" -> "\0" (cb = 1).
        var spaced = new LogonRequest { CharacterName = "Komrad   ", FedsrvVer = 216 };
        var b = spaced.Encode();
        Assert.Equal((ushort)(LogonRequest.FixedSize + 7), FedMessageHeader.Read(b).CbMsg);
        var d = LogonRequest.Decode(b);
        Assert.Equal("Komrad", d.CharacterName);

        var empty = new LogonRequest { MissionPassword = "", FedsrvVer = 216 }.Encode();
        Assert.Equal((ushort)(LogonRequest.FixedSize + 1), FedMessageHeader.Read(empty).CbMsg);
        Assert.Equal("", LogonRequest.Decode(empty).MissionPassword);
    }

    [Fact]
    public void LogonAck_RoundTrips_AllValues()
    {
        var ack = new LogonAck
        {
            CharNameOrFailureReason = "Welcome Komrad",
            TimeServer = 777,
            ShipID = -42,
            FValidated = true,
            FRetry = false,
            Cookie = 0x7F,
        };
        var decoded = LogonAck.Decode(ack.Encode());
        Assert.Equal(ack.CharNameOrFailureReason, decoded.CharNameOrFailureReason);
        Assert.Equal(ack.TimeServer, decoded.TimeServer);
        Assert.Equal(ack.ShipID, decoded.ShipID);
        Assert.Equal(ack.FValidated, decoded.FValidated);
        Assert.Equal(ack.FRetry, decoded.FRetry);
        Assert.Equal(ack.Cookie, decoded.Cookie);
    }

    [Fact]
    public void LogonAck_NullReason_BecomesEmptyVar_NotCrash()
    {
        var ack = new LogonAck { FValidated = false, Cookie = 1 }.Encode();
        var d = LogonAck.Decode(ack);
        Assert.Null(d.CharNameOrFailureReason);
        Assert.False(d.FValidated);
    }
}

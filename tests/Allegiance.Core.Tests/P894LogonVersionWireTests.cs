using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests;

/// <summary>
/// P894 — the LOGONREQ client-version port extension. Native's version identity at
/// logon was the file-list CRC (<c>Inc/Messages.h:44</c>); the port adds a tagged
/// trailing blob (<c>"P894"</c> + u32 length + bytes) so the game server can enforce
/// the advertised minimum client version at the logon the port's client performs.
/// The native fixed region and var items are untouched, and a message without the
/// extension is byte-identical to the pre-P894 shape.
/// </summary>
public sealed class P894LogonVersionWireTests
{
    private static LogonRequest Request() => new()
    {
        CharacterName = "WIRECHECK",
        ZoneTicket = [0x01, 0x02, 0x03],
        CDKey = "ABCDEFGHIJKL",
        MissionPassword = "pw",
        FedsrvVer = LogonRequest.MsgVer,
        Time = 894u,
        DwCookie = uint.MaxValue,
        CrcFileList = 0x1234,
        SteamAuthTicket = new byte[16],
        SteamAuthTicketLength = 0,
        SteamID = 89400000000000002ul,
        DrmHash = "p894wire",
    };

    /// <summary>The fixed region + the four native variable blobs, with no port
    /// extension: the exact pre-P894 wire length.</summary>
    private static int NativeLength(LogonRequest r)
        => LogonRequest.FixedSize
           + 1 + r.CharacterName!.Length                       // CB_ZTS
           + r.ZoneTicket!.Length                              // length blob
           + 1 + r.CDKey!.Length
           + 1 + r.MissionPassword!.Length;

    [Fact]
    public void Without_a_version_the_message_is_the_native_shape()
    {
        var r = Request();
        r.ClientVersion = null;

        byte[] bytes = r.Encode();

        Assert.Equal(NativeLength(r), bytes.Length);
        var decoded = LogonRequest.Decode(bytes);
        Assert.Null(decoded.ClientVersion);
        Assert.Equal(0u, decoded.ResumeToken);
    }

    [Fact]
    public void The_client_version_round_trips()
    {
        var r = Request();
        r.ClientVersion = "2.0.0";

        byte[] bytes = r.Encode();
        var decoded = LogonRequest.Decode(bytes);

        Assert.Equal("2.0.0", decoded.ClientVersion);
        Assert.Equal(0u, decoded.ResumeToken);
        Assert.Equal(NativeLength(r) + 8 + "2.0.0".Length, bytes.Length);
        // Native fields are unaffected by the extension.
        Assert.Equal("WIRECHECK", decoded.CharacterName);
        Assert.Equal(0x1234, decoded.CrcFileList);
    }

    [Fact]
    public void The_resume_token_and_the_client_version_coexist()
    {
        var r = Request();
        r.ResumeToken = 0xC0FFEE01u;
        r.ClientVersion = "1.9.9-rc1";

        var decoded = LogonRequest.Decode(r.Encode());

        Assert.Equal(0xC0FFEE01u, decoded.ResumeToken);
        Assert.Equal("1.9.9-rc1", decoded.ClientVersion);
    }

    [Fact]
    public void The_resume_token_alone_still_decodes_as_before()
    {
        var r = Request();
        r.ResumeToken = 0xDEADBEEF;
        r.ClientVersion = null;

        var decoded = LogonRequest.Decode(r.Encode());

        Assert.Equal(0xDEADBEEF, decoded.ResumeToken);
        Assert.Null(decoded.ClientVersion);
        Assert.Equal(NativeLength(r) + 8, r.Encode().Length);
    }

    [Fact]
    public void An_empty_client_version_is_omitted_not_encoded()
    {
        var r = Request();
        r.ClientVersion = "";

        byte[] bytes = r.Encode();
        Assert.Equal(NativeLength(r), bytes.Length);       // no extension bytes at all
        Assert.Null(LogonRequest.Decode(bytes).ClientVersion);
    }
}

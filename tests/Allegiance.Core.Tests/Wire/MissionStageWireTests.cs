using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests.Wire;

/// <summary>
/// Round-trip + byte-level tests for <see cref="MissionStageMessage"/>
/// (FM_S_MISSION_STAGE 37), ported from <c>Messages.h:290-292</c>
/// (<c>STAGE stage;</c>, a <c>[v1_enum]</c> int32 per <c>AGCIDL.idl:480-489</c>).
/// Total = 4 hdr + 4 stage = 8 bytes.
/// </summary>
public class MissionStageWireTests
{
    [Fact]
    public void MissionStage_FixedSizes_AreDeclared()
    {
        Assert.Equal((ushort)37, MissionStageMessage.Fmid);
        Assert.Equal(8, MissionStageMessage.FixedSize);
    }

    [Fact]
    public void MissionStage_RoundTrips_ThroughWire()
    {
        var msg = new MissionStageMessage { Stage = MissionStageMessage.StageStarted };
        var d = MissionStageMessage.Decode(msg.Encode());
        Assert.Equal(MissionStageMessage.StageStarted, d.Stage);

        msg = new MissionStageMessage { Stage = MissionStageMessage.StageOver };
        d = MissionStageMessage.Decode(msg.Encode());
        Assert.Equal(MissionStageMessage.StageOver, d.Stage);
    }

    [Fact]
    public void MissionStage_EmitsExactDeclaredBytes()
    {
        var msg = new MissionStageMessage { Stage = MissionStageMessage.StageStarted };
        var got = msg.Encode();

        //                       cbmsg  fmid   stage(le, int32=2)
        var exp = new byte[] { 0x08, 0x00, 0x25, 0x00, 0x02, 0x00, 0x00, 0x00 };

        Assert.Equal(8, exp.Length);
        Assert.Equal(exp, got);
    }

    [Theory]
    [InlineData(MissionStageMessage.StageNotStarted)]
    [InlineData(MissionStageMessage.StageStarting)]
    [InlineData(MissionStageMessage.StageStarted)]
    [InlineData(MissionStageMessage.StageOver)]
    [InlineData(MissionStageMessage.StageTerminate)]
    public void MissionStage_AllStageValues_RoundTrip(int stage)
    {
        var d = MissionStageMessage.Decode(new MissionStageMessage { Stage = stage }.Encode());
        Assert.Equal(stage, d.Stage);
    }

    [Fact]
    public void MissionStage_RejectsWrongFmid_AndTruncatedBuffer()
    {
        var bytes = new MissionStageMessage { Stage = MissionStageMessage.StageStarted }.Encode();

        var badFmid = (byte[])bytes.Clone();
        badFmid[2] = 99;
        Assert.Throws<ArgumentException>(() => MissionStageMessage.Decode(badFmid));

        Assert.Throws<ArgumentException>(() => MissionStageMessage.Decode(bytes.AsSpan(0, 7)));
    }
}

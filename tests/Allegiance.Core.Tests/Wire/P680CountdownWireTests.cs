using System.Buffers.Binary;
using Allegiance.Core.Wire;

namespace Allegiance.Core.Tests.Wire;

public sealed class P680CountdownWireTests
{
    [Fact]
    public void Duration_and_start_epoch_use_native_MissionParams_offsets_and_leave_neighbors_intact()
    {
        // igc.h:1160-1169; timeStart offset 156 confirmed by P29 MSVC probe.
        var bytes = new MissionDef { GameLengthSeconds = 900.25f, TimeStart = 0xFEDCBA98,
            VerIgcCore = 1234, RgCivId = [1, 3], NTems = 2 }.Encode();
        Assert.Equal(900.25f, BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(4 + 132)));
        Assert.Equal(0xFEDCBA98u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 + 156)));
        Assert.All(bytes[128..136], b => Assert.Equal(0, b)); // treasure rates
        Assert.All(bytes[140..160], b => Assert.Equal(0, b)); // density + money
        Assert.All(bytes[164..172], b => Assert.Equal(0, b)); // restart settings
        var decoded = MissionDef.Decode(bytes);
        Assert.Equal(900.25f, decoded.GameLengthSeconds);
        Assert.Equal(0xFEDCBA98u, decoded.TimeStart);
        Assert.Equal(1234, decoded.VerIgcCore);
        Assert.Equal(new short[] { 1, 3, 0, 0, 0, 0 }, decoded.RgCivId);
        Assert.Equal((short)2, decoded.NTems);

        // Decode independently composed native fields, not only our encoder.
        bytes = new MissionDef().Encode();
        BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(136), 600.5f);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(160), 250_000);
        decoded = MissionDef.Decode(bytes);
        Assert.Equal(600.5f, decoded.GameLengthSeconds);
        Assert.Equal(250_000u, decoded.TimeStart);
    }
}

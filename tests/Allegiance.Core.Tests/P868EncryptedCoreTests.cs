using Allegiance.TestInfra;
using Srv = Allegiance.Server.Server;

namespace Allegiance.Core.Tests;

[Collection("LiveNetwork")]
public sealed class P868EncryptedCoreTests
{
    // Independent native DoEncrypt word calculation, FedSrv.CPP:42-56:
    // P=00080000 22110000 66554433 00008877 (last word padded)
    // E=P xor preceding P, with initial preceding P=0.
    private static readonly byte[] Plain = Convert.FromHexString("0000080000001122334455667788");
    private static readonly byte[] Cipher = Convert.FromHexString("00000800000019223344444444CC");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Native_rolling_xor_decodes_payload_before_walking_and_leaves_prefix(bool versioned)
    {
        byte[] input = Wrap(Cipher, versioned);
        byte[] original = input.ToArray();
        var decoded = IgcReader.Parse(input, encrypted: true);
        Assert.Equal(Plain, decoded.Body.ToArray());
        Assert.Equal(original, input); // caller's captured bytes stay immutable
        Assert.True(decoded.IsExact);
        Assert.Equal(versioned ? 42 : (int?)null, decoded.Version);
        Assert.Equal(8, Assert.Single(decoded.Records).Size);
        // This ciphertext also has a valid record header; format selection
        // cannot depend on whether the plain record walker succeeds.
        Assert.True(IgcReader.Parse(input).IsExact);
        Assert.NotEqual(Plain, IgcReader.Parse(input).Body.ToArray());
    }

    [Fact]
    public void File_loader_requires_explicit_format_including_native_named_plain_artwork()
    {
        string scratch = Path.Combine(Path.GetTempPath(), "p868-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string native = Path.Combine(scratch, "ZoNe_CoRe.igc");
            string renamed = Path.Combine(scratch, "custom.igc");
            File.WriteAllBytes(native, Wrap(Cipher, true));
            File.WriteAllBytes(renamed, Wrap(Cipher, true));
            Assert.Equal(Plain, IgcReader.ParseFile(native, encrypted: true).Body.ToArray());
            Assert.Equal(Cipher, IgcReader.ParseFile(native).Body.ToArray());
            Assert.Equal(Cipher, IgcReader.ParseFile(native, encrypted: false).Body.ToArray());
            Assert.Equal(Cipher, IgcReader.ParseFile(renamed).Body.ToArray());
            Assert.Equal(Plain, IgcReader.ParseFile(renamed, encrypted: true).Body.ToArray());
            File.WriteAllBytes(native, Wrap(Plain, true));
            Assert.Equal(Plain, IgcReader.ParseFile(native).Body.ToArray());
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    [SkippableFact]
    public void Configured_server_boot_loads_a_renamed_encrypted_real_core()
    {
        Require.Artwork();
        string scratch = Path.Combine(Path.GetTempPath(), "p868-server-core-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            byte[] raw = File.ReadAllBytes(Path.Combine(TestArtRoot.Value, "PCore001.igc"));
            var expected = IgcReader.Parse(raw);
            // Independently apply native DoEncrypt: E[n]=P[n] xor P[n-1].
            byte[] cipher = raw.ToArray();
            for (int offset = 8; offset < cipher.Length; offset++)
                cipher[offset] = (byte)(raw[offset] ^ (offset >= 12 ? raw[offset - 4] : 0));
            File.WriteAllBytes(Path.Combine(scratch, "renamed.igc"), cipher);
            File.Copy(Path.Combine(TestArtRoot.Value, "XXV_simple.igc"), Path.Combine(scratch, "XXV_simple.igc"));
            var config = UpdateCadenceTests.Config();
            config.ArtPath = scratch;
            config.StaticCore = "renamed.igc";
            config.StaticCoreEncrypted = true;
            using var server = new Srv { SpawnInitialMinersAtBoot = false };
            server.Boot(config);
            Assert.Equal(expected.Version, server.Missions[0].Core.Version);
            Assert.NotEmpty(server.Missions[0].Core.HullTypes);
            Assert.NotEmpty(server.Missions[0].Core.Civilizations);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static byte[] Wrap(byte[] body, bool versioned) =>
        (versioned ? BitConverter.GetBytes(42) : [])
        .Concat(BitConverter.GetBytes(body.Length)).Concat(body).ToArray();
}

using Allegiance.Update;

namespace Allegiance.Update.Tests;

/// <summary>P810 — the ECDSA P-256 feed signature (design: no native check at
/// all was an era constraint; a tampered manifest must never stage).</summary>
public class UpdateSignatureTests
{
    private static readonly byte[] Payload = "manifest-bytes"u8.ToArray();

    [Fact]
    public void Sign_then_verify_round_trips()
    {
        var (priv, pub) = UpdateSignature.GenerateKeyPair();
        var sig = UpdateSignature.Sign(Payload, priv);
        Assert.True(UpdateSignature.Verify(Payload, sig, pub));
    }

    [Fact]
    public void One_flipped_byte_is_refused()
    {
        var (priv, pub) = UpdateSignature.GenerateKeyPair();
        var sig = UpdateSignature.Sign(Payload, priv);
        var tampered = Payload.ToArray();
        tampered[3] ^= 0xFF;
        Assert.False(UpdateSignature.Verify(tampered, sig, pub));
    }

    [Fact]
    public void A_wrong_key_is_refused()
    {
        var (priv, _) = UpdateSignature.GenerateKeyPair();
        var (_, otherPub) = UpdateSignature.GenerateKeyPair();
        var sig = UpdateSignature.Sign(Payload, priv);
        Assert.False(UpdateSignature.Verify(Payload, sig, otherPub));
    }

    [Fact]
    public void Garbage_signature_and_key_never_throw()
    {
        var (_, pub) = UpdateSignature.GenerateKeyPair();
        Assert.False(UpdateSignature.Verify(Payload, "!!!not base64!!!", pub));
        Assert.False(UpdateSignature.Verify(Payload, Convert.ToBase64String(new byte[8]), "not a pem"));
    }

    [Fact]
    public void Non_p256_keys_are_rejected_at_signing()
    {
        // A P-384 key must not silently produce a signature the verifier would
        // accept policy-wise: the pipeline pins P-256.
        using var p384 = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP384);
        Assert.Throws<InvalidOperationException>(() =>
            UpdateSignature.Sign(Payload, p384.ExportPkcs8PrivateKeyPem()));
    }
}

/// <summary>P810 — manifest building and path safety (the port's replacement of
/// native's lobby-side FileList CRC computation, lobby/AutoUpdate.cpp:72-76).</summary>
public class UpdateManifestBuilderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "p810-manifest-" + Guid.NewGuid().ToString("N"));

    public UpdateManifestBuilderTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Every_regular_file_gets_a_size_and_sha256()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "alpha");
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "b.txt"), "beta");

        var manifest = UpdateManifestBuilder.Build(_root, "1.0.0");

        Assert.Equal("1.0.0", manifest.Version);
        Assert.Equal(2, manifest.Files.Count);
        var a = manifest.Files.Single(f => f.Path == "a.txt");
        Assert.Equal(5, a.Size);
        Assert.Equal(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData("alpha"u8.ToArray())), a.Sha256);
    }

    [Fact]
    public void The_hd_texture_pack_is_never_in_the_manifest()
    {
        Directory.CreateDirectory(Path.Combine(_root, "packs", "hd"));
        File.WriteAllText(Path.Combine(_root, "packs", "hd", "tex.pbr"), "huge");
        Directory.CreateDirectory(Path.Combine(_root, "hd"));
        File.WriteAllText(Path.Combine(_root, "hd", "other.tex"), "huge");
        File.WriteAllText(Path.Combine(_root, "game.dll"), "app");

        var manifest = UpdateManifestBuilder.Build(_root, "1.0.0");

        Assert.Equal(["game.dll"], manifest.Files.Select(f => f.Path));
    }

    [Fact]
    public void The_updater_state_directory_is_excluded()
    {
        Directory.CreateDirectory(Path.Combine(_root, "autoupdate"));
        File.WriteAllText(Path.Combine(_root, "autoupdate", "installed.json"), "{}");
        File.WriteAllText(Path.Combine(_root, "game.dll"), "app");

        var manifest = UpdateManifestBuilder.Build(_root, "1.0.0");

        Assert.Equal(["game.dll"], manifest.Files.Select(f => f.Path));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/absolute.txt")]
    [InlineData("a/../b.txt")]
    [InlineData("")]
    [InlineData("packs/hd/tex.pbr")]
    [InlineData("a\0b")]
    public void Hostile_paths_are_refused(string path)
        => Assert.False(UpdatePathGuard.TryNormalize(path, out _));

    [Fact]
    public void Plain_relative_paths_normalize()
        => Assert.True(UpdatePathGuard.TryNormalize("Content\\sub\\a.xnb", out var norm)
                       && norm == "Content/sub/a.xnb");
}

/// <summary>P810 — the version gate (native compared the LOGON crcFileList,
/// lobby/client.cpp:272-281; the port compares release versions).</summary>
public class UpdateVersionGateTests
{
    [Theory]
    [InlineData("2.0.0", "1.9.9", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    [InlineData("2.0.0", "2.1.0", false)]
    [InlineData("v2.0.0", "1.9.9", true)]
    public void IsNewer_compares_release_versions(string candidate, string installed, bool expected)
        => Assert.Equal(expected, UpdateVersionGate.IsNewer(candidate, installed));

    [Theory]
    [InlineData("2.0.0", "1.0.0", true)]
    [InlineData("2.0.0", "2.0.0", false)]
    public void IsTooOld_is_the_minimum_version_trigger(string minimum, string installed, bool expected)
        => Assert.Equal(expected, UpdateVersionGate.IsTooOld(installed, minimum));

    [Fact]
    public void Unparsable_versions_never_count_as_newer()
    {
        Assert.False(UpdateVersionGate.IsNewer("banana", "1.0.0"));
        Assert.False(UpdateVersionGate.IsNewer("2.0.0", "banana"));
        Assert.False(UpdateVersionGate.IsTooOld("banana", "1.0.0"));
    }
}

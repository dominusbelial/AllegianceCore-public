using System.Buffers.Binary;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

/// <summary>
/// P870b — certification of the port's static-core file loader rows against the
/// real corpus: SIM-0784 (<c>LoadIGCFile@94</c>: size-prefixed binary, optional
/// transform, import at mission last-update time, model population) and SIM-0787
/// (<c>LoadIGCFile@2334</c>: named core resolution, successful import, observable
/// missing-file branch through the production loading entry point).
/// </summary>
public sealed class P870StaticCoreLoaderTests
{
    private static string CorePath(string name)
        => Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork),
            name.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? name : name + ".igc");

    // ---- SIM-0784 LoadIGCFile@94 (missionigc.cpp:94-111) ----
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Size_prefixed_core_loads_with_optional_transform_and_populates_models()
    {
        Require.Artwork();
        string path = CorePath("PCore001");
        var file = IgcReader.ParseFile(path);

        // Outer wrapper: int32 version + int32 size + body, walked to the end.
        Assert.Equal(IgcWrapper.VersionAndDataSize, file.Wrapper);
        Assert.NotNull(file.Version);
        Assert.True(file.IsExact, $"record walk consumed {file.BytesConsumed} of {file.BodyLength}");
        Assert.True(file.Records.Count > 1000);

        // Every static-core record type the loader supports is populated, and the
        // count matches the record stream (the "mission-time import" step).
        var core = StaticCore.Load(file);
        Assert.Equal(file.CountOf(IgcObjectType.ProjectileType), core.ProjectileTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.MissileType), core.MissileTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.MineType), core.MineTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.ProbeType), core.ProbeTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.ChaffType), core.ChaffTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.HullType), core.HullTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.StationType), core.StationTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.Development), core.Developments.Count);
        Assert.Equal(file.CountOf(IgcObjectType.DroneType), core.DroneTypes.Count);
        Assert.Equal(file.CountOf(IgcObjectType.Civilization), core.Civilizations.Count);
        Assert.Equal(file.CountOf(IgcObjectType.TreasureSet), core.TreasureSets.Count);
        Assert.Equal(
            file.CountOf(IgcObjectType.PartType),
            core.PartTypes.Count + core.Launchers.Count);

        // Selected model types decode with sane values (not just record counts).
        var hull = core.HullTypes.Values.First(h => h.MaxWeapons >= 2);
        Assert.NotEmpty(hull.Name);
        Assert.Equal(hull.MaxWeapons, hull.Hardpoints.Length);
        Assert.True(hull.HitPoints > 0);
        Assert.NotEmpty(core.StationTypes.Values.First().Name);
        Assert.NotEmpty(core.Civilizations.Values.First().Name);
        Assert.Equal(DataConstantsIGC.Size, 1760);
        Assert.True(core.Constants.FloatConstants.Length == DataConstantsIGC.FcidMax);

        // Optional native transform (FedSrv.CPP:31-41 rolling XOR): feeding the
        // re-encrypted body to Parse(encrypted: true) yields the same stream.
        byte[] raw = File.ReadAllBytes(path);
        int version = BinaryPrimitives.ReadInt32LittleEndian(raw);
        int length = BinaryPrimitives.ReadInt32LittleEndian(raw.AsSpan(4));
        byte[] body = raw.AsSpan(8, length).ToArray();
        RollingXor(body);
        byte[] encrypted = new byte[8 + length];
        BinaryPrimitives.WriteInt32LittleEndian(encrypted, version);
        BinaryPrimitives.WriteInt32LittleEndian(encrypted.AsSpan(4), length);
        body.CopyTo(encrypted, 8);

        var decrypted = IgcReader.Parse(encrypted, encrypted: true);
        Assert.Equal(file.Wrapper, decrypted.Wrapper);
        Assert.Equal(file.Version, decrypted.Version);
        Assert.Equal(file.Records, decrypted.Records);
        Assert.True(decrypted.Body.Span.SequenceEqual(file.Body.Span));
    }

    // ---- SIM-0787 LoadIGCFile@2334 (missionigc.cpp:2334-2350) ----
    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Production_resolver_loads_the_named_core_and_returns_null_when_missing()
    {
        Require.Artwork();
        var world = new ClientWorld();

        // Production entry point: ClientWorld.MissionCoreResolver defaults to
        // ClientMissionCoreCache.Resolve (name → <artwork>/<name>.igc).
        var core = world.MissionCoreResolver("PCore001");
        Assert.NotNull(core);
        Assert.True(core!.HullTypes.Count > 100);
        Assert.True(core.Civilizations.Count >= 1);

        // The extension is optional. Instance sharing holds only while a consumer leases
        // the core (P869b, native consumer count missionigc.cpp:3033-3053); the resolver
        // releases its lease on return, so compare content, not identity.
        var withExtension = world.MissionCoreResolver("PCore001.igc");
        Assert.NotNull(withExtension);
        Assert.Equal(core.HullTypes.Keys.Order(), withExtension!.HullTypes.Keys.Order());
        Assert.Equal(core.Civilizations.Keys.Order(), withExtension.Civilizations.Keys.Order());

        // Missing file: observable false/null branch.
        Assert.Null(world.MissionCoreResolver("P870_NoSuchCore"));
        // A remote-supplied name cannot escape the asset root.
        Assert.Null(world.MissionCoreResolver("../PCore001.igc"));
    }

    /// <summary>Native rolling DWORD XOR encoder (<c>DoEncrypt</c>,
    /// <c>FedSrv/FedSrv.CPP:41-56</c>): <c>cipher_i = plain_i ^ plain_{i-1}</c>.
    /// <c>DoDecrypt</c> (line 30-39) folds in the value it just produced, which is
    /// the exact inverse.</summary>
    private static void RollingXor(Span<byte> body)
    {
        uint previous = 0;
        for (int offset = 0; offset < body.Length; offset += 4)
        {
            int width = Math.Min(4, body.Length - offset);
            uint value = 0;
            for (int i = 0; i < width; i++)
                value |= (uint)body[offset + i] << (8 * i);
            uint result = value ^ previous;
            for (int i = 0; i < width; i++)
                body[offset + i] = (byte)(result >> (8 * i));
            previous = value; // encrypt keeps the plaintext word
        }
    }
}

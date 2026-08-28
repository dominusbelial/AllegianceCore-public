namespace Allegiance.Core.Tests;

/// <summary>
/// Payload decoders are asserted against the <b>whole corpus</b>, not one file —
/// a decoder that parses cleanly on a single sample could be silently wrong on
/// others. Corrupt values surface here as range violations.
/// </summary>
public class PayloadDecodeTests
{
    /// <summary>Yield each record's raw payload (correctly body-indexed) for a given type across the whole corpus.</summary>
    private static IEnumerable<(string File, byte[] Payload)> RawOf(IgcObjectType type)
    {
        foreach (var p in Corpus.Files)
        {
            var raw = File.ReadAllBytes(p);
            var f = IgcReader.ParseFile(p);
            var bodyStart = f.Wrapper == IgcWrapper.VersionAndDataSize ? 8 : 4;
            foreach (var r in f.Records)
            {
                if (r.Type != type) continue;
                yield return (Path.GetFileName(p), raw[(bodyStart + r.PayloadOffset)..(bodyStart + r.PayloadOffset + r.Size)]);
            }
        }
    }

    /// <summary>Enumerate decoded <see cref="DataConstantsIGC"/> — one per static core.</summary>
    public static IEnumerable<(string File, DataConstantsIGC V)> ConstantRecords()
        => RawOf(IgcObjectType.Constants).Select(t => (t.File, DataConstantsIGC.Decode(t.Payload)));

    [Fact]
    public void Constants_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in ConstantRecords())
        {
            n++;
            Assert.Equal(DataConstantsIGC.FcidMax, v.FloatConstants.Length);
            Assert.Equal(DataConstantsIGC.DmgidMax, v.DamageConstants.Length);
            Assert.All(v.DamageConstants, row =>
            {
                Assert.Equal(DataConstantsIGC.DefidMax, row.Length);
                Assert.All(row, c => Assert.True(float.IsFinite(c), $"{file}: damage element NaN/Inf"));
            });
            Assert.All(v.FloatConstants, c => Assert.True(float.IsFinite(c), $"{file}: floatConstants element NaN/Inf"));
        }
        Assert.True(n >= 99, $"expected exactly ~99 constants records, saw {n}");
        Assert.Equal(Corpus.ExpectedStaticCores, n); // one per static core
    }

    /// <summary>Enumerate decoded <see cref="DataMissileTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataMissileTypeIGC V)> Missiles()
        => RawOf(IgcObjectType.MissileType).Select(t => (t.File, DataMissileTypeIGC.Decode(t.Payload)));

    [Fact]
    public void MissileType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Missiles())
        {
            n++;
            Assert.InRange(v.ExpendableTypeId, 0, short.MaxValue);
            Assert.InRange(v.DamageType, 0, 19);        // < c_dmgidMax
            Assert.True(v.AmbientSound >= -1, $"{file}: ambient {v.AmbientSound}");
            Assert.InRange(v.LauncherExpendableSize, 0, short.MaxValue);
            Assert.True(v.Price >= 0, $"{file}: negative launcher price {v.Price}");
            Assert.Equal(50, v.LauncherTtbmRequired.Length);
            Assert.Equal(50, v.LauncherTtbmEffects.Length);
            foreach (var f in new[]
                     {
                         v.Radius, v.Rotation, v.LoadTime, v.Lifespan, v.Signature,
                         v.LauncherSignature, v.LauncherMass, v.HitPoints,
                         v.Acceleration, v.TurnRate, v.InitialSpeed, v.LockTime,
                         v.ReadyTime, v.MaxLock, v.ChaffResistance, v.Dispersion,
                         v.LockAngle, v.Power, v.BlastPower, v.BlastRadius, v.Width,
                     })
                Assert.True(float.IsFinite(f), $"{file}: missile float NaN/Inf");
            foreach (var f in v.Color) Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.ModelName, v.TextureName, v.IconName, v.LauncherName, v.LauncherModelName, v.LauncherIconName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 5350, $"expected ~5370 missile records, saw {n}");
    }

    /// <summary>Enumerate decoded <see cref="DataStationTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataStationTypeIGC V)> Stations()
        => RawOf(IgcObjectType.StationType).Select(t => (t.File, DataStationTypeIGC.Decode(t.Payload)));

    [Fact]
    public void StationType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Stations())
        {
            n++;
            Assert.True(v.Price >= 0, $"{file}: negative station price {v.Price}");
            Assert.True(v.StationTypeId >= 0, $"{file}: stationTypeId {v.StationTypeId}");
            Assert.InRange(v.SuccessorStationTypeId, -1, short.MaxValue); // NA = -1
            Assert.InRange(v.ConstructionDroneTypeId, -1, short.MaxValue); // may be -1 (none)
            Assert.InRange(v.DefenseTypeArmor, 0, 19);
            Assert.InRange(v.DefenseTypeShield, 0, 19);
            Assert.Equal(50, v.TtbmRequired.Length);
            Assert.Equal(50, v.TtbmEffects.Length);
            Assert.Equal(50, v.TtbmLocal.Length);
            foreach (var f in new[]
                     {
                         v.Signature, v.MaxArmorHitPoints, v.MaxShieldHitPoints,
                         v.ArmorRegeneration, v.ShieldRegeneration, v.ScannerRange,
                         v.Radius,
                     })
                Assert.True(float.IsFinite(f), $"{file}: station float NaN/Inf");
            foreach (var s in new[] { v.Name, v.IconName, v.ModelName, v.TextureName, v.BuilderName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 12500, $"expected ~12659 station records, saw {n}");
    }

    /// <summary>Enumerate decoded <see cref="DataDroneTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataDroneTypeIGC V)> Drones()
        => RawOf(IgcObjectType.DroneType).Select(t => (t.File, DataDroneTypeIGC.Decode(t.Payload)));

    [Fact]
    public void DroneType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Drones())
        {
            n++;
            Assert.True(v.Price >= 0, $"{file}: negative drone price {v.Price}");
            Assert.True(v.HullTypeId >= 0, $"{file}: hullTypeId {v.HullTypeId}");
            Assert.InRange(v.DroneTypeId, 0, short.MaxValue);
            Assert.InRange(v.EtidLaid, -1, short.MaxValue); // NA = -1
            Assert.InRange(v.PilotType, 0, 11);             // c_ptCheatPlayer
            Assert.Equal(50, v.TtbmRequired.Length);
            Assert.Equal(50, v.TtbmEffects.Length);
            // Skills are unitless fractions/tolerances; a value beyond 0..3 is suspect.
            foreach (var f in new[] { v.ShootSkill, v.MoveSkill, v.Bravery })
                Assert.InRange(f, 0f, 3f);
            foreach (var s in new[] { v.Name, v.IconName, v.ModelName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 6400, $"expected ~6503 drone records, saw {n}");
    }

    /// <summary>Enumerate decoded <see cref="DataProjectileTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataProjectileTypeIGC V)> Projectiles()
        => RawOf(IgcObjectType.ProjectileType).Select(t => (t.File, DataProjectileTypeIGC.Decode(t.Payload)));

    [Fact]
    public void ProjectileType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Projectiles())
        {
            n++;
            Assert.InRange(v.ProjectileTypeId, 0, short.MaxValue);
            Assert.InRange(v.DamageType, 0, 19);        // DamageTypeID < c_dmgidMax
            Assert.True(float.IsFinite(v.Radius), $"{file}: radius NaN/Inf");
            Assert.True(float.IsFinite(v.Rotation), $"{file}: rotation NaN/Inf");
            Assert.True(float.IsFinite(v.Power), $"{file}: power NaN/Inf");
            Assert.True(float.IsFinite(v.BlastPower), $"{file}: blastPower NaN/Inf");
            Assert.True(float.IsFinite(v.BlastRadius), $"{file}: blastRadius NaN/Inf");
            Assert.True(float.IsFinite(v.Speed), $"{file}: speed NaN/Inf");
            Assert.True(float.IsFinite(v.Lifespan), $"{file}: lifespan NaN/Inf");
            Assert.True(float.IsFinite(v.Width), $"{file}: width NaN/Inf");
            foreach (var f in v.Color) Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.ModelName, v.TextureName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 9300, $"expected ~9498 projectile records, saw {n}");
    }

    /// <summary>Enumerate decoded civilizations across every record in every file.</summary>
    public static IEnumerable<(string File, DataCivilizationIGC V)> Civilizations()
        => RawOf(IgcObjectType.Civilization).Select(t => (t.File, DataCivilizationIGC.Decode(t.Payload)));

    [Fact]
    public void Civilization_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Civilizations())
        {
            n++;
            Assert.InRange(v.CivilizationId, 0, short.MaxValue);
            Assert.InRange(v.Lifepod, 0, short.MaxValue);
            Assert.InRange(v.InitialStationTypeId, 0, short.MaxValue);
            Assert.True(float.IsFinite(v.IncomeMoney), $"{file}: income NaN/Inf");
            Assert.True(float.IsFinite(v.BonusMoney), $"{file}: bonus NaN/Inf");
            Assert.True(v.GasBaseAttributes.Length == DataCivilizationIGC.GaCount);
            Assert.All(v.GasBaseAttributes, g => Assert.True(float.IsFinite(g), $"{file}: gas element NaN/Inf"));
            Assert.NotEmpty(v.Name);
            foreach (var s in new[] { v.Name, v.IconName, v.HudName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
            Assert.Equal(50, v.TtbmBaseTechs.Length);
            Assert.Equal(50, v.TtbmNoDevTechs.Length);
        }
        Assert.True(n >= 950, $"expected ~965 civilization records, saw {n}");
    }

    /// <summary>Enumerate decoded developments across every record in every file.</summary>
    public static IEnumerable<(string File, DataDevelopmentIGC V)> Developments()
        => RawOf(IgcObjectType.Development).Select(t => (t.File, DataDevelopmentIGC.Decode(t.Payload)));

    [Fact]
    public void Development_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Developments())
        {
            n++;
            Assert.True(v.Price >= 0, $"{file}: negative price {v.Price}");
            Assert.InRange(v.DevelopmentId, 0, short.MaxValue);
            Assert.InRange(v.CompletionSound, -1, short.MaxValue); // NA = -1
            Assert.True(float.IsFinite(v.TimeToBuild));
            Assert.NotEmpty(v.Name);
            foreach (var s in new[] { v.Name, v.IconName, v.ModelName, v.Description })
                Assert.DoesNotContain(s, c => char.IsControl(c));
            Assert.True(v.Gas.Length == DataDevelopmentIGC.GaCount);
            Assert.All(v.Gas, g => Assert.True(float.IsFinite(g), $"{file}: gas element NaN/Inf"));
            Assert.Equal(50, v.TtbmRequired.Length);
            Assert.Equal(50, v.TtbmEffects.Length);
        }
        Assert.True(n >= 28000, $"expected ~28843 development records, saw {n}");
    }

    /// <summary>Enumerate decoded hulls across every record in every file.</summary>
    public static IEnumerable<(string File, DataHullTypeIGC V)> Hulls()
        => RawOf(IgcObjectType.HullType).Select(t => (t.File, DataHullTypeIGC.Decode(t.Payload)));

    [Fact]
    public void Hull_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Hulls())
        {
            n++;
            Assert.True(v.Price >= 0, $"{file}: negative hull price {v.Price}");
            Assert.InRange(v.HullId, 0, short.MaxValue);
            Assert.InRange(v.MaxAmmo, 0, short.MaxValue);
            Assert.InRange(v.DefenseType, 0, 19);
            Assert.InRange(v.SuccessorHullId, -1, short.MaxValue); // NA = -1
            Assert.True(v.MaxFixedWeapons <= v.MaxWeapons, $"{file}: maxFixed>maxWeapons");
            Assert.Equal(v.Hardpoints.Length, v.MaxWeapons);
            Assert.Equal(540, v.HardpointOffset); // always 540 in shipped files
            Assert.Equal(14, v.PreferredPartsTypes.Length);
            Assert.Equal(8, v.PmEquipment.Length);
            Assert.Equal(50, v.TtbmRequired.Length);
            Assert.Equal(50, v.TtbmEffects.Length);
            foreach (var f in new[]
                     {
                         v.Mass, v.Signature, v.Speed, v.Thrust, v.SideMultiplier, v.BackMultiplier,
                         v.ScannerRange, v.MaxFuel, v.Ecm, v.Length, v.MaxEnergy, v.RechargeRate,
                         v.RipcordSpeed, v.RipcordCost, v.HitPoints,
                     })
                Assert.True(float.IsFinite(f), $"{file}: hull float NaN/Inf");
            foreach (var f in v.MaxTurnRates.Concat(v.TurnTorques))
                Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.Name, v.IconName, v.ModelName, v.TextureName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
            foreach (var hp in v.Hardpoints)
            {
                Assert.DoesNotContain(hp.FrameName, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(hp.FrameName) == hp.FrameName.Length);
            }
        }
        Assert.True(n >= 38000, $"expected ~38927 hull records, saw {n}");
    }
}

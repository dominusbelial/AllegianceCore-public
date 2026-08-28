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

    /// <summary>Enumerate decoded <see cref="DataProbeTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataProbeTypeIGC V)> Probes()
        => RawOf(IgcObjectType.ProbeType).Select(t => (t.File, DataProbeTypeIGC.Decode(t.Payload)));

    [Fact]
    public void ProbeType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Probes())
        {
            n++;
            Assert.InRange(v.ExpendableTypeId, 0, short.MaxValue);
            Assert.InRange(v.ProjectileTypeId, -1, short.MaxValue); // NA = -1 (gunless probe)
            Assert.InRange(v.Ammo, -1, short.MaxValue);            // NA = -1
            Assert.True(v.Price >= 0, $"{file}: negative launcher price {v.Price}");
            Assert.Equal(50, v.LauncherTtbmRequired.Length);
            Assert.Equal(50, v.LauncherTtbmEffects.Length);
            foreach (var f in new[]
                     {
                         v.Radius, v.Rotation, v.LoadTime, v.Lifespan, v.Signature,
                         v.LauncherSignature, v.LauncherMass, v.HitPoints,
                         v.ScannerRange, v.DtimeBurst, v.Dispersion, v.Accuracy,
                         v.DtRipcord,
                     })
                Assert.True(float.IsFinite(f), $"{file}: probe float NaN/Inf");
            foreach (var f in v.Color) Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.ModelName, v.TextureName, v.IconName, v.LauncherName, v.LauncherModelName, v.LauncherIconName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 4700, $"expected ~4760 probe records, saw {n}");
    }

    /// <summary>Enumerate decoded <see cref="DataChaffTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataChaffTypeIGC V)> Chaffs()
        => RawOf(IgcObjectType.ChaffType).Select(t => (t.File, DataChaffTypeIGC.Decode(t.Payload)));

    [Fact]
    public void ChaffType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Chaffs())
        {
            n++;
            Assert.InRange(v.ExpendableTypeId, 0, short.MaxValue);
            Assert.True(v.Price >= 0, $"{file}: negative launcher price {v.Price}");
            Assert.Equal(50, v.LauncherTtbmRequired.Length);
            Assert.Equal(50, v.LauncherTtbmEffects.Length);
            Assert.True(float.IsFinite(v.ChaffStrength), $"{file}: chaffStrength NaN/Inf");
            foreach (var f in new[]
                     {
                         v.Radius, v.Rotation, v.LoadTime, v.Lifespan, v.Signature,
                         v.LauncherSignature, v.LauncherMass, v.HitPoints,
                     })
                Assert.True(float.IsFinite(f), $"{file}: chaff float NaN/Inf");
            foreach (var f in v.Color) Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.ModelName, v.TextureName, v.IconName, v.LauncherName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 550, $"expected ~619 chaff records, saw {n}");
    }

    /// <summary>Enumerate decoded <see cref="DataMineTypeIGC"/> across every record in every file.</summary>
    public static IEnumerable<(string File, DataMineTypeIGC V)> Mines()
        => RawOf(IgcObjectType.MineType).Select(t => (t.File, DataMineTypeIGC.Decode(t.Payload)));

    [Fact]
    public void MineType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Mines())
        {
            n++;
            Assert.InRange(v.ExpendableTypeId, 0, short.MaxValue);
            Assert.InRange(v.DamageType, 0, 19);        // < c_dmgidMax
            Assert.True(v.Price >= 0, $"{file}: negative launcher price {v.Price}");
            Assert.Equal(50, v.LauncherTtbmRequired.Length);
            Assert.Equal(50, v.LauncherTtbmEffects.Length);
            foreach (var f in new[]
                     {
                         v.Radius, v.Rotation, v.LoadTime, v.Lifespan, v.Signature,
                         v.LauncherSignature, v.LauncherMass, v.HitPoints,
                         v.MineRadius, v.Power, v.Endurance,
                     })
                Assert.True(float.IsFinite(f), $"{file}: mine float NaN/Inf");
            foreach (var f in v.Color) Assert.True(float.IsFinite(f));
            foreach (var s in new[] { v.ModelName, v.TextureName, v.IconName, v.LauncherName })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 680, $"expected ~782 mine records, saw {n}");
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

    /// <summary>Enumerate decoded part-type payloads (excluding 24-byte launchers) across every record in every file.</summary>
    public static IEnumerable<(string File, DataPartTypeIGC V)> Parts()
        => RawOf(IgcObjectType.PartType).Where(t => t.Payload.Length != DataLauncherTypeIGC.Size)
            .Select(t => (t.File, DataPartTypeIGC.Decode(t.Payload)));

    /// <summary>Enumerate decoded launcher payloads (24-byte OT_partType) across every record in every file.</summary>
    public static IEnumerable<(string File, DataLauncherTypeIGC V)> Launchers()
        => RawOf(IgcObjectType.PartType).Where(t => t.Payload.Length == DataLauncherTypeIGC.Size)
            .Select(t => (t.File, DataLauncherTypeIGC.Decode(t.Payload)));

    [Fact]
    public void PartType_decodes_across_corpus_by_subtype()
    {
        // No OT_partType record is a bare DataPartTypeIGC (396 bytes): every one
        // carries a subtype tail, so size dispatch must always pick a subtype.
        Assert.DoesNotContain(DataPartTypeIGC.Size,
            RawOf(IgcObjectType.PartType).Select(t => t.Payload.Length));

        var counts = new Dictionary<EquipmentType, int>();
        foreach (var (file, v) in Parts())
        {
            counts[v.EquipmentType] = counts.GetValueOrDefault(v.EquipmentType) + 1;
            Assert.True(v.Price >= 0, $"{file}: negative part price {v.Price}");
            Assert.InRange(v.PartId, 0, short.MaxValue);
            Assert.InRange(v.SuccessorPartId, -1, short.MaxValue); // NA = -1
            Assert.Equal(50, v.TtbmRequired.Length);
            Assert.Equal(50, v.TtbmEffects.Length);
            Assert.True(float.IsFinite(v.Mass) && float.IsFinite(v.Signature), $"{file}: base float NaN/Inf");
            foreach (var s in new[] { v.ModelName, v.IconName, v.Name, v.InventoryLineMdl })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }

            switch (v.Type)
            {
                case DataPartTypeIGC.Weapon w:
                    foreach (var f in new[] { w.DtimeReady, w.DtimeBurst, w.EnergyPerShot, w.Dispersion })
                        Assert.True(float.IsFinite(f), $"{file}: weapon float NaN/Inf");
                    Assert.InRange(w.ProjectileTypeId, -1, short.MaxValue); // NA=-1 (rare gunless weapon)
                    Assert.InRange(w.ActivateSound, -1, short.MaxValue);
                    Assert.InRange(w.SingleShotSound, -1, short.MaxValue);
                    Assert.InRange(w.BurstSound, -1, short.MaxValue);
                    break;
                case DataPartTypeIGC.Shield s:
                    foreach (var f in new[] { s.RateRegen, s.MaxStrength }) Assert.True(float.IsFinite(f), $"{file}: shield float NaN/Inf");
                    Assert.InRange(s.DefenseType, 0, 40); // < c_dmgidMax-ish
                    break;
                case DataPartTypeIGC.Cloak c:
                    foreach (var f in new[] { c.EnergyConsumption, c.MaxCloaking, c.OnRate, c.OffRate }) Assert.True(float.IsFinite(f));
                    break;
                case DataPartTypeIGC.Afterburner a:
                    foreach (var f in new[] { a.FuelConsumption, a.MaxThrust, a.OnRate, a.OffRate }) Assert.True(float.IsFinite(f));
                    break;
                case DataPartTypeIGC.Pack pk:
                    Assert.InRange(pk.Amount, 0, short.MaxValue);
                    Assert.True(pk.PackType == 0 || pk.PackType == 1, $"{file}: bad packType {pk.PackType}"); // c_packAmmo/Fuel
                    break;
                default:
                    Assert.Fail($"{file}: unexpected subtype {v.Type.GetType().Name}");
                    break;
            }
        }
        // Five observable subtype families; the two 416-byte ones are distinguished by equipmentType.
        Assert.Equal(5, counts.Count);
        Assert.True(counts.TryGetValue(EquipmentType.Weapon, out var weaponCount) && weaponCount >= 7700, "weapon count");
        Assert.True(counts.GetValueOrDefault(EquipmentType.Shield) >= 1000, "shield count");
        Assert.True(counts.GetValueOrDefault(EquipmentType.Cloak) >= 690, "cloak count");
        Assert.True(counts.GetValueOrDefault(EquipmentType.Afterburner) >= 1100, "afterburner count");
        Assert.True(counts.GetValueOrDefault(EquipmentType.Pack) >= 200, "pack count");
    }

    [Fact]
    public void LauncherType_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Launchers())
        {
            n++;
            Assert.True(v.Amount >= 0, $"{file}: negative launcher amount {v.Amount}");
            Assert.InRange(v.LaunchCount, 0, short.MaxValue);
            Assert.InRange(v.ExpendabletypeId, 0, short.MaxValue); // names an expendable in the same core
            Assert.DoesNotContain(v.InventoryLineMdl, c => char.IsControl(c));
            Assert.True(System.Text.Encoding.ASCII.GetByteCount(v.InventoryLineMdl) == v.InventoryLineMdl.Length, $"{file}: non-ASCII {v.InventoryLineMdl}");
        }
        Assert.True(n >= 11000, $"expected ~11511 launcher records, saw {n}");
    }

    [Fact]
    public void PartType_offsets_cross_reference_same_core()
    {
        // Per-file id spaces: projectileTypeIds from OT_projectileType,
        // expendable ids from the expendable types, and part ids from
        // OT_partType launcher + part records — all in the SAME file. A weapon
        // referencing a projectile type / a part successor / a launcher's
        // expendable that is absent from the core would mean a misread offset.
        var proj = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var exp = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        var partIds = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var p in Corpus.Files)
        {
            var f = IgcReader.ParseFile(p);
            var name = Path.GetFileName(p);
            foreach (var r in f.Records)
            {
                var pay = RawPayload(p, f, r);
                switch (r.Type)
                {
                    case IgcObjectType.ProjectileType:
                        Get(proj, name).Add(DataProjectileTypeIGC.Decode(pay).ProjectileTypeId);
                        break;
                    case IgcObjectType.MissileType: Get(exp, name).Add(DataMissileTypeIGC.Decode(pay).ExpendableTypeId); break;
                    case IgcObjectType.MineType: Get(exp, name).Add(DataMineTypeIGC.Decode(pay).ExpendableTypeId); break;
                    case IgcObjectType.ProbeType: Get(exp, name).Add(DataProbeTypeIGC.Decode(pay).ExpendableTypeId); break;
                    case IgcObjectType.ChaffType: Get(exp, name).Add(DataChaffTypeIGC.Decode(pay).ExpendableTypeId); break;
                    case IgcObjectType.PartType:
                        var ids = Get(partIds, name);
                        if (pay.Length == DataLauncherTypeIGC.Size) ids.Add(DataLauncherTypeIGC.Decode(pay).PartId);
                        else { var pt = DataPartTypeIGC.Decode(pay); ids.Add(pt.PartId); ids.Add(pt.SuccessorPartId); }
                        break;
                }
            }
        }

        var n = 0;
        foreach (var (file, v) in Parts())
        {
            n++;
            if (v.Type is DataPartTypeIGC.Weapon w && w.ProjectileTypeId >= 0)
                Assert.True(proj[file].Contains(w.ProjectileTypeId),
                    $"{file}: weapon {v.PartId} projectileTypeID {w.ProjectileTypeId} not a projectile in this file");
            if (v.SuccessorPartId >= 0)
                Assert.True(partIds[file].Contains(v.SuccessorPartId),
                    $"{file}: part {v.PartId} successorPartID {v.SuccessorPartId} not a part in this file");
        }
        foreach (var (file, l) in Launchers())
            Assert.True(exp[file].Contains(l.ExpendabletypeId),
                $"{file}: launcher {l.PartId} expendabletypeID {l.ExpendabletypeId} not an expendable in this file");
    }

    private static HashSet<int> Get(Dictionary<string, HashSet<int>> d, string key)
    {
        if (!d.TryGetValue(key, out var s)) { s = new HashSet<int>(); d[key] = s; }
        return s;
    }

    // ----------------------------------------------------------------------
    // Map-file objects (OT_station/OT_cluster/OT_warp/OT_asteroid). These are
    // the mission geography. They point at static-core objects and at each
    // other, so the strongest validation is cross-reference: a station's
    // stationTypeID must resolve to an OT_stationType in the static core, and
    // every clusterID / warp destination must name a record present in the
    // SAME file.
    // ----------------------------------------------------------------------

    /// <summary>Distinct OT_stationType (31) IDs present anywhere in the corpus.</summary>
    private static Lazy<HashSet<int>> KnownStationTypes = new(() =>
    {
        var s = new HashSet<int>();
        foreach (var (_, v) in RawOf(IgcObjectType.StationType))
            s.Add(DataStationTypeIGC.Decode(v).StationTypeId);
        return s;
    });

    public static IEnumerable<(string File, DataStationIGC V)> StationsM()
        => RawOf(IgcObjectType.Station).Select(t => (t.File, DataStationIGC.Decode(t.Payload)));

    [Fact]
    public void Station_decodes_across_corpus_with_cross_refs()
    {
        var n = 0;
        foreach (var (file, v) in StationsM())
        {
            n++;
            Assert.True(KnownStationTypes.Value.Contains(v.StationTypeId),
                $"{file}: stationTypeID {v.StationTypeId} not a known OT_stationType");
            Assert.InRange(v.ClusterId, 0, short.MaxValue);
            Assert.InRange(v.SideId, -1, short.MaxValue);
            Assert.InRange(v.StationId, 0, short.MaxValue);
            Assert.InRange(v.BpHull, 0, 255);
            Assert.InRange(v.BpShield, 0, 255);
            foreach (var f in v.Position.Concat(v.Up).Concat(v.Forward).Concat(v.Rotation))
                Assert.True(float.IsFinite(f), $"{file}: station geom NaN/Inf");
            Assert.DoesNotContain(v.Name, c => char.IsControl(c));
            Assert.True(System.Text.Encoding.ASCII.GetByteCount(v.Name) == v.Name.Length, $"{file}: non-ASCII {v.Name}");
        }
        Assert.True(n >= 250, $"expected ~289 station records, saw {n}");
    }

    public static IEnumerable<(string File, DataClusterIGC V)> Clusters()
        => RawOf(IgcObjectType.Cluster).Select(t => (t.File, DataClusterIGC.Decode(t.Payload)));

    [Fact]
    public void Cluster_decodes_across_corpus()
    {
        var n = 0;
        foreach (var (file, v) in Clusters())
        {
            n++;
            Assert.True(float.IsFinite(v.ScreenX) && float.IsFinite(v.ScreenY), $"{file}: screen pos NaN/Inf");
            Assert.InRange(v.ClusterId, 0, short.MaxValue);
            Assert.InRange(v.Debris, 0, short.MaxValue);
            Assert.InRange(v.Stars, 0, short.MaxValue);
            // activeF is forced false on export (clusterIGC.cpp:494) — a real value here means a misread offset.
            Assert.False(v.Active, $"{file}: activeF must be false in a shipped map");
            Assert.InRange(v.PlanetSinLatitude, 0, 255);
            Assert.InRange(v.PlanetLongitude, 0, 255);
            Assert.InRange(v.PlanetRadius, 0, 255);
            Assert.NotEmpty(v.Name);
            Assert.DoesNotContain(v.Name, c => char.IsControl(c));
            Assert.True(System.Text.Encoding.ASCII.GetByteCount(v.Name) == v.Name.Length, $"{file}: non-ASCII {v.Name}");
            foreach (var s in new[] { v.PosterName, v.PlanetName })
                Assert.DoesNotContain(s, c => char.IsControl(c));
        }
        Assert.True(n >= 1450, $"expected ~1506 cluster records, saw {n}");
    }

    public static IEnumerable<(string File, DataWarpIGC V)> Warps()
        => RawOf(IgcObjectType.Warp).Select(t => (t.File, DataWarpIGC.Decode(t.Payload)));

    [Fact]
    public void Warp_decodes_across_corpus_with_cross_refs()
    {
        // Per-file sets of valid destinations: cluster IDs and warp IDs in the same file.
        var destinationsByFile = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var p in Corpus.Files)
        {
            var f = IgcReader.ParseFile(p);
            var name = Path.GetFileName(p);
            if (!f.Records.Any(r => r.Type == IgcObjectType.Warp)) continue;
            var set = new HashSet<int>();
            foreach (var r in f.Records)
            {
                switch (r.Type)
                {
                    case IgcObjectType.Cluster:
                        set.Add(DataClusterIGC.Decode(RawPayload(p, f, r)).ClusterId);
                        break;
                    case IgcObjectType.Warp:
                        set.Add(DataWarpIGC.Decode(RawPayload(p, f, r)).WarpId);
                        break;
                }
            }
            destinationsByFile[name] = set;
        }

        var n = 0;
        foreach (var (file, v) in Warps())
        {
            n++;
            Assert.True(destinationsByFile[file].Contains(v.DestinationId),
                $"{file}: warp {v.WarpId} destination {v.DestinationId} not a warp/sector in this file");
            Assert.True(destinationsByFile[file].Contains(v.ClusterId),
                $"{file}: warp {v.WarpId} clusterID {v.ClusterId} not a sector in this file");
            Assert.True(v.Radius >= 0, $"{file}: negative warp radius {v.Radius}");
            Assert.InRange(v.WarpId, 0, short.MaxValue);
            foreach (var f in v.Position.Concat(v.Forward).Concat(v.Rotation).Append(v.Signature))
                Assert.True(float.IsFinite(f), $"{file}: warp float NaN/Inf");
            foreach (var s in new[] { v.TextureName, v.IconName, v.Name })
            {
                Assert.DoesNotContain(s, c => char.IsControl(c));
                Assert.True(System.Text.Encoding.ASCII.GetByteCount(s) == s.Length, $"{file}: non-ASCII {s}");
            }
        }
        Assert.True(n >= 5000, $"expected ~5212 warp records, saw {n}");
    }

    public static IEnumerable<(string File, DataAsteroidIGC V)> Asteroids()
        => RawOf(IgcObjectType.Asteroid).Select(t => (t.File, DataAsteroidIGC.Decode(t.Payload)));

    [Fact]
    public void Asteroid_decodes_across_corpus_with_cross_refs()
    {
        var clusterIdsByFile = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);
        foreach (var p in Corpus.Files)
        {
            var f = IgcReader.ParseFile(p);
            var name = Path.GetFileName(p);
            if (!f.Records.Any(r => r.Type == IgcObjectType.Asteroid)) continue;
            clusterIdsByFile[name] = new HashSet<int>(
                f.Records.Where(r => r.Type == IgcObjectType.Cluster)
                    .Select(r => (int)DataClusterIGC.Decode(RawPayload(p, f, r)).ClusterId));
        }

        var n = 0;
        foreach (var (file, v) in Asteroids())
        {
            n++;
            Assert.True(clusterIdsByFile[file].Contains(v.ClusterId),
                $"{file}: asteroid clusterID {v.ClusterId} not a sector in this file");
            Assert.InRange(v.AsteroidId, -1, short.MaxValue); // NA=-1 legal
            Assert.InRange(v.Radius, 0, short.MaxValue);
            Assert.True(v.Ore >= 0, $"{file}: negative ore {v.Ore}");
            Assert.True(v.OreMax >= 0, $"{file}: negative oreMax {v.OreMax}");
            Assert.True(float.IsFinite(v.HitPoints), $"{file}: hitPoints NaN/Inf");
            foreach (var f in v.Position.Concat(v.Up).Concat(v.Forward).Concat(v.Rotation).Append(v.Signature))
                Assert.True(float.IsFinite(f), $"{file}: asteroid geom NaN/Inf");
            foreach (var s in new[] { v.ModelName, v.TextureName, v.IconName, v.Name })
                Assert.DoesNotContain(s, c => char.IsControl(c));
            Assert.InRange(v.Fraction, 0, 255);
        }
        Assert.True(n >= 970, $"expected ~973 asteroid records, saw {n}");
    }

    private static byte[] RawPayload(string path, IgcFile f, IgcRecord r)
    {
        var raw = File.ReadAllBytes(path);
        var bodyStart = f.Wrapper == IgcWrapper.VersionAndDataSize ? 8 : 4;
        return raw[(bodyStart + r.PayloadOffset)..(bodyStart + r.PayloadOffset + r.Size)];
    }
}

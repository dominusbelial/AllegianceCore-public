using System.Buffers.Binary;
using System.Text;
using Allegiance.Core;
using Allegiance.Core.Model;
using Allegiance.TestInfra;
using Allegiance.Tools.Core2Txt;

namespace Allegiance.Core.Tests;

/// <summary>
/// P870/P870b — certification of the native static-core text-authoring rules
/// behind Core2Text/Text2Core: the shared helpers in
/// <c>src/Igc/missionigc.cpp:115-545</c> and the per-type codecs
/// (<c>Obj2Txt@4302</c>, <c>ExportStaticIGCObjs@5396</c>,
/// <c>ImportStaticIGCObjs@547</c>), plus the real-core byte-identical round trip.
/// </summary>
public sealed class P870Core2TxtTests
{
    // ---- SIM-0686 TechBitFromToken (missionigc.cpp:115-125) ----
    [Fact]
    public void TechBitFromToken_resolves_loaded_names_case_insensitively_and_NA_unknown()
    {
        var t = new CoreTable();
        Assert.Equal(-1, t.TechBitFromToken("anything")); // empty table → NA sentinel
        t.SetTechFlagName(5, "Expansion");
        t.SetTechFlagName(0, "Military");
        Assert.Equal(5, t.TechBitFromToken("expansion"));
        Assert.Equal(0, t.TechBitFromToken("MILITARY"));
        Assert.Equal(-1, t.TechBitFromToken("nonexistent"));
    }

    // ---- SIM-0687 TechsListToBits (missionigc.cpp:128-149) ----
    [Fact]
    public void TechsListToBits_zero_hex_and_named_tokens()
    {
        var t = new CoreTable();
        var z = new bool[400];
        t.TechsListToBits("0", z);
        Assert.All(z, b => Assert.False(b)); // literal 0 → empty

        // Digit-leading goes through TLargeBitMask::FromString (mask.h:205):
        // bytes are MSB-first, so "01" sets bit 7, not bit 0.
        var h = new bool[400];
        t.TechsListToBits("0100", h);
        Assert.False(h[0]);
        Assert.True(h[7]);

        t.SetTechFlagName(3, "Expansion");
        t.SetTechFlagName(9, "Combat");
        var n = new bool[400];
        t.TechsListToBits("expansion combat", n);
        Assert.True(n[3]);
        Assert.True(n[9]);
        Assert.False(n[4]);

        // Unknown token: TechBitFromToken is NA; native's SetBit(-1) is undefined
        // behaviour, the port adds nothing.
        var u = new bool[400];
        t.TechsListToBits("nope", u);
        Assert.All(u, b => Assert.False(b));
    }

    [Fact]
    public void Tech_mask_hex_is_native_msb_first_uppercase_and_rejects_odd_length()
    {
        var bit0 = new bool[400];
        bit0[0] = true;
        string raw = CoreTable.MaskToString(bit0);
        Assert.Equal(100, raw.Length);          // 50 bytes * 2
        Assert.StartsWith("80", raw);           // SetBit(0) = 0x80, ToString = "%02X"
        Assert.Equal(raw.ToUpperInvariant(), raw);

        // Round trip, uppercase, MSB-first.
        var bits = new bool[400];
        bits[0] = bits[7] = bits[8] = bits[399] = true;
        string hex = CoreTable.MaskToString(bits);
        Assert.StartsWith("8180", hex);
        Assert.EndsWith("01", hex);
        var back = new bool[400];
        Assert.True(CoreTable.FromString(hex, back));
        Assert.Equal(bits, back);

        // Odd length is rejected and leaves the mask untouched (native returns
        // false before Set()).
        var untouched = new bool[400];
        untouched[5] = true;
        Assert.False(CoreTable.FromString("010", untouched));
        Assert.True(untouched[5]);
    }

    // ---- SIM-0688 PartMaskFromToken (missionigc.cpp:152-206) ----
    [Fact]
    public void PartMaskFromToken_digit_atoi_negative_spaces_and_index()
    {
        var t = new CoreTable();
        Assert.Equal(3, t.PartMaskFromToken("3abc", 1));      // native atoi leniency
        Assert.Equal(0, t.PartMaskFromToken("   ", 1));       // empty after removing spaces
        Assert.Equal(0, t.PartMaskFromToken("unknown", 1));
        Assert.Equal(unchecked((short)-32768), t.PartMaskFromToken("-32768", 1));
        t.SetPartFlagName(2, 1, "Gatling");
        Assert.Equal(0x04, t.PartMaskFromToken("gatling", 1)); // c_pbm3
        Assert.Equal(0x04, t.PartMaskFromToken("Gat Ling", 1)); // spaces removed
    }

    // ---- SIM-0689 PartsListToMask (missionigc.cpp:361-374) ----
    [Fact]
    public void PartsListToMask_adds_token_bits_including_repeats()
    {
        var t = new CoreTable();
        Assert.Equal(5, t.PartsListToMask("5", 1));      // digit-leading → atoi
        Assert.Equal(3, t.PartsListToMask("3abc", 1));
        t.SetPartFlagName(0, 1, "One");
        t.SetPartFlagName(2, 1, "Two");
        Assert.Equal(0x05, t.PartsListToMask("One Two", 1)); // 1 + 4
        Assert.Equal(0x02, t.PartsListToMask("One One", 1)); // native adds (2 * c_pbm1)
    }

    // ---- SIM-0690 BitsToTechsList (missionigc.cpp:377-413) ----
    [Fact]
    public void BitsToTechsList_raw_mask_when_flags_not_loaded_then_names()
    {
        var t = new CoreTable();
        var bits = new bool[400];
        bits[0] = true;
        Assert.StartsWith("80", t.BitsToTechsList(bits)); // raw branch, uppercase hex

        t.SetTechFlagName(0, "unavailable");
        t.SetTechFlagName(2, "Expansion");
        t.SetTechFlagName(5, "Combat");
        var nb = new bool[400];
        nb[2] = nb[5] = true;
        // Native appends a space after every name (including the last).
        Assert.Equal("Expansion Combat ", t.BitsToTechsList(nb));
        Assert.Equal("0", t.BitsToTechsList(new bool[400]));

        t.SetTechFlagName(1, "X");
        var sb = new bool[400];
        sb[1] = true; // "X " is shorter than 3 chars → raw fallback
        Assert.StartsWith("40", t.BitsToTechsList(sb));
    }

    // ---- SIM-0691 BitsToPartsList (missionigc.cpp:417-449) ----
    [Fact]
    public void BitsToPartsList_decimal_when_unloaded_then_names()
    {
        var t = new CoreTable();
        Assert.Equal("5", t.BitsToPartsList(5, 1));

        t.SetPartFlagName(0, 1, "AAA");
        t.SetPartFlagName(2, 1, "BBB");
        Assert.Equal("AAA BBB", t.BitsToPartsList(0x05, 1).Trim());
        Assert.Equal(unchecked((short)-32768), short.Parse(t.BitsToPartsList(unchecked((short)-32768), 1)));
    }

    // ---- SIM-0692 LoadTechBitsList (missionigc.cpp:451-486) ----
    [Fact]
    public void LoadTechBitsList_reads_csv_skips_header_reuses_last_name_and_missing_is_false()
    {
        var t = new CoreTable();
        using var tmp = new TempDir();
        Assert.False(t.LoadTechBitsList(Path.Combine(tmp.Path, "BitsOfTechs.csv")));

        File.WriteAllText(Path.Combine(tmp.Path, "BitsOfTechs.csv"),
            "BITID\tSHORT NAME\tLONG NAME\r\n0\tMilitary\tMilitary\r\n7\tExpansion\tExpansion\r\n8\r\n");
        Assert.True(t.LoadTechBitsList(Path.Combine(tmp.Path, "BitsOfTechs.csv")));
        Assert.Equal("Military", t.GetTechFlagName(0));
        Assert.Equal("Expansion", t.GetTechFlagName(7));
        // native's strFlag persists across rows, so a 1-column row keeps it
        Assert.Equal("Expansion", t.GetTechFlagName(8));
    }

    // ---- SIM-0693 LoadPartsBitsList (missionigc.cpp:487-526) ----
    [Fact]
    public void LoadPartsBitsList_reads_csv_per_equipment_and_missing_is_false()
    {
        var t = new CoreTable();
        using var tmp = new TempDir();
        Assert.False(t.LoadPartsBitsList(Path.Combine(tmp.Path, "BitsOfParts.csv")));

        File.WriteAllText(Path.Combine(tmp.Path, "BitsOfParts.csv"),
            "EQUIPMENT\tSHORT NAME\r\n1\tGatling\r\n1\tBolter\r\n2\tSig Sigma\r\n99\tOut Of Range\r\n");
        Assert.True(t.LoadPartsBitsList(Path.Combine(tmp.Path, "BitsOfParts.csv")));
        Assert.Equal("Gatling", t.GetPartFlagName(0, 1));
        Assert.Equal("Bolter", t.GetPartFlagName(1, 1));
        Assert.Equal("Sig Sigma", t.GetPartFlagName(2, 2));
    }

    // ---- SIM-0785 AbilitiesListToMask (missionigc.cpp:209-357) ----
    [Fact]
    public void AbilitiesListToMask_parses_native_ability_names_per_kind()
    {
        Assert.Equal(0x80, CoreTable.AbilitiesListToMask("IsFighter", CoreTable.ATHull));
        Assert.Equal(0x4000, CoreTable.AbilitiesListToMask("IsMiner", CoreTable.ATHull));
        Assert.Equal(0x80 | 0x4000,
            CoreTable.AbilitiesListToMask("IsFighter IsMiner", CoreTable.ATHull));

        Assert.Equal(0x20, CoreTable.AbilitiesListToMask("CanLandHere", CoreTable.ATStation));
        Assert.Equal(0x200, CoreTable.AbilitiesListToMask("IsVictoryObjective", CoreTable.ATStation));
        Assert.Equal(0x01, CoreTable.AbilitiesListToMask("CanOffloadMinerals", CoreTable.ATStation));
        Assert.Equal(0x01, CoreTable.AbilitiesListToMask("CanBoard", CoreTable.ATStation));

        Assert.Equal(0x40, CoreTable.AbilitiesListToMask("CanShootShips", CoreTable.ATExpendable));
        Assert.Equal(0x01, CoreTable.AbilitiesListToMask("CanCaptureStations", CoreTable.ATExpendable));
        // The overlapping names must resolve to what the writer emitted: native's
        // own reader sets BOTH bits for "CanRescueAny" and breaks the round trip.
        Assert.Equal(0x4000, CoreTable.AbilitiesListToMask("CanRescueAny", CoreTable.ATExpendable));
        Assert.Equal(0x2000, CoreTable.AbilitiesListToMask("CanRescue", CoreTable.ATExpendable));
        Assert.Equal(0x6000, CoreTable.AbilitiesListToMask("CanRescue CanRescueAny", CoreTable.ATExpendable));

        Assert.Equal(0x08, CoreTable.AbilitiesListToMask("CanBeRegular", CoreTable.ATAsteroid));
        Assert.Equal(0x10, CoreTable.AbilitiesListToMask("CanBeUranium", CoreTable.ATAsteroid));
        Assert.Equal(0x20, CoreTable.AbilitiesListToMask("CanBeSilicon", CoreTable.ATAsteroid));
        Assert.Equal(0x40, CoreTable.AbilitiesListToMask("CanBeCarbon", CoreTable.ATAsteroid));
        Assert.Equal(0x01, CoreTable.AbilitiesListToMask("CanBeHelium", CoreTable.ATAsteroid));
        Assert.Equal(0x04, CoreTable.AbilitiesListToMask("CanBeGold", CoreTable.ATAsteroid));
        Assert.Equal(0, CoreTable.AbilitiesListToMask("", CoreTable.ATHull));

        // Writer/reader are exact inverses for every table.
        Assert.Equal("CanOffloadMinerals ", CoreTable.AbilitiesListToStrings(0x01, CoreTable.ATStation));
        Assert.Equal("IsMiner ", CoreTable.AbilitiesListToStrings(0x4000, CoreTable.ATHull));
        Assert.Equal("CanBeUranium CanBeSilicon ", CoreTable.AbilitiesListToStrings(0x30, CoreTable.ATAsteroid));
    }

    // ---- SIM-0786 readCSV (missionigc.cpp:530-545) ----
    [Fact]
    public void ReadCsv_is_tab_delimited_drops_trailing_empty_field_and_keeps_header()
    {
        using var tmp = new TempDir();
        var p = Path.Combine(tmp.Path, "x.csv");
        // Native std::getline(..., '\t') drops the trailing empty field:
        // "1\t2\t" -> 2 columns, while an interior empty field survives.
        File.WriteAllText(p, "A\tB\tC\r\n1\t2\t\r\n1\t\t3\r\n\r\n");
        var rows = CoreTable.ReadCsv(p);
        Assert.Equal(4, rows.Count);
        Assert.Equal(new[] { "A", "B", "C" }, rows[0]);
        Assert.Equal(2, rows[1].Count);
        Assert.Equal("2", rows[1][1]);
        Assert.Equal(3, rows[2].Count);
        Assert.Equal("", rows[2][1]);
        Assert.Empty(rows[3]); // an empty line yields no columns
    }

    // ---- SIM-0791 ConstantsDamageComment (missionigc.cpp:4246-4299) ----
    [Fact]
    public void ConstantsDamageComment_returns_native_names_and_fallbacks()
    {
        Assert.Equal("Plasma vs. Medium Ship", CoreTable.ConstantsDamageComment(2, 1));
        Assert.Equal("Galv", CoreTable.ConstantsDamageComment(-1, 7));
        Assert.Equal("Heavy Base", CoreTable.ConstantsDamageComment(7, -1));
        Assert.Equal("Invincible", CoreTable.ConstantsDamageComment(-1, -1));
        Assert.Equal("Uncommented damage constant vs. Rock", CoreTable.ConstantsDamageComment(0, 999));
    }

    // ---- SIM-0793 ConstantsGlobalComment (missionigc.cpp:5316-5363) ----
    [Fact]
    public void ConstantsGlobalComment_returns_native_descriptions_and_fallback()
    {
        Assert.Equal("Payday (economy) timer (# sec/payday).", CoreTable.ConstantsGlobalComment(6));
        Assert.Equal("He3 asteroid (economy) capacity. ", CoreTable.ConstantsGlobalComment(7));
        Assert.Equal("New uncommented constant", CoreTable.ConstantsGlobalComment(9999));
    }

    // ---- %g float formatting (zstring.cpp:257-262) ----
    [Fact]
    public void FormatFloat_matches_native_g_and_round_trips_every_value()
    {
        Assert.Equal("1.25", CoreTable.FormatFloat(1.25f));
        Assert.Equal("0.5", CoreTable.FormatFloat(0.5f));
        Assert.Equal("1", CoreTable.FormatFloat(1f));
        Assert.Equal("-0", CoreTable.FormatFloat(-0f));
        Assert.Equal("1e+06", CoreTable.FormatFloat(1e6f));
        Assert.Equal("1e-05", CoreTable.FormatFloat(1e-5f));

        // %g rounds to 6 significant digits and cannot reproduce this bit
        // pattern; the exact fallback must.
        float weird = BitConverter.Int32BitsToSingle(unchecked((int)0xCDCDCDCD));
        string s = CoreTable.FormatFloat(weird);
        Assert.Equal(0xCDCDCDCDu, BitConverter.SingleToUInt32Bits(CoreTable.ParseFloat(s)));

        foreach (uint bits in new uint[]
                 {
                     0, 0x80000000, 0x3F800000, 0x3DCCCCCD, 0xCDCDCDCD,
                     0xCCCCCCCC, 0x7F7FFFFF, 0x00800000, 0x3F490FDB, 0x49742400,
                 })
        {
            float v = BitConverter.Int32BitsToSingle(unchecked((int)bits));
            Assert.Equal(bits, BitConverter.SingleToUInt32Bits(CoreTable.ParseFloat(CoreTable.FormatFloat(v))));
        }
    }

    [Theory]
    [InlineData("3abc", 3)]
    [InlineData("-12x", -12)]
    [InlineData("  42", 42)]
    [InlineData("", 0)]
    [InlineData("abc", 0)]
    public void ParseInt_matches_native_atoi_leniency(string text, int expected)
        => Assert.Equal(expected, CoreTable.ParseInt(text));

    [Theory]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void ParseBool_accepts_the_native_bool_spelling(string text, bool expected)
        => Assert.Equal(expected, CoreTable.ParseBool(text));

    // ------------------------------------------------------------------
    // Native header/column shape of the exported text (Obj2Txt).
    // ------------------------------------------------------------------

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Exported_text_files_use_native_headers_and_column_counts()
    {
        Require.Artwork();
        string core = Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc");
        using var tmp = new TempDir();
        StaticCoreRoundtrip.Export(IgcReader.ParseFile(core), tmp.Path);

        // Verbatim native header lines (missionigc.cpp:5396-5706); a changed
        // column or renamed field fails here.
        Assert.Equal("GLOBALID OR DEFENSE CONSTANTIDS\tGLOBAL VALUE OR DAMAGE CONSTANTID\tCOMMENT",
            Header(tmp, "Constants"));
        Assert.Equal("ID\tMASS\tSIGNATURE\tSPEED\tMAX TURN RATES X Y Z\tTURN TORQUES Z Y Z\tTHRUST\t" +
                     "SIDE MULTIPLIER\tBACK MULTIPLIER\tSCANNER RANGE\tMAX FUEL\tECM\tLENGTH\tMAX ENERGY\t" +
                     "RECHARGE RATE\tRIP SPEED\tRIP COST\tMAX AMMO\tSUCCESSOR HULL ID\tMAX WEAPONS\t" +
                     "MAX FIXED WEAPONS\tHIT POINTS\tDEFENSE CONSTNAT ID\tMAGAZINE CAPACITY\t" +
                     "DISPENSER CAPACITY\tCHAFF LAUNCHER CAPACITY\tPREFERRED PART TYPES\tABILITIES\t" +
                     "TEXTURE NAME\tALLOWED CHAFF\tALLOWED MISSILES\tALLOWED DISPENSERS\tALLOWED SHIELDS\t" +
                     "ALLOWED CLOAKS\tALLOWED BOOSTERS\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\t" +
                     "DESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT", Header(tmp, "TypesOfShips"));
        Assert.Equal("ID\tREADY TIME\tBURST TIME\tENERGY PER SHOT\tDISPERSION\tAMMO PER SHOT\t" +
                     "PROJECTILE TYPE ID\tACTIVATE SFX ID\tSHOT SFX ID\tBURST SFX ID\tMASS\tSIGNATURE\t" +
                     "SUCCESSOR PART ID\tPART MASK\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\t" +
                     "ICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT", Header(tmp, "Weapons"));

        // Every file exists with the native header and every data row has the
        // same number of columns.
        foreach (var spec in CoreFiles.All)
        {
            string path = Path.Combine(tmp.Path, spec.BaseName + ".csv");
            Assert.True(File.Exists(path), $"{spec.BaseName}.csv missing");
            var lines = ReadLines(path);
            Assert.Equal(spec.Header, lines[0]);
            int cols = lines[0].Split('\t').Length;
            for (int i = 1; i < lines.Length; i++)
                Assert.True(lines[i].Split('\t').Length == cols,
                    $"{spec.BaseName}.csv line {i + 1} has {lines[i].Split('\t').Length} columns, header has {cols}");
        }
    }

    // ------------------------------------------------------------------
    // Exported values against the corpus-certified decoders (independent of
    // the codec's own field access, so a wrong offset fails).
    // ------------------------------------------------------------------

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Exported_values_match_the_certified_static_core_decoders()
    {
        Require.Artwork();
        string corePath = Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc");
        var igc = IgcReader.ParseFile(corePath);
        var core = StaticCore.Load(igc);
        using var tmp = new TempDir();
        StaticCoreRoundtrip.Export(igc, tmp.Path);

        // Hull type: constants, strings, buyable tail, part masks, hardpoints.
        var hull = core.HullTypes.Values.First(h => h.MaxWeapons >= 2);
        var hrow = RowWithId(tmp, "TypesOfShips", hull.HullId);
        Assert.Equal(CoreTable.FormatFloat(hull.Mass), hrow[1]);
        Assert.Equal(CoreTable.FormatFloat(hull.Speed), hrow[3]);
        Assert.Equal($"{CoreTable.FormatFloat(hull.MaxTurnRates[0])} " +
                     $"{CoreTable.FormatFloat(hull.MaxTurnRates[1])} " +
                     $"{CoreTable.FormatFloat(hull.MaxTurnRates[2])}", hrow[4]);
        Assert.Equal(CoreTable.FormatFloat(hull.HitPoints), hrow[21]);
        Assert.Equal(CoreTable.FormatInt(hull.MaxWeapons), hrow[19]);
        Assert.Equal(hull.Name, hrow[39]);
        Assert.Equal(hull.ModelName, hrow[37]);
        Assert.Equal(CoreTable.FormatInt(hull.PmEquipment[0]), hrow[29]); // chaff launcher
        Assert.Equal(CoreTable.FormatInt(hull.PmEquipment[2]), hrow[30]); // magazine/missiles
        Assert.Equal(CoreTable.FormatInt(hull.PmEquipment[3]), hrow[31]); // dispenser
        Assert.Equal(CoreTable.FormatInt(hull.Price), hrow[35]);
        var hpRows = RowsWithId(tmp, "Hardpoints", hull.HullId);
        Assert.Equal(hull.Hardpoints.Length, hpRows.Count);
        Assert.Equal(hull.Hardpoints[0].FrameName, hpRows[0][3]);
        Assert.Equal(CoreTable.FormatInt(hull.Hardpoints[0].PartMask), hpRows[0][5]);
        Assert.Equal(hull.Hardpoints[0].Fixed ? "true" : "false", hpRows[0][6]);

        // Projectile.
        var proj = core.ProjectileTypes.Values.First();
        var prow = RowWithId(tmp, "TypesOfProjectiles", proj.ProjectileTypeId);
        Assert.Equal(CoreTable.FormatFloat(proj.Power), prow[1]);
        Assert.Equal(CoreTable.FormatFloat(proj.Speed), prow[4]);
        Assert.StartsWith(CoreTable.FormatInt(proj.DamageType) + " (", prow[6]);
        Assert.Equal(CoreTable.FormatFloat(proj.Width), prow[9]);
        Assert.Equal(proj.ModelName, prow[14]);

        // Missile (expendable + embedded launcher definition).
        var missile = core.MissileTypes.Values.First();
        var mrow = RowWithId(tmp, "ExpendableMissiles", missile.ExpendableTypeId);
        Assert.Equal(CoreTable.FormatFloat(missile.Acceleration), mrow[1]);
        Assert.Equal(CoreTable.FormatFloat(missile.LockAngle), mrow[9]);
        Assert.Equal(CoreTable.FormatInt(missile.DamageType), mrow[14].Split(' ')[0]);
        Assert.Equal(CoreTable.FormatFloat(missile.LoadTime), mrow[18]);
        Assert.Equal(CoreTable.FormatFloat(missile.LauncherSignature), mrow[30]);
        Assert.Equal(CoreTable.FormatInt(missile.LauncherExpendableSize), mrow[33]);
        Assert.Equal(missile.LauncherName, mrow[34]);

        // Part (weapon) + its subtype tail.
        var weapon = core.PartTypes.Values.First(p => p.EquipmentType == EquipmentType.Weapon);
        var wrow = RowWithId(tmp, "Weapons", weapon.PartId);
        var wt = (DataPartTypeIGC.Weapon)weapon.Type;
        Assert.Equal(CoreTable.FormatFloat(wt.DtimeReady), wrow[1]);
        Assert.Equal(CoreTable.FormatInt(wt.ProjectileTypeId), wrow[6]);
        Assert.Equal(CoreTable.FormatFloat(weapon.Mass), wrow[10]);
        Assert.Equal(CoreTable.FormatInt(weapon.PartMask), wrow[13]);
        Assert.Equal(weapon.Name, wrow[19]);
        Assert.Equal(CoreTable.FormatInt(weapon.Price), wrow[15]);

        // Launcher part row repeats its expendable's launcher definition.
        var launcher = core.Launchers.Values.First();
        var lrow = ReadCsv(tmp, "Expendables").First(r =>
            CoreTable.ParseInt(r[0]) == launcher.ExpendabletypeId
            && CoreTable.ParseInt(r[1]) == launcher.PartId);
        Assert.Equal(CoreTable.FormatInt(launcher.Amount), lrow[2]);
        Assert.Equal(launcher.InventoryLineMdl, lrow[5]);

        // Station type (abilities, defense comments, buyable tail).
        var station = core.StationTypes.Values.First();
        var srow = RowWithId(tmp, "TypesOfStations", station.StationTypeId);
        Assert.Equal(CoreTable.FormatFloat(station.MaxArmorHitPoints), srow[2]);
        Assert.Equal(CoreTable.FormatInt(station.Income), srow[7]);
        Assert.Equal(CoreTable.FormatInt(station.SuccessorStationTypeId), srow[9]);
        Assert.StartsWith(CoreTable.FormatInt(station.DefenseTypeArmor) + " (", srow[10]);
        Assert.Equal(station.Name, srow[22]);
        Assert.Equal(station.TextureName, srow[16]);
        var sfxRow = RowWithId(tmp, "StationsSFX", station.StationTypeId);
        Assert.Equal(CoreTable.FormatInt(station.CompletionSound), sfxRow[4]);
        Assert.Equal(CoreTable.FormatInt(station.EnemyDestroyedSound), sfxRow[13]);

        // Development (incl. its GlobalAttributeSet row; a non-default set is
        // the only one native writes a row for).
        var dev = core.Developments.Values.First(d => d.Gas.Any(g => g != 1f));
        var drow = RowWithId(tmp, "Developments", dev.DevelopmentId);
        Assert.Equal(CoreTable.FormatInt(dev.Price), drow[2]);
        Assert.Equal(CoreTable.FormatInt(dev.CompletionSound), drow[1]);
        Assert.Equal(dev.Name, drow[6]);
        var gasRow = ReadCsv(tmp, "GlobalAttributes").First(r =>
            CoreTable.ParseInt(r[0]) == dev.DevelopmentId && r[1] == dev.Name);
        Assert.Equal(CoreTable.FormatFloat(dev.Gas[0]), gasRow[2]);
        Assert.Equal(CoreTable.FormatFloat(dev.Gas[24]), gasRow[26]);

        // Drone, civilization, treasure set, constants.
        var drone = core.DroneTypes.Values.First();
        var drrow = RowWithId(tmp, "TypesOfDrones", drone.DroneTypeId);
        Assert.Equal(CoreTable.FormatFloat(drone.ShootSkill), drrow[1]);
        Assert.Equal(CoreTable.FormatInt(drone.HullTypeId), drrow[5]);
        Assert.Equal(drone.Name, drrow[11]);

        var civ = core.Civilizations.Values.First();
        var crow = RowWithId(tmp, "Civilizations", civ.CivilizationId);
        Assert.Equal(CoreTable.FormatFloat(civ.IncomeMoney), crow[1]);
        Assert.Equal(civ.Name, crow[3]);
        Assert.Equal(CoreTable.FormatInt(civ.Lifepod), crow[6]);
        var civGas = ReadCsv(tmp, "GlobalAttributes").First(r =>
            CoreTable.ParseInt(r[0]) == civ.CivilizationId && r[1] == civ.Name);
        Assert.Equal(CoreTable.FormatFloat(civ.GasBaseAttributes[3]), civGas[5]);

        var set = core.TreasureSets.Values.First();
        var trow = RowWithId(tmp, "TypesOfTreasures", set.TreasureSetId);
        Assert.Equal(set.Name, trow[1]);
        Assert.Equal(CoreTable.FormatInt(set.Entries.Length), trow[2]);
        Assert.StartsWith(CoreTable.FormatInt(set.Entries[0].TreasureId) + " ", trow[4]);

        var constants = core.Constants;
        var krow = ReadCsv(tmp, "Constants")[0];
        Assert.Equal(CoreTable.FormatFloat(constants.FloatConstants[0]), krow[1]);
        var damageRow = ReadCsv(tmp, "Constants")[CoreTable.CFcidMax + 3 * CoreTable.CDefidMax + 4];
        Assert.Equal(CoreTable.FormatFloat(constants.DamageConstants[3][4]), damageRow[1]);
    }

    // ------------------------------------------------------------------
    // Real-core round trip (SIM-0694 / SIM-0752 / SIM-0792).
    // ------------------------------------------------------------------

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void PCore001_round_trips_igc_to_text_to_igc_byte_identically()
    {
        Require.Artwork();
        string core = Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc");
        var result = StaticCoreVerify.VerifyFile(core);
        Assert.True(result.Ok, result.Detail);
        Assert.Contains("Constants: 1/1", result.Detail);
        Assert.Contains("HullType:", result.Detail);
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    [Trait("Tier", "Live")] // 100 cores x export+rebuild+compare ~8 s
    public void All_shipped_static_cores_round_trip_byte_identically()
    {
        Require.Artwork();
        string root = ExternalPaths.Require(ExternalResourceKind.Artwork);
        var cores = StaticCoreVerify.StaticCoresIn(root).ToList();
        Assert.True(cores.Count >= 90, $"expected the whole static-core corpus, found {cores.Count}");

        var failures = new List<string>();
        foreach (string core in cores)
        {
            var r = StaticCoreVerify.VerifyFile(core);
            if (!r.Ok) failures.Add($"{Path.GetFileName(core)}: {r.Detail}");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Text_export_is_stable_across_a_round_trip()
    {
        Require.Artwork();
        string corePath = Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc");
        var igc = IgcReader.ParseFile(corePath);
        using var first = new TempDir();
        using var second = new TempDir();
        StaticCoreRoundtrip.Export(igc, first.Path);
        File.WriteAllBytes(Path.Combine(second.Path, "rebuilt.igc"), StaticCoreRoundtrip.BuildIgc(first.Path));
        StaticCoreRoundtrip.Export(IgcReader.ParseFile(Path.Combine(second.Path, "rebuilt.igc")), second.Path);

        foreach (var spec in CoreFiles.All)
        {
            byte[] a = File.ReadAllBytes(Path.Combine(first.Path, spec.BaseName + ".csv"));
            byte[] b = File.ReadAllBytes(Path.Combine(second.Path, spec.BaseName + ".csv"));
            Assert.True(a.AsSpan().SequenceEqual(b), $"{spec.BaseName}.csv changed across the round trip");
        }
    }

    [SkippableFact]
    [Trait("Requires", "Artwork")]
    public void Foreign_text_folder_without_sidecar_imports_the_same_semantics()
    {
        Require.Artwork();
        string corePath = Path.Combine(ExternalPaths.Require(ExternalResourceKind.Artwork), "PCore001.igc");
        var igc = IgcReader.ParseFile(corePath);
        var original = StaticCore.Load(igc);
        using var tmp = new TempDir();
        StaticCoreRoundtrip.Export(igc, tmp.Path);

        // No .coremeta: native Text2Core semantics (canonical order, fresh
        // version, zeros where native leaves memory uninitialised).
        File.Delete(Path.Combine(tmp.Path, StaticCoreRoundtrip.MetaFileName));
        byte[] rebuilt = StaticCoreRoundtrip.BuildIgc(tmp.Path);
        var core = StaticCore.Load(IgcReader.Parse(rebuilt));

        Assert.Equal(original.ProjectileTypes.Count, core.ProjectileTypes.Count);
        Assert.Equal(original.MissileTypes.Count, core.MissileTypes.Count);
        Assert.Equal(original.MineTypes.Count, core.MineTypes.Count);
        Assert.Equal(original.ProbeTypes.Count, core.ProbeTypes.Count);
        Assert.Equal(original.ChaffTypes.Count, core.ChaffTypes.Count);
        Assert.Equal(original.HullTypes.Count, core.HullTypes.Count);
        Assert.Equal(original.PartTypes.Count, core.PartTypes.Count);
        Assert.Equal(original.Launchers.Count, core.Launchers.Count);
        Assert.Equal(original.StationTypes.Count, core.StationTypes.Count);
        Assert.Equal(original.Developments.Count, core.Developments.Count);
        Assert.Equal(original.DroneTypes.Count, core.DroneTypes.Count);
        Assert.Equal(original.Civilizations.Count, core.Civilizations.Count);
        Assert.Equal(original.TreasureSets.Count, core.TreasureSets.Count);

        foreach (var (id, hull) in original.HullTypes)
        {
            var got = core.HullTypes[id];
            Assert.Equal(hull.Name, got.Name);
            Assert.Equal(hull.Mass, got.Mass);
            Assert.Equal(hull.Hardpoints.Length, got.Hardpoints.Length);
            for (int i = 0; i < hull.Hardpoints.Length; i++)
                Assert.Equal(hull.Hardpoints[i].FrameName, got.Hardpoints[i].FrameName);
        }
        foreach (var (id, st) in original.StationTypes)
        {
            var got = core.StationTypes[id];
            Assert.Equal(st.Name, got.Name);
            Assert.Equal(st.SabmCapabilities, got.SabmCapabilities);
            Assert.Equal(st.AabmBuild, got.AabmBuild);
        }
        for (int i = 0; i < CoreTable.CFcidMax; i++)
            Assert.Equal(original.Constants.FloatConstants[i], core.Constants.FloatConstants[i]);
        Assert.Equal(original.TreasureSets.Values.First().Entries.Length,
            core.TreasureSets.Values.First().Entries.Length);
    }

    // ------------------------------------------------------------------
    // Helpers.
    // ------------------------------------------------------------------

    internal sealed class TempDir : IDisposable
    {
        public string Path { get; }

        public TempDir()
        {
            Path = System.IO.Path.Combine(StaticCoreRoundtrip.ScratchRoot(),
                "p870-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, true); } catch { /* best effort */ }
        }
    }

    private static string[] ReadLines(string path) =>
        File.ReadAllText(path, Encoding.Latin1).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

    private static string Header(TempDir dir, string name) => ReadLines(Path.Combine(dir.Path, name + ".csv"))[0];

    private static List<string[]> ReadCsv(TempDir dir, string name)
    {
        var lines = ReadLines(Path.Combine(dir.Path, name + ".csv"));
        return lines.Skip(1).Select(l => l.Split('\t')).ToList();
    }

    private static string[] RowWithId(TempDir dir, string name, int id, int idCol = 0)
        => ReadCsv(dir, name).First(r => int.TryParse(r[idCol], out var v) && v == id);

    private static List<string[]> RowsWithId(TempDir dir, string name, int id, int idCol = 0)
        => ReadCsv(dir, name).Where(r => int.TryParse(r[idCol], out var v) && v == id).ToList();
}

using System.Globalization;
using System.Text;

namespace Allegiance.Tools.Core2Txt;

/// <summary>
/// Faithful port of the native static-core text authoring helpers in
/// <c>src/Igc/missionigc.cpp:115-545</c> and the constants comment lookups
/// (<c>4246-4299</c>, <c>5316-5363</c>). Also carries the per-core
/// tech/part flag-name tables (<c>rgTechs</c>/<c>rgParts</c>, populated from
/// the optional <c>BitsOfTechs.csv</c>/<c>BitsOfParts.csv</c>), mirroring
/// <c>LoadTechBitsList</c>/<c>LoadPartsBitsList</c>.
///
/// <para>Bit semantics are taken from <c>TLargeBitMask</c>
/// (<c>src/zlib/mask.h:167-212</c>): <c>SetBit(i)</c> ORs
/// <c>0x80 &gt;&gt; (i &amp; 7)</c> into <c>m_bits[i &gt;&gt; 3]</c> (MSB-first
/// within each byte), <c>ToString</c> emits each byte with <c>%02X</c>
/// (UPPERCASE, 100 hex chars for the 400-bit mask) and <c>FromString</c>
/// rejects an odd-length string outright.</para>
/// </summary>
public sealed class CoreTable
{
    public const int CFcidMax = 40;
    public const int CDmgidMax = 20;
    public const int CDefidMax = 20;
    public const int CTtbMax = 400;
    public const int CTechs = 400;
    public const int CbTechBitName = 40;
    public const int ETMax = 8;

    /// <summary>Part bits 1..16 (<c>igc.h:706-721</c>, <c>c_pbm1..c_pbm16</c>).</summary>
    private static readonly PartMaskValue[] Pbm =
    {
        new(0, 0x01), new(1, 0x02), new(2, 0x04), new(3, 0x08),
        new(4, 0x10), new(5, 0x20), new(6, 0x40), new(7, 0x80),
        new(8, 0x100), new(9, 0x200), new(10, 0x400), new(11, 0x800),
        new(12, 0x1000), new(13, 0x2000), new(14, 0x4000), new(15, 0x8000),
    };

    private readonly record struct PartMaskValue(int Bit, int Mask);

    // Native equipment-type indices (igc.h:530-539).
    public const int ETChaffLauncher = 0;
    public const int ETWeapon = 1;
    public const int ETMagazine = 2;
    public const int ETDispenser = 3;
    public const int ETShield = 4;
    public const int ETCloak = 5;
    public const int ETPack = 6;
    public const int ETAfterburner = 7;

    public const int NA = -1;

    // ------------------------------------------------------------------
    // Native scalar formatting/parsing (zstring.cpp, atoi/atof).
    // ------------------------------------------------------------------

    /// <summary><c>ZString(int)</c> → <c>_itoa_s(...,10)</c>.
    /// <c>ZString(bool)</c> → <c>"true"</c>/<c>"false"</c> (<c>zstring.cpp:264-275</c>).</summary>
    public static string FormatInt(int value) => value.ToString(CultureInfo.InvariantCulture);
    public static string FormatUInt(uint value) => value.ToString(CultureInfo.InvariantCulture);
    public static string FormatBool(bool value) => value ? "true" : "false";

    /// <summary>Replicates native <c>ZString(float)</c> → <c>sprintf_s("%g", value)</c>
    /// (<c>zlib/zstring.cpp:257-262</c>), precision 6, trailing zeros trimmed.
    ///
    /// <para>Native <c>%g</c> rounds to 6 significant digits and therefore CANNOT
    /// reproduce every float bit pattern (e.g. the MSVC uninitialised marker
    /// <c>0xCDCDCDCD</c>). P870 asks for a byte-identical round trip, so we keep
    /// the exact <c>%g</c> spelling whenever it parses back to the identical
    /// float and fall back to the shortest exact round-trip spelling otherwise.
    /// Cores whose values all fit native's 6 digits get byte-for-byte native
    /// text.</para></summary>
    public static string FormatFloat(float value)
    {
        if (float.IsNaN(value) || float.IsInfinity(value))
            return value.ToString(CultureInfo.InvariantCulture); // "NaN"/"Infinity"/"-Infinity"
        string g = FormatPercentG(value);
        if (BitConverter.SingleToInt32Bits(ParseFloat(g)) == BitConverter.SingleToInt32Bits(value))
            return g;
        return value.ToString("R", CultureInfo.InvariantCulture);
    }

    private static string FormatPercentG(float value)
    {
        if (value == 0f) return BitConverter.SingleToInt32Bits(value) < 0 ? "-0" : "0";
        bool neg = value < 0f;
        double av = Math.Abs((double)value);
        int exp = (int)Math.Floor(Math.Log10(av));
        string s;
        if (exp < -4 || exp >= 6)
        {
            s = av.ToString("E5", CultureInfo.InvariantCulture);
            int e = s.IndexOf('E');
            string mant = TrimG(s[..e]);
            int eval = int.Parse(s[(e + 1)..], CultureInfo.InvariantCulture);
            s = mant + "e" + (eval >= 0 ? "+" : "-") + Math.Abs(eval).ToString("00", CultureInfo.InvariantCulture);
        }
        else
        {
            int digits = 6 - 1 - exp;
            if (digits < 0) digits = 0;
            s = TrimG(av.ToString("F" + digits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture));
        }
        return neg ? "-" + s : s;
    }

    private static string TrimG(string s)
    {
        int dot = s.IndexOf('.');
        if (dot >= 0)
        {
            int end = s.Length;
            while (end > dot + 1 && s[end - 1] == '0') end--;
            if (end == dot + 1) end = dot;
            if (end < s.Length) s = s[..end];
        }
        return s;
    }

    /// <summary>Native <c>atof</c>: parse the longest numeric prefix, 0 otherwise.</summary>
    public static float ParseFloat(string s)
    {
        double d = ParseAtod(s);
        return (float)d;
    }

    private static double ParseAtod(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (i < s.Length && s[i] == '.')
        {
            i++;
            while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        }
        if (digits == 0) return 0;
        int end = i;
        if (i < s.Length && (s[i] == 'e' || s[i] == 'E'))
        {
            int j = i + 1;
            if (j < s.Length && (s[j] == '+' || s[j] == '-')) j++;
            int expDigits = 0;
            while (j < s.Length && char.IsAsciiDigit(s[j])) { j++; expDigits++; }
            if (expDigits > 0) end = j;
        }
        return double.TryParse(s[start..end], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    /// <summary>Length of the leading number in <paramref name="s"/> after optional
    /// whitespace/sign (0 when it does not start with a number).</summary>
    public static int NumericPrefixLength(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        int start = i;
        while (i < s.Length && char.IsAsciiDigit(s[i])) i++;
        return i > start ? i : 0;
    }

    /// <summary>Native <c>atoi</c>: leading whitespace, optional sign, digits, stop.
    /// Deliberately lenient — <c>"3abc"</c> is <c>3</c> (unlike <c>int.Parse</c>).</summary>
    public static int ParseInt(string s)
    {
        int i = 0;
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        int start = i;
        if (i < s.Length && (s[i] == '+' || s[i] == '-')) i++;
        int digits = 0;
        while (i < s.Length && char.IsAsciiDigit(s[i])) { i++; digits++; }
        if (digits == 0) return 0;
        return int.TryParse(s[start..i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var v)
            ? v : 0;
    }

    /// <summary>Native <c>atoi</c> of the first space-delimited token
    /// (<c>ZString::GetToken().GetInteger()</c>, <c>zstring.cpp:485,592</c>).</summary>
    public static int ParseTokenInt(string s)
    {
        foreach (string t in Tokens(s)) return ParseInt(t);
        return 0;
    }

    private static string Token(string s, ref int pos)
    {
        int i = pos;
        while (i < s.Length && s[i] == ' ') i++;
        int start = i;
        while (i < s.Length && s[i] != ' ') i++;
        pos = i;
        return s[start..i];
    }

    /// <summary>Tokenize on spaces, mirroring <c>strtok(s, " ")</c>: runs of spaces
    /// collapse and empty tokens are never produced.</summary>
    public static IEnumerable<string> Tokens(string s)
    {
        int pos = 0;
        while (true)
        {
            string t = Token(s, ref pos);
            if (t.Length == 0) yield break;
            yield return t;
        }
    }

    /// <summary>The native boolean spelling is <c>true</c>/<c>false</c>
    /// (<c>ZString(bool)</c>); native's own reader feeds that through <c>atoi</c>
    /// and so always gets 0. We accept the writer's spelling, which is what makes
    /// the round trip lossless; a numeric token keeps native's meaning.</summary>
    public static bool ParseBool(string s)
    {
        string t = s.Trim();
        if (t.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (t.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        return ParseInt(t) != 0;
    }

    // ------------------------------------------------------------------
    // Tech/part flag names (rgTechs / rgParts).
    // ------------------------------------------------------------------

    /// <summary>Optional <c>BitsOfTechs.csv</c> contents by bit id.</summary>
    private readonly Dictionary<int, string> _techNames = new();

    /// <summary>Optional <c>BitsOfParts.csv</c> contents: [et][bit]=name.</summary>
    private readonly string?[,] _partNames = new string?[ETMax, 16];

    public bool TechBitsLoaded { get; private set; }
    public bool PartBitsLoaded { get; private set; }

    /// <summary>Native <c>GetTechFlagName</c> (<c>missionigc.h:826-829</c>).</summary>
    public string GetTechFlagName(int id)
        => id >= 0 && id < CTechs && _techNames.TryGetValue(id, out var n) ? n : "";

    public void SetTechFlagName(int id, string name)
    {
        if (id >= 0 && id < CTechs) _techNames[id] = name;
    }

    /// <summary>Native <c>GetPartFlagName</c> (<c>missionigc.h:836-839</c>).</summary>
    public string GetPartFlagName(int id, int et)
    {
        if (et < 0 || et >= ETMax || id < 0 || id >= 16) return "";
        return _partNames[et, id] ?? "";
    }

    public void SetPartFlagName(int bit, int et, string name)
    {
        if (et >= 0 && et < ETMax && bit >= 0 && bit < 16) _partNames[et, bit] = name;
    }

    // ------------------------------------------------------------------
    // Token ↔ mask converters (SIM-0686..0693, SIM-0785).
    // ------------------------------------------------------------------

    /// <summary>SIM-0686. Native <c>CmissionIGC::TechBitFromToken@115</c>
    /// (<c>missionigc.cpp:115-125</c>): case-insensitive scan of <c>rgTechs</c>;
    /// <see cref="NA"/> when no flag carries the token.</summary>
    public int TechBitFromToken(string token)
    {
        for (int itoken = 0; itoken < CTechs; itoken++)
            if (string.Equals(GetTechFlagName(itoken), token, StringComparison.OrdinalIgnoreCase))
                return itoken;
        return NA;
    }

    /// <summary>SIM-0687. Native <c>CmissionIGC::TechsListToBits@128</c>
    /// (<c>missionigc.cpp:128-149</c>): clear, literal <c>"0"</c> → empty,
    /// digit-leading → <c>FromString</c> hex, else space tokens → <c>SetBit</c>.
    /// Native calls <c>SetBit(TechBitFromToken(token))</c> even for an unknown
    /// token, i.e. <c>SetBit(-1)</c>, which indexes <c>m_bits[-1]</c> (undefined
    /// behaviour / memory corruption). The port keeps the sentinel and ignores
    /// it.</summary>
    public void TechsListToBits(string techs, bool[] bits)
    {
        Array.Clear(bits);
        if (techs == "0") return;
        if (techs.Length > 0 && char.IsAsciiDigit(techs[0]))
        {
            FromString(techs, bits);
            return;
        }
        foreach (string tok in Tokens(techs))
        {
            int b = TechBitFromToken(tok);
            if (b != NA && b < bits.Length) bits[b] = true;
        }
    }

    /// <summary>Native <c>TLargeBitMask::FromString</c> (<c>zlib/mask.h:205-230</c>):
    /// even-length hex, MSB-first bytes, clears the mask first; returns false
    /// (mask untouched) on odd length.</summary>
    public static bool FromString(string hex, bool[] bits)
    {
        if (hex.Length % 2 != 0) return false;
        Array.Clear(bits);
        int nbytes = Math.Min(hex.Length / 2, (CTtbMax + 7) / 8);
        for (int i = 0; i < nbytes; i++)
        {
            int v = ParseHexByte(hex[(i * 2)..(i * 2 + 2)]);
            for (int k = 0; k < 8; k++)
            {
                int bit = i * 8 + k;
                if ((v & (0x80 >> k)) != 0 && bit < bits.Length) bits[bit] = true;
            }
        }
        return true;
    }

    /// <summary>Native mask bytes (<c>m_bits[]</c>) → bit array; bytes beyond
    /// the destination are ignored.</summary>
    public static void FromBytes(ReadOnlySpan<byte> bytes, bool[] bits)
    {
        Array.Clear(bits);
        for (int i = 0; i < bytes.Length; i++)
            for (int k = 0; k < 8; k++)
            {
                int bit = i * 8 + k;
                if ((bytes[i] & (0x80 >> k)) != 0 && bit < bits.Length) bits[bit] = true;
            }
    }

    private static int ParseHexByte(string two)
        => int.TryParse(two, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0;

    /// <summary>Native <c>TLargeBitMask::ToString</c> (<c>zlib/mask.h:194-203</c>):
    /// 50 bytes, <c>%02X</c> each → 100 uppercase hex chars, bit 0 = MSB of byte 0.</summary>
    public static string MaskToString(bool[] bits)
    {
        const int nbytes = (CTtbMax + 7) / 8;
        var sb = new StringBuilder(nbytes * 2);
        for (int i = 0; i < nbytes; i++)
        {
            int v = 0;
            for (int k = 0; k < 8; k++)
            {
                int bit = i * 8 + k;
                if (bit < bits.Length && bits[bit]) v |= 0x80 >> k;
            }
            sb.Append(v.ToString("X2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary>SIM-0688. Native <c>CmissionIGC::PartMaskFromToken@152</c>
    /// (<c>missionigc.cpp:152-206</c>). Native indexes <c>rgParts[et][16]</c>
    /// out of bounds on the 17th comparison; the port stops at 16.</summary>
    public short PartMaskFromToken(string token, int et)
    {
        // Native only checks isdigit(token[0]), so the writer's own negative
        // decimal spelling (PartMask is a signed short; e.g. c_pbm16 alone reads
        // -32768) is not recognised and becomes 0. Accept a signed number too.
        int numeric = NumericPrefixLength(token);
        if (numeric > 0)
            return unchecked((short)ParseInt(token));

        string tmp = token.Replace(" ", "");
        if (tmp.Length == 0) return 0;

        for (int itoken = 0; itoken < 16; itoken++)
            if (string.Equals(GetPartFlagName(itoken, et), tmp, StringComparison.OrdinalIgnoreCase))
                return unchecked((short)Pbm[itoken].Mask);
        return 0;
    }

    /// <summary>SIM-0689. Native <c>CmissionIGC::PartsListToMask@361</c>
    /// (<c>missionigc.cpp:361-374</c>): digit-leading → <c>atoi</c>, otherwise
    /// space tokens <b>added</b> (so a repeated flag adds twice).</summary>
    public short PartsListToMask(string parts, int et)
    {
        if (parts.Length > 0 && char.IsAsciiDigit(parts[0]))
            return unchecked((short)ParseInt(parts));

        short pm = 0;
        foreach (string tok in Tokens(parts))
            pm = unchecked((short)(pm + PartMaskFromToken(tok, et)));
        return pm;
    }

    /// <summary>SIM-0690. Native <c>CmissionIGC::BitsToTechsList@377</c>
    /// (<c>missionigc.cpp:377-413</c>): raw uppercase hex when the name table is
    /// not loaded, otherwise space-separated names (trailing space), with the
    /// raw-mask fallback for a short non-empty list and literal <c>"0"</c> for an
    /// empty mask. The 100-char truncation is native's buffer size.</summary>
    public string BitsToTechsList(bool[] ttbm)
    {
        if (GetTechFlagName(0) != "unavailable")
            return MaskToString(ttbm);

        var sb = new StringBuilder();
        for (int i = 0; i < CTtbMax; i++)
        {
            if (!ttbm[i]) continue;
            string name = GetTechFlagName(i);
            if (name.Length != 0)
            {
                sb.Append(name);
                if (i < CTtbMax - 1) sb.Append(' ');
            }
        }
        if (sb.Length < 3)
        {
            if (Array.IndexOf(ttbm, true) >= 0) return MaskToString(ttbm);
            return "0";
        }
        return sb.ToString();
    }

    /// <summary>SIM-0691. Native <c>CmissionIGC::BitsToPartsList@417</c>
    /// (<c>missionigc.cpp:417-449</c>): decimal <c>PartMask</c> when the name
    /// table is not loaded, otherwise names each followed by a space.</summary>
    public string BitsToPartsList(short pm, int et)
    {
        if (GetPartFlagName(0, et).Length < 3)
            return FormatInt(pm);

        var sb = new StringBuilder();
        foreach (var p in Pbm)
            if ((p.Mask & pm) != 0)
                sb.Append(GetPartFlagName(p.Bit, et)).Append(' ');
        return sb.Length > 3 ? sb.ToString() : FormatInt(pm);
    }

    /// <summary>SIM-0692. Native <c>CmissionIGC::LoadTechBitsList@451</c>
    /// (<c>missionigc.cpp:451-486</c>): tab CSV, skip header, column 1 = bit id,
    /// column 2 = short name. <c>strFlag</c> persists across rows in native, so a
    /// short row reuses the previous name; missing file → false.</summary>
    public bool LoadTechBitsList(string path)
    {
        if (!File.Exists(path)) return false;
        var rows = ReadCsv(path);
        string flag = "";
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Count == 0) continue;
            int id = ParseInt(r[0]);
            if (r.Count > 1) flag = r[1];
            SetTechFlagName(id, flag);
        }
        TechBitsLoaded = true;
        return true;
    }

    /// <summary>SIM-0693. Native <c>CmissionIGC::LoadPartsBitsList@487</c>
    /// (<c>missionigc.cpp:487-526</c>): tab CSV, skip header, column 1 = equipment
    /// type, column 2 = flag name, bit index cycles 0..15. Native's
    /// <c>if (bitnum &gt;= 16) { bitnum = 0; if (et == ET_MAX-1) break; }</c> tests
    /// the just-declared <c>et</c> (= 0), so the break is dead code; out-of-range
    /// equipment types then index <c>rgParts[et]</c> out of bounds. The port
    /// mirrors the loop but drops rows whose equipment type is out of range.
    /// Missing file → false.</summary>
    public bool LoadPartsBitsList(string path)
    {
        if (!File.Exists(path)) return false;
        var rows = ReadCsv(path);
        int bitnum = 0;
        for (int i = 1; i < rows.Count; i++)
        {
            var r = rows[i];
            if (r.Count == 0) continue;
            if (bitnum >= 16) bitnum = 0;
            int et = ParseInt(r[0]);
            string flag = r.Count > 1 ? r[1] : "";
            SetPartFlagName(bitnum, et, flag);
            bitnum++;
        }
        PartBitsLoaded = true;
        return true;
    }

    /// <summary>SIM-0786. Native <c>readCSV@530</c> (<c>missionigc.cpp:530-545</c>):
    /// <c>std::getline(..., '\t')</c> per line, so a trailing empty field is
    /// DROPPED (<c>"1\t2\t"</c> → 2 columns) while interior empty fields and the
    /// header row are kept. This variant strips the line terminator (ReadLine);
    /// <see cref="ReadCsvNativeCarriageReturn"/> is the byte-exact native variant
    /// that keeps a CRLF file's <c>\r</c> in the last field.</summary>
    public static List<List<string>> ReadCsv(string path)
    {
        var output = new List<List<string>>();
        foreach (string rawLine in ReadLinesLf(path))
            output.Add(SplitTabs(rawLine));
        return output;
    }

    private static IEnumerable<string> ReadLinesLf(string path)
    {
        using var reader = new StreamReader(path, Encoding.Latin1);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
        // StreamReader.ReadLine strips \r\n / \n / \r; native's getline only
        // strips \n, leaving a lone \r in the last field. Callers that need the
        // exact native bytes use ReadCsvNative below.
    }

    /// <summary>Native-exact variant of <see cref="ReadCsv"/>: splits on <c>\n</c>
    /// only, so a CRLF file's last field keeps its <c>\r</c> (what native
    /// <c>readCSV</c> actually produces). Exposed for the SIM-0786 test; the
    /// static-core importer uses <see cref="ReadCsv"/> + explicit CR trimming,
    /// because native's CR-in-last-field behaviour corrupts the last mask/flag.</summary>
    public static List<List<string>> ReadCsvNativeCarriageReturn(string path)
    {
        var output = new List<List<string>>();
        string text = File.ReadAllText(path, Encoding.Latin1);
        int start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n') continue;
            string line = text[start..i];
            if (line.EndsWith('\r')) { /* keep the \r: native does */ }
            output.Add(SplitTabs(line));
            start = i + 1;
        }
        if (start < text.Length) output.Add(SplitTabs(text[start..]));
        return output;
    }

    /// <summary><c>std::getline</c> over a tab delimiter: a trailing delimiter
    /// does not yield a final empty field.</summary>
    private static List<string> SplitTabs(string line)
    {
        var cols = new List<string>();
        if (line.Length == 0) return cols;
        int start = 0;
        for (int i = 0; i < line.Length; i++)
        {
            if (line[i] != '\t') continue;
            cols.Add(line[start..i]);
            start = i + 1;
        }
        if (start < line.Length) cols.Add(line[start..]);
        return cols;
    }

    // ------------------------------------------------------------------
    // Ability masks (SIM-0785).
    // ------------------------------------------------------------------

    private static readonly (string Name, short Mask)[] HullAbilities =
    {
        ("CanBoard", 0x01), ("CanRescue", 0x02), ("IsPod", 0x04),
        ("CanCapture", 0x08), ("CanLandOnCarrier", 0x10), ("CanNotRip", 0x20),
        ("IsRipcordTarget", 0x40), ("IsFighter", 0x80),
        ("IsRemoteLeadIndicator", 0x100), ("CanBomb", 0x200),
        ("IsCarrier", 0x400), ("CanLeadIndicator", 0x800),
        ("IsLtRip", 0x1000), ("CanLtRip", 0x2000), ("IsMiner", 0x4000),
        ("IsBuilder", unchecked((short)0x8000)),
    };

    private static readonly (string Name, short Mask)[] StationAbilities =
    {
        ("CanBoard", 0x01), ("CanOffloadMinerals", 0x01), ("CanStartHere", 0x02),
        ("CanRestartHere", 0x04), ("CanRipHere", 0x08), ("CanBeCaptured", 0x10),
        ("CanLandHere", 0x20), ("CanRepair", 0x40), ("IsVisibleInLoadout", 0x80),
        ("CanReload", 0x100), ("IsVictoryObjective", 0x200),
        ("IsFlagPedestal", 0x400), ("CanTeleportOffloadMinerals", 0x800),
        ("CanLandCapitols", 0x1000), ("CanRescuePods", 0x2000),
        ("CanRescueAnyPods", 0x4000),
    };

    private static readonly (string Name, short Mask)[] ExpendableAbilities =
    {
        ("CanCaptureStations", 0x01), ("CanWarpBombDual", 0x02),
        ("CanWarpBombSingle", 0x04), ("CanQuickReady", 0x08), ("CanRipcord", 0x10),
        ("CanShootStations", 0x20), ("CanShootShips", 0x40),
        ("CanShootMissiles", 0x80), ("CanShootTargetOnly", 0x1000),
        ("CanRescue", 0x2000), ("CanRescueAny", 0x4000),
    };

    private static readonly (string Name, short Mask)[] AsteroidAbilities =
    {
        ("CanBeRegular", 0x08), ("CanBeUranium", 0x10), ("CanBeSilicon", 0x20),
        ("CanBeCarbon", 0x40), ("CanBeHelium", 0x01), ("CanBeDenseHelium", 0x02),
        ("CanBeGold", 0x04),
    };

    /// <summary>Native AT_Hull.</summary>
    public const int ATHull = 0;
    /// <summary>Native AT_Station.</summary>
    public const int ATStation = 1;
    /// <summary>Native AT_Expendable.</summary>
    public const int ATExpendable = 2;
    /// <summary>Native AT_Asteroid.</summary>
    public const int ATAsteroid = 3;

    private static (string Name, short Mask)[] AbilityTable(int at) => at switch
    {
        ATHull => HullAbilities,
        ATStation => StationAbilities,
        ATExpendable => ExpendableAbilities,
        ATAsteroid => AsteroidAbilities,
        _ => Array.Empty<(string, short)>(),
    };

    /// <summary>SIM-0785. Native <c>AbilitiesListToMask@209</c>
    /// (<c>missionigc.cpp:209-357</c>): space tokens, each prefix-matched against
    /// the ability names for the kind (<c>ReverseFind(name) == 0</c>), OR-ing the
    /// mapped bit. Native ORs <em>every</em> matching name, so the overlapping
    /// native names <c>CanRescue</c>/<c>CanRescueAny</c> make the writer's
    /// <c>"CanRescueAny"</c> read back as both bits; that is a writer/reader
    /// asymmetry that breaks the native round trip. The port keeps native's
    /// prefix rule but resolves each token to its LONGEST matching name, which is
    /// exactly what the writer emitted and is unambiguous for every token the
    /// writer can produce.</summary>
    public static short AbilitiesListToMask(string abilities, int at)
    {
        if (abilities.Length == 0) return 0;
        var table = AbilityTable(at);
        short mask = 0;
        foreach (string tok in Tokens(abilities))
        {
            int best = -1;
            int bestLen = -1;
            for (int i = 0; i < table.Length; i++)
            {
                if (table[i].Name.Length <= bestLen) continue;
                if (!tok.StartsWith(table[i].Name, StringComparison.Ordinal)) continue;
                best = i;
                bestLen = table[i].Name.Length;
            }
            if (best >= 0) mask |= table[best].Mask;
        }
        return mask;
    }

    /// <summary>Native's <c>Obj2Txt</c> writer order. The station writer does not
    /// emit <c>CanBoard</c> (it shares bit 0x01 with <c>CanOffloadMinerals</c>),
    /// while the reader accepts both spellings.</summary>
    private static readonly (string Name, short Mask)[] StationExportAbilities =
    {
        ("CanOffloadMinerals", 0x01), ("CanStartHere", 0x02),
        ("CanRestartHere", 0x04), ("CanRipHere", 0x08), ("CanBeCaptured", 0x10),
        ("CanLandHere", 0x20), ("CanRepair", 0x40), ("IsVisibleInLoadout", 0x80),
        ("CanReload", 0x100), ("IsVictoryObjective", 0x200),
        ("IsFlagPedestal", 0x400), ("CanTeleportOffloadMinerals", 0x800),
        ("CanLandCapitols", 0x1000), ("CanRescuePods", 0x2000),
        ("CanRescueAnyPods", 0x4000),
    };

    /// <summary>The inverse of <see cref="AbilitiesListToMask"/>: native's exact
    /// <c>Obj2Txt</c> order, each name followed by a space.</summary>
    public static string AbilitiesListToStrings(short mask, int at)
    {
        var table = at == ATStation ? StationExportAbilities : AbilityTable(at);
        var sb = new StringBuilder();
        foreach (var (name, bit) in table)
            if ((mask & bit) != 0)
                sb.Append(name).Append(' ');
        return sb.ToString();
    }

    // ------------------------------------------------------------------
    // Constant comments (SIM-0791, SIM-0793).
    // ------------------------------------------------------------------

    /// <summary>SIM-0791. Native <c>ConstantsDamageComment</c>
    /// (<c>missionigc.cpp:4246-4299</c>).</summary>
    public static string ConstantsDamageComment(int defenseid, int damageid)
    {
        string comment = "Uncommented damage constant";
        string comment2 = "Uncommented defense constant";
        switch (damageid)
        {
            case 0: comment = "Asteroid"; break;
            case 1: comment = "Plasma"; break;
            case 2: comment = "Gattling"; break;
            case 3: comment = "Disruptor"; break;
            case 4: comment = "Sniper"; break;
            case 5: comment = "Mini"; break;
            case 6: comment = "Cannon"; break;
            case 7: comment = "Galv"; break;
            case 8: comment = "Anti Base"; break;
            case 9: comment = "Seismic"; break;
            case 10: comment = "Nan"; break;
            case 11: comment = "AC Turret"; break;
            case 12: comment = "EMP"; break;
            case 13: comment = "More EMP"; break;
            case 14: comment = "Gas/HTT"; break;
            case 15: comment = "SkyRip"; break;
            case 16: comment = "Mine"; break;
            case 17: comment = "Stinger"; break;
            case 18: comment = "Lancer"; break;
            case 19: comment = "Ion"; break;
        }
        switch (defenseid)
        {
            case 0: comment2 = "Rock"; break;
            case 1: comment2 = "Light Ship"; break;
            case 2: comment2 = "Medium Ship"; break;
            case 3: comment2 = "Heavy Ship"; break;
            case 4: comment2 = "Super Heavy Ship"; break;
            case 5: comment2 = "Utility Ship"; break;
            case 6: comment2 = "Light Base"; break;
            case 7: comment2 = "Heavy Base"; break;
            case 8: comment2 = "Ship Shields"; break;
            case 9: comment2 = "Light Base Shields"; break;
            case 10: comment2 = "Heavy Base Shields"; break;
            case 11: comment2 = "Probe"; break;
            case 12: comment2 = "Ultra Light Base Armor"; break;
            case 13: comment2 = "Ultra Light Base Shields"; break;
            case 14: comment2 = "Large Ship Shields"; break;
        }
        if (defenseid >= 0)
            return damageid < 0 ? comment2 : comment + " vs. " + comment2;
        if (damageid >= 0)
            return defenseid < 0 ? comment : comment + " vs. " + comment2;
        return "Invincible";
    }

    /// <summary>SIM-0793. Native <c>ConstantsGlobalComment</c>
    /// (<c>missionigc.cpp:5316-5363</c>).</summary>
    public static string ConstantsGlobalComment(int constid)
    {
        string comment = "New uncommented constant";
        switch (constid)
        {
            case 0: comment = "Universe (sizing) lens multiplier."; break;
            case 1: comment = "Universe (sizing) radius modifier."; break;
            case 2: comment = "Universe (sizing) outer bounds modifier."; break;
            case 3: comment = "Ship (speed) modifier (exiting an aleph)."; break;
            case 4: comment = "Ship (speed) modifier (exiting a station)."; break;
            case 5: comment = "Shield (damage) 'downed' variable (determine if shields are down)."; break;
            case 6: comment = "Payday (economy) timer (# sec/payday)."; break;
            case 7: comment = "He3 asteroid (economy) capacity. "; break;
            case 8: comment = "He3 mineral (economy) unit price (worth)."; break;
            case 9: comment = "He3 amount (economy) in universe per team."; break;
            case 10: comment = "Ship (equipment, speed) time to mount."; break;
            case 11: comment = "Starting cash base (economy)."; break;
            case 12: comment = "Prosperity cash (game type) goal base."; break;
            case 13: comment = "Ripcord time (speed) base (seconds)."; break;
            case 14: comment = "He3 regeneration (economy) rate."; break;
            case 15: comment = "Point modifier (rank) for finding an aleph."; break;
            case 16: comment = "Point modifier (rank) for finding a rock."; break;
            case 17: comment = "Point modifier (rank) for retrieving a tech."; break;
            case 18: comment = "Point modifier (rank) for destroying a miner."; break;
            case 19: comment = "Point modifier (rank) for destroying a builder (constructor)."; break;
            case 20: comment = "Point modifier (rank) for destroying a layer (caltrop/tower)."; break;
            case 21: comment = "Point modifier (rank) for destroying a carrier (drone)."; break;
            case 22: comment = "Point modifier (rank) for destroying a player."; break;
            case 23: comment = "Point modifier (rank) for destorying a base."; break;
            case 24: comment = "Point modifier (rank) for capturing a base."; break;
            case 25: comment = "Point modifier (rank) for retrieving a flag."; break;
            case 26: comment = "Point modifier (rank) for recovering an artifcat."; break;
            case 27: comment = "Point modifier (rank) for picking up a pod."; break;
            case 28: comment = "Combat rating modifier (rank) added to kill math"; break;
            case 29: comment = "Combat rating modifier (rank) divided by kill math."; break;
            case 30: comment = "Payday (economy) size ($/payday)."; break;
            case 31: comment = "Escape pod (gameplay) O2 supply."; break;
            case 32: comment = "Warp bomb detonation timer."; break;
            case 33: comment = "Cost to update (performance) lifepods."; break;
            case 34: comment = "Cost to update (performance) turrets."; break;
            case 35: comment = "Cost to update (performance) drones."; break;
            case 36: comment = "Cost to update (performance) players."; break;
            case 37: comment = "Base sector update cost (performance) in a sector."; break;
            case 38: comment = "Cost to update (performance) divided by move math."; break;
        }
        return comment;
    }
}

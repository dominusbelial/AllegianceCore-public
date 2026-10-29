using Allegiance.Core;

namespace Allegiance.Tools.Core2Txt;

/// <summary>One text file a type codec writes (export) and reads (import).</summary>
public sealed record TypeFileSpec(string BaseName, string Header);

/// <summary>
/// Every tab-delimited file native <c>ExportStaticIGCObjs</c> writes
/// (<c>src/Igc/missionigc.cpp:5396-5706</c>), with the native header lines copied
/// verbatim (including native's own typos: <c>TURN TORQUES Z Y Z</c>,
/// <c>DEFENSE CONSTNAT ID</c>, <c>ACCELLERATION</c>, <c>DISPURSION</c>).
/// </summary>
public static class CoreFiles
{
    public static readonly TypeFileSpec Constants = new("Constants",
        "GLOBALID OR DEFENSE CONSTANTIDS\tGLOBAL VALUE OR DAMAGE CONSTANTID\tCOMMENT");

    public static readonly TypeFileSpec GlobalAttributes = new("GlobalAttributes",
        "ID\tCOPY OF NAME\tMAX SPEED\tTHRUST\tTURN RATE\tTURN TORQUE\tMAX STATION ARMOR\tSTATION ARMOR REGEN\tMAX STATION SHIELD\tSTATION SHIELD REGEN\tSHIP MAX ARMOR\tSHIP MAX SHIELD\tSHIP SHIELD REGEN\tSCAN RANGE\tSIGNATURE\tMAX ENERGY\tAMMO RANGE\tENERGY LIFESPAN\tMISSILE TURN RATE\tMINING RATE\tMINING YIELD\tMINING CAPACITY\tIP TIME\tWEAPON DAMAGE\tMISSILE DAMAGE\tDEVELOPMENT COST\tDEVELOPMENT TIME");

    public static readonly TypeFileSpec ShipsSFX = new("ShipsSFX",
        "ID\tCOPY OF NAME\tINTERIOR\tEXTERIOR\tBOOST INTERIOR\tBOOST EXTERIOR\tTHRUST INTERIOR\tTHRUST EXTERIOR");

    public static readonly TypeFileSpec StationsSFX = new("StationsSFX",
        "ID\tCON NEEDS ROCK\tCON UNDER ATTACK\tCON DESTROYED\tBUILD COMPLETION\tINTERIOR\tEXTERIOR\tALERT\tUNDER ATTACK\tCRITICAL\tDESTROYED\tCAPTURED\tCAPTURED ENEMY LISTENER\tDESTROYED ENEMY LISTENER");

    public static readonly TypeFileSpec TypesOfStations = new("TypesOfStations",
        "ID\tSIGNATURE\tMAX ARMOR HP\tMAX SHIELD HP\tARMOR REGEN RATE\tSHIELD REGEN RATE\tSCANNER RANGE\tINCOME\tRADIUS\tSUCCESSOR STATION TYPE ID\tARMOR DEFENSE ID\tSHIELD DEFENSE ID\tABILITIES\tALLOWED ASTEROIDS\tCLASS ID\tCON DRONE ID\tTEXTURE NAME\tCON NAME\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT\tTECH SELF");

    public static readonly TypeFileSpec TypesOfProjectiles = new("TypesOfProjectiles",
        "ID\tPOWER\tBLAST POWER\tBLAST RADIUS\tSPEED\tLIFE SPAN\tDAMAGE TYPE\tABSOLUTE\tDIRECTIONAL\tWIDTH\tAMBIENT SFX ID\tCOLOR R G B A PCT\tRADIUS\tROTATION\tMODEL NAME\tTEXTURE NAME\tCOPY OF NAMES");

    public static readonly TypeFileSpec ExpendableMissiles = new("ExpendableMissiles",
        "ID\tACCELLERATION\tTURN RATE\tINITIAL SPEED\tLOCK TIME\tREADY TIME\tMAX LOCK\tCHAFF RESISTANCE\tDISPERSION\tLOCK ANGLE\tPOWER\tBLAST POWER\tBLAST RADIUS\tWIDTH\tDAMAGE CONSTANT ID\tDIRECTIONAL\tLAUNCH SFX ID\tAMBIENT SFX ID\tLOAD TIME\tLIFE SPAN\tSIGNATURE\tHIT POINTS\tDEFENSE CONSTANT ID\tABILITIES\tICON NAME\tCOLOR R G B A PCT\tRADIUS\tROTATION\tMODEL NAME\tTEXTURE NAME\tLAUNCHER SIGNATURE\tMASS\tPART MASK\tEXPENDABLE SIZE\tCOPY OF NAME");

    public static readonly TypeFileSpec ExpendableMines = new("ExpendableMines",
        "ID\tMINE RADIUS\tPOWER\tENDURANCE\tDAMAGE CONSTANT ID\tLOAD TIME\tLIFE SPAN\tSIGNATURE\tHIT POINTS\tDEFENSE CONSTANT ID\tABILITIES\tICON NAME\tCOLOR R G B A PCT\tRADIUS\tROTATION\tMODEL NAME\tTEXTURE NAME\tLAUNCHER SIGNATURE\tMASS\tPART MASK\tEXPENDABLE SIZE\tCOPY OF NAME");

    public static readonly TypeFileSpec ExpendableChaff = new("ExpendableChaff",
        "ID\tSTRENGTH\tLOAD TIME\tLIFE SPAN\tSIGNATURE\tHIT POINTS\tDEFENSE CONSTANT ID\tABILITIES\tICON NAME\tCOLOR R G B A PCT\tRADIUS\tROTATION\tMODEL NAME\tTEXTURE NAME\tLAUNCHER SIGNATURE\tMASS\tPART MASK\tEXPENDABLE SIZE\tCOPY OF NAME");

    public static readonly TypeFileSpec ExpendableProbes = new("ExpendableProbes",
        "ID\tSCAN RANGE\tTIME TO LIVE\tDISPURSION\tACCURACY\tAMMO\tPROJECTILE TYPE ID\tAMBIENT SFX ID\tTIME TO RIP\tLOAD TIME\tLIFE SPAN\tSIGNATURE\tHIT POINTS\tDEFENSE CONSTANT ID\tABILITIES\tICON NAME\tCOLOR R G B A PCT\tRADIUS\tROTATION\tMODEL NAME\tTEXTURE NAME\tLAUNCHER SIGNATURE\tMASS\tPART MASK\tEXPENDABLE SIZE\tCOPY OF NAME");

    public static readonly TypeFileSpec Expendables = new("Expendables",
        "EXPENDABLE ID\tPART ID\tAMOUNT\tSUCCESSOR PART ID\tLAUNCH COUNT\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Weapons = new("Weapons",
        "ID\tREADY TIME\tBURST TIME\tENERGY PER SHOT\tDISPERSION\tAMMO PER SHOT\tPROJECTILE TYPE ID\tACTIVATE SFX ID\tSHOT SFX ID\tBURST SFX ID\tMASS\tSIGNATURE\tSUCCESSOR PART ID\tPART MASK\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Shields = new("Shields",
        "ID\tREGEN RATE\tMAX STRENGTH\tDEFENSE CONSTANT ID (COMMENT)\tACTIVATE SFX ID\tDEACTIVATE SFX ID\tMASS\tSIGNATURE\tSUCCESSOR PART ID\tPART MASK\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Cloaks = new("Cloaks",
        "ID\tENERGY CONSUMPTION\tMAX CLOAKING\tPOWER ON RATE\tPOWER OFF RATE\tENGAGE SFX ID\tDISENGAGE SFX ID\tMASS\tSIGNATURE\tSUCCESSOR PART ID\tPART MASK\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Afterburners = new("Afterburners",
        "ID\tFUEL CONSUMPTION\tMAX THRUST\tPOWER ON RATE\tPOWER OFF RATE\tINTERIOR SFX ID\tEXTERIOR SFX ID\tMASS\tSIGNATURE\tSUCCESSOR PART ID\tPART MASK\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Packs = new("Packs",
        "ID\tTYPE\tAMOUNT\tMASS\tSIGNATURE\tSUCCESSOR PART ID\tPART MASK\tINVENTORY MODEL\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec TypesOfShips = new("TypesOfShips",
        "ID\tMASS\tSIGNATURE\tSPEED\tMAX TURN RATES X Y Z\tTURN TORQUES Z Y Z\tTHRUST\tSIDE MULTIPLIER\tBACK MULTIPLIER\tSCANNER RANGE\tMAX FUEL\tECM\tLENGTH\tMAX ENERGY\tRECHARGE RATE\tRIP SPEED\tRIP COST\tMAX AMMO\tSUCCESSOR HULL ID\tMAX WEAPONS\tMAX FIXED WEAPONS\tHIT POINTS\tDEFENSE CONSTNAT ID\tMAGAZINE CAPACITY\tDISPENSER CAPACITY\tCHAFF LAUNCHER CAPACITY\tPREFERRED PART TYPES\tABILITIES\tTEXTURE NAME\tALLOWED CHAFF\tALLOWED MISSILES\tALLOWED DISPENSERS\tALLOWED SHIELDS\tALLOWED CLOAKS\tALLOWED BOOSTERS\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Hardpoints = new("Hardpoints",
        "SHIP TYPE ID\tINTERIOR SFX ID\tTURN SFX ID\tFRAME NAME\tLOCATION ABBREVIATION\tALLOWED WEAPONS\tFIXED\tCOPY OF NAME MDL");

    public static readonly TypeFileSpec Developments = new("Developments",
        "ID\tCOMPLETION SFX ID\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec TypesOfDrones = new("TypesOfDrones",
        "ID\tSHOOT SKILL\tMOVE SKILL\tBRAVERY\tPILOT TYPE\tHULL TYPE ID\tEXPENDABLE TYPE ID\tPRICE\tTIME TO BUILD\tMODEL NAME\tICON NAME\tNAME\tDESCRIPTION\tGROUP\tTECH REQUIRED\tTECH EFFECT");

    public static readonly TypeFileSpec Civilizations = new("Civilizations",
        "ID\tINCOME MODIFIER\tBONUS MODIFIER\tNAME\tICON\tHUD\tPOD HULL TYPE ID\tINITIAL STATION TYPE ID\tSTARTING TECH\tSTARTING TECH NODEV");

    public static readonly TypeFileSpec TypesOfTreasures = new("TypesOfTreasures",
        "ID\tNAME\tNUMTREASURES\tZONE ONLY\tTREASURES  OBJID TYPE CHANCEPCT");

    /// <summary>All files in native export order (each is written even when empty).</summary>
    public static readonly IReadOnlyList<TypeFileSpec> All = new[]
    {
        Constants, GlobalAttributes, ShipsSFX, StationsSFX, TypesOfStations,
        TypesOfProjectiles, ExpendableMissiles, ExpendableMines, ExpendableChaff,
        ExpendableProbes, Expendables, Weapons, Shields, Cloaks, Afterburners, Packs,
        TypesOfShips, Hardpoints, Developments, TypesOfDrones, Civilizations,
        TypesOfTreasures,
    };

    private static readonly Dictionary<string, TypeFileSpec> ByName =
        All.ToDictionary(f => f.BaseName, StringComparer.OrdinalIgnoreCase);

    public static TypeFileSpec Get(string baseName) => ByName[baseName];

    public static IEnumerable<string> Names => All.Select(f => f.BaseName);
}

/// <summary>
/// One static-core object type's bridge between raw record payloads and native's
/// tab-delimited text (<c>Obj2Txt</c> write side,
/// <c>ImportStaticIGCObjs</c> read side).
/// </summary>
public interface ITypeCodec
{
    IgcObjectType Type { get; }

    /// <summary>The native files this type writes and reads.</summary>
    IReadOnlyList<TypeFileSpec> Files { get; }

    /// <summary>Export: one payload → (file, data line) pairs in native column order.</summary>
    IReadOnlyList<(string File, string Line)> Write(ReadOnlySpan<byte> payload, CoreTable table, CoreCatalog catalog);

    /// <summary>Import: all files → one <see cref="TextRecord"/> per text row, in
    /// native row order, carrying which payload bytes the text determines.</summary>
    List<TextRecord> Read(TextCoreFiles text, CoreTable table);
}

/// <summary>Registry of the supported static-core type codecs.</summary>
public static class TypeCodecRegistry
{
    private static readonly Dictionary<IgcObjectType, ITypeCodec> Codecs = new();

    public static void Register(ITypeCodec codec) => Codecs[codec.Type] = codec;

    public static ITypeCodec? Get(IgcObjectType type)
        => Codecs.TryGetValue(type, out var c) ? c : null;

    /// <summary>The native text→binary order of <c>ImportStaticIGCObjs</c>
    /// (<c>missionigc.cpp:547-2313</c>) as it reaches
    /// <c>CmissionIGC::Export</c>'s container walk (projectiles, then the
    /// expendable list in insertion order, then the part list in insertion order,
    /// hulls, developments, drones, station types, treasure sets, civilizations).
    /// Used only for a text folder WITHOUT a <c>.coremeta</c> sidecar.</summary>
    public static readonly IgcObjectType[] NativeImportOrder =
    {
        IgcObjectType.Constants,
        IgcObjectType.ProjectileType,
        IgcObjectType.MissileType,
        IgcObjectType.ProbeType,
        IgcObjectType.ChaffType,
        IgcObjectType.MineType,
        IgcObjectType.PartType,
        IgcObjectType.HullType,
        IgcObjectType.Development,
        IgcObjectType.DroneType,
        IgcObjectType.StationType,
        IgcObjectType.TreasureSet,
        IgcObjectType.Civilization,
    };

    /// <summary>Registration order used for writing export files, matching
    /// <c>ExportStaticIGCObjs</c> (developments' gas rows precede civilizations').</summary>
    public static readonly IgcObjectType[] ExportOrder =
    {
        IgcObjectType.Constants,
        IgcObjectType.StationType,
        IgcObjectType.ProjectileType,
        IgcObjectType.MissileType,
        IgcObjectType.MineType,
        IgcObjectType.ChaffType,
        IgcObjectType.ProbeType,
        IgcObjectType.PartType,
        IgcObjectType.HullType,
        IgcObjectType.Development,
        IgcObjectType.DroneType,
        IgcObjectType.Civilization,
        IgcObjectType.TreasureSet,
    };

    /// <summary>Native object id of one record, used to match text rows to the
    /// records in <c>.coremeta</c> (ids are unique per type in a static core).</summary>
    public static int RecordId(IgcObjectType type, ReadOnlySpan<byte> payload) => type switch
    {
        IgcObjectType.Constants => 0,
        IgcObjectType.ProjectileType => Bin.I16(payload, 72),
        IgcObjectType.MissileType or IgcObjectType.MineType
            or IgcObjectType.ChaffType or IgcObjectType.ProbeType => Bin.I16(payload, 446),
        IgcObjectType.PartType => payload.Length == 24 ? Bin.I16(payload, 2) : Bin.I16(payload, 372),
        IgcObjectType.HullType => Bin.I16(payload, 446),
        IgcObjectType.StationType => Bin.I16(payload, 446),
        IgcObjectType.Development => Bin.I16(payload, 464),
        IgcObjectType.DroneType => Bin.I16(payload, 380),
        IgcObjectType.Civilization => Bin.I16(payload, 262),
        IgcObjectType.TreasureSet => Bin.I16(payload, 26),
        _ => 0,
    };
}

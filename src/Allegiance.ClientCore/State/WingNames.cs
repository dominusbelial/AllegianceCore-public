namespace Allegiance.ClientCore.State;

public static class WingNames
{
    // Igc/common.cpp:23-35, c_pszWingName[c_widMax].
    private static readonly string[] Names =
        ["command", "attack", "defend", "escort", "search", "alpha", "bravo", "charlie", "delta", "echo"];

    public static string For(short wingId) => wingId >= 0 && wingId < Names.Length ? Names[wingId] : "?";
}

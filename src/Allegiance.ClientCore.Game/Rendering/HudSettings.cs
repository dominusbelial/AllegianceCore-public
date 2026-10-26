using System.Text.Json;
using System.Text.Json.Serialization;

namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum HudStyle { Classic, Modern }

/// <summary>Presentation only. A missing HUD preference on an existing profile
/// migrates to Classic; a fresh profile starts Modern. Persist the decision at boot,
/// before the intro screen can create player.json.</summary>
public sealed class HudSettings
{
    public HudStyle Style { get; set; } = HudStyle.Modern;
    public float Size { get; set; } = 1;
    public bool ColorBlindSafe { get; set; }
    /// <summary>HUD-4 — native optional round radar border (WinTrek.cpp:2715
    /// defaults false, i.e. square radar).</summary>
    public bool RoundRadar { get; set; }
    /// <summary>HUD-10 — the full-viewport translucent-red damage wash is a port
    /// extension (native flashes only the hull/shield gauge art, dialog.mdl:2403-2506).
    /// Default on to preserve existing behaviour; set false for native-only flash.</summary>
    public bool DamageWash { get; set; } = true;
    /// <summary>HUD-14 — native classic gauges carry no percentage numeral; the
    /// port's % readout is an extension gated behind this HUD option.
    /// Default OFF = native.</summary>
    public bool GaugePercentage { get; set; }
    /// <summary>HUD-11 — native <c>ChatLines</c> preference
    /// (<c>LoadPreference("ChatLines", 7)</c>, WinTrek.cpp:3317, range 1-10
    /// SetChatLines WinTrek.cpp:5262-5280). This is the CONFIGURED chat-pane
    /// height (native <c>NumChatLines</c>), not the number of lines currently
    /// shown: the vote/critical-tip banner offsets read it.</summary>
    public int ChatLines { get; set; } = 7;
    /// <summary>HUD-19 — native ShowJoystickIndicator Graphics Option,
    /// default ON in the ctor (m_bShowJoystickIndicator = true,
    /// WinTrek.cpp:2739) and loaded with
    /// LoadPreference("ShowJoystickIndicator", 1) (WinTrek.cpp:3059).
    /// Gates <see cref="HudModel.JoystickIndicatorVisible"/>.</summary>
    public bool ShowJoystickIndicator { get; set; } = true;
    /// <summary>P805 (row 470) — native ShowCenterHUD Graphics option
    /// (WinTrek.cpp:2898; LoadPreference("CenterHUD", TRUE) :3300-3301 persisted
    /// by ToggleCenterHUD :5082-5086), default true.</summary>
    public bool ShowCenterHud { get; set; } = true;
    /// <summary>P805 (row 470) — native ShowTargetHUD Graphics option
    /// (WinTrek.cpp:2899; LoadPreference("TargetHUD", TRUE) :3302-3303 persisted
    /// by ToggleTargetHUD :5090-5094), default true.</summary>
    public bool ShowTargetHud { get; set; } = true;
    /// <summary>P805 (parity row 418) — the port's 3-D-view target-selection
    /// box (P345/P376 closed rectangle) is a SUPERSET native lacks: native marks
    /// the selected target only with the targetedbmp bracket on the RADAR
    /// contact (radarimage.cpp:307-319,352) and nothing in the 3-D view.
    /// </summary>
    /// <remarks>Null = the style's default (Domingo, 2026-10-24): off in Classic
    /// (native has no 3-D selection box), on in Modern (readability extension).
    /// An explicit true/false is the player's choice in either style.</remarks>
    public bool? SelectionBracket { get; set; }
    /// <summary>Whether the 3-D selection box draws: the player's choice, else the
    /// style default.</summary>
    public bool SelectionBracketOn => SelectionBracket ?? Style == HudStyle.Modern;
    /// <summary>P828 — the Modern-HUD radar label declutter (priority + nearest-N
    /// labels, distance fade, overlap merge) for crowded battles. A VISUAL port
    /// option: native labels every ship its radar mode allows
    /// (radarimage.cpp:747-756), which is unreadable at the 200v200 showcase's
    /// ~390 in-sector ships. It never changes WHAT the native modes allow, only
    /// how many of those labels draw and how.</summary>
    /// <remarks>Null = the style's default (Domingo, 2026-10-25): OFF in Classic
    /// (Classic stays exactly native), ON in Modern. An explicit true/false is
    /// the player's choice in either style, persisted like the other HUD options.</remarks>
    public bool? RadarDeclutter { get; set; }
    /// <summary>Whether the declutter runs: the player's choice, else the style
    /// default.</summary>
    public bool RadarDeclutterOn => RadarDeclutter ?? Style == HudStyle.Modern;
    /// <summary>P828 — how many ordinary (non-priority) labels per frame the
    /// declutter keeps ("the nearest N"; the priority contacts are always
    /// labelled). 0 hides every ordinary label.</summary>
    public int RadarLabelMax { get; set; } = RadarLabelDeclutter.DefaultExtraLabels;
    /// <summary>P828 — draw the per-frame contacts / labelled / merged telemetry
    /// line (off by default; a tuning aid, not gameplay information).</summary>
    public bool RadarLabelTelemetry { get; set; }
    /// <summary>P828 — the resolved declutter plan for one renderer at HUD scale
    /// <paramref name="scale"/>. Classic and Modern both call this so the policy
    /// lives in one place.</summary>
    public RadarLabelDeclutterOptions RadarLabelPlan(float scale)
        => RadarDeclutterOn
            ? RadarLabelDeclutterOptions.Modern(RadarLabelMax, scale, RadarLabelTelemetry)
            : RadarLabelDeclutterOptions.Off;
    public static string DefaultPath => Environment.GetEnvironmentVariable("ALLEGIANCE_HUD_SETTINGS")
        ?? Path.Combine(IntroPreferences.DirectoryPath, "hud.json");
    public void Normalize()
    {
        if (!Enum.IsDefined(Style)) Style = HudStyle.Classic;
        Size = float.IsFinite(Size) ? Math.Clamp(Size, .75f, 1.5f) : 1;
        ChatLines = Math.Clamp(ChatLines, 1, 10);
        RadarLabelMax = Math.Clamp(RadarLabelMax, 0, RadarLabelDeclutter.MaxExtraLabelsLimit);
    }
    public static HudSettings Load(string path, bool existingPlayer)
    {
        try
        {
            var settings = JsonSerializer.Deserialize(File.ReadAllText(path), HudSettingsJson.Default.HudSettings) ?? new();
            settings.Normalize();
            return settings;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        { return new() { Style = existingPlayer ? HudStyle.Classic : HudStyle.Modern }; }
    }
    public void Save(string path)
    {
        Normalize();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, HudSettingsJson.Default.HudSettings));
        File.Move(path + ".tmp", path, true);
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(HudSettings))]
internal partial class HudSettingsJson : JsonSerializerContext;

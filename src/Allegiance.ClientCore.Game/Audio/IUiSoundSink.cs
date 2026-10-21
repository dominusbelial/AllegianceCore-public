namespace Allegiance.ClientCore.MonoGame.Audio;

/// <summary>
/// P764 — the UI one-shot sound seam. The native client plays its UI cues by
/// calling <c>trekClient.PlaySoundEffect(...)</c> directly at each trigger site
/// (WinTrek/sectormap.cpp, teamscreen.cpp, teampane.cpp, WinTrek.cpp, load.cpp,
/// teleportpane.cpp). The port's triggers live in <c>Game1</c>, which is not
/// constructible under a test harness — so every UI cue this wave wires routes
/// through this interface (implemented by <see cref="SoundManager"/> in
/// production), and the headless tests inject a recording sink via
/// <c>Game1._uiSoundOverride</c> to pin the CALL at the trigger, not just the
/// bundled wave.
/// </summary>
public interface IUiSoundSink
{
    /// <summary>Native <c>mouseoverSound</c> — starmap hover onto a new cluster
    /// (sectormap.cpp:1442).</summary>
    void PlayMouseOver();

    /// <summary>Native <c>mouseclickSound</c> — a starmap cluster click (left:
    /// sectormap.cpp:1523; right: sectormap.cpp:1583-1587) and the team
    /// selection clicks (teamscreen.cpp:2209/2217/2438).</summary>
    void PlayMapClick();

    /// <summary>Native <c>errorSound</c> — the sector map's invalid
    /// teleport-target feedback (sectormap.cpp:1362/1367).</summary>
    void PlayError();

    /// <summary>Native <c>positiveButtonClickSound</c> — affirmative UI actions:
    /// help toggle (WinTrek.cpp:9269), team-pane money gift (teampane.cpp:1177),
    /// custom-loadout save (load.cpp:2235).</summary>
    void PlayPositiveClick();

    /// <summary>Native <c>mountSound</c> — the loadout restore cue
    /// (load.cpp:2247).</summary>
    void PlayMountPart();

    /// <summary>A raw wave-stem one-shot (the P757 <c>noTurretsSound</c> drain
    /// uses it with <c>vo_sal_noturrets</c>, teleportpane.cpp:285).</summary>
    void PlayVoice(string assetStem);
}

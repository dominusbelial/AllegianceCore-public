using System.Globalization;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

public sealed partial class Server
{
    private StaticMapRecord[] _staticMaps = [];

    // Native standalone catalog, FedSrv/FedSrv.CPP:211-231.
    private void LoadStaticMapCatalog()
    {
        string path = Path.Combine(_config.ArtPath, "maps.txt");
        if (!File.Exists(path)) { _staticMaps = []; return; }
        using var reader = File.OpenText(path);
        // FedSrv.CPP:218-228 never checks fscanf's return value and does not
        // abort startup on a malformed record. Undefined/uninitialized C++ data
        // has no safe managed equivalent: omit unreadable entries and continue.
        if (!int.TryParse(reader.ReadLine(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count)
            || count < 0 || count > (ushort.MaxValue - StaticMapInfo.FixedSize) / StaticMapInfo.RecordSize)
        {
            _staticMaps = [];
            return;
        }
        var maps = new List<StaticMapRecord>(count);
        for (int i = 0; i < count; i++)
        {
            string? line = reader.ReadLine();
            if (line is null) break;
            string[] fields = line.Split(',', 3);
            if (fields.Length != 3 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int teams)
                || fields[1].Length > 24 || fields[2].Length > 12
                || fields[2] != Path.GetFileName(fields[2]) || fields[2].Contains('\\'))
                continue;
            maps.Add(new(fields[2], fields[1], teams));
        }
        _staticMaps = maps.ToArray();
    }

    private string ResolveCatalogMap(GameConfig game)
    {
        // Provisioning accepts the same catalog selection (file and team count)
        // as the client's selector; existing explicit file configurations remain valid.
        var selected = _staticMaps.FirstOrDefault(m => m.Teams == game.Teams
            && (m.FileName == game.Map || m.FriendlyName == game.Map));
        return string.IsNullOrEmpty(selected.FileName) ? game.Map
            : selected.FileName.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? selected.FileName : selected.FileName + ".igc";
    }

    /// <summary>
    /// P966 (P963-03) — an EXACT catalog match for a player-CLAIMED map. Native
    /// checks a user map against the static-map catalog before hosting it:
    /// <c>VerifyUserMap</c> requires the team count AND the file name to match a
    /// catalog entry exactly (<c>FedSrv/FedSrv.CPP:234-245</c>,
    /// <c>nTeams == nNumTeams &amp;&amp; 0 == strcmp(szMapFile, cbIGCFile)</c>). The
    /// port's provisioning previously <c>Path.Combine</c>d the raw claim under
    /// <c>artPath</c>, so a <c>..</c> or rooted claim escaped the artwork root and
    /// hosted any readable file while skipping the catalog entirely.
    /// <para>The claim is therefore only ever used as a catalog KEY: the file that
    /// is actually opened comes from the operator's <c>maps.txt</c> (whose loader
    /// already rejects non-basename entries, above), matched by file name (with
    /// or without the <c>.igc</c> extension — the form the lobby's wizard list
    /// uses) or friendly name, with the team count. No match ⇒ refused; a
    /// matching entry is still re-checked as a relative basename by the caller
    /// before anything is opened.</para>
    /// </summary>
    private bool TryResolveClaimedCatalogMap(GameConfig game, out string catalogFile)
    {
        catalogFile = "";
        var selected = _staticMaps.FirstOrDefault(m => m.Teams == game.Teams
            && (m.FileName == game.Map
                || (m.FileName + ".igc").Equals(game.Map, StringComparison.Ordinal)
                || m.FriendlyName == game.Map));
        if (selected.FileName is not { Length: > 0 } file) return false;
        // The catalog's own basename rule (loader, above): a relative file name
        // only — no separators, no rooted form. The claim string never reaches
        // the file system, but a hostile maps.txt entry must not either.
        if (file != Path.GetFileName(file) || file.Contains('\\') || Path.IsPathRooted(file)) return false;
        catalogFile = file.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? file : file + ".igc";
        return true;
    }
}

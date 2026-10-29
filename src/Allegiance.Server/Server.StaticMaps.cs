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
}

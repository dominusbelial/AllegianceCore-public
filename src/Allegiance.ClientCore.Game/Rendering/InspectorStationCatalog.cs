using System;
using System.Collections.Generic;
using System.Linq;
using Allegiance.Core;

namespace Allegiance.ClientCore.MonoGame.Rendering;

/// <summary>
/// P615 — the STATION list the offline inspector walks in station mode
/// (<c>--inspect-stations</c>). Pure enumeration over the static core's
/// <c>DataStationTypeIGC</c> records (the static core has 143 station types),
/// mirroring <see cref="InspectorHullCatalog"/>'s GPU-free, headless-testable
/// shape so a station-mode inspection runs without a server.
/// </summary>
public static class InspectorStationCatalog
{
    /// <summary>A single reviewable station type in the inspector's walk order.</summary>
    public sealed record InspectorStation(
        short StationTypeId,
        string Name,
        string ModelName,
        string TextureName,
        float Radius,
        byte GroupId,
        bool NativeAvailable)
    {
        /// <summary>Short, stable label for screenshots + notes (sanitised of slashes).</summary>
        public string Slug => $"{StationTypeId}_{Sanitise(Name)}";
        private static string Sanitise(string s)
        {
            var c = new char[s.Length];
            for (int i = 0; i < s.Length; i++) c[i] = char.IsLetterOrDigit(s[i]) ? s[i] : '_';
            return new string(c);
        }
    }

    /// <summary>
    /// Build the ordered walk list (ordered by <see cref="InspectorStation.StationTypeId"/>,
    /// stable across runs). <paramref name="nativeAvailable"/> tests whether a station's
    /// native <c>.mdl</c> mesh decodes, so <see cref="InspectorStation.NativeAvailable"/>
    /// is honest. The static core's station table is authoritative (the same
    /// <c>PCore001.igc</c> the server boots).
    /// </summary>
    public static List<InspectorStation> Enumerate(
        IEnumerable<DataStationTypeIGC> stations,
        Func<DataStationTypeIGC, bool> nativeAvailable)
    {
        var result = new List<InspectorStation>();
        foreach (var s in stations.OrderBy(s => s.StationTypeId))
        {
            if (result.Any(e => e.StationTypeId == s.StationTypeId)) continue;   // dedupe by id
            result.Add(new InspectorStation(
                s.StationTypeId, s.Name, s.ModelName, s.TextureName, s.Radius,
                s.GroupId, nativeAvailable(s)));
        }
        return result;
    }

    /// <summary>The stations in a group (GroupId filter), in walk order. Station types
    /// group loosely by role (base/outpost/refinery…); null group = all.</summary>
    public static List<InspectorStation> ForGroup(List<InspectorStation> all, byte? groupId)
        => groupId is null ? all : all.Where(e => e.GroupId == groupId.Value).ToList();

    /// <summary>Every distinct GroupId present in the list, ascending (the filter options).</summary>
    public static List<byte> Groups(List<InspectorStation> all)
        => all.Select(e => e.GroupId).Distinct().OrderBy(g => g).ToList();
}

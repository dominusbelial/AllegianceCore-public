using System.Collections.Generic;
using Allegiance.Core.Model;
using Allegiance.Core.Transport;

namespace Allegiance.Server;

/// <summary>
/// P612 — admin-unrestricted spectator visibility. For an ADMIN player (the P602
/// <c>adminCheats</c> allowNames identity), the server streams ALL ships in the
/// sector the free camera is viewing (ignoring side visibility), and lets the
/// free camera view ANY sector. Non-admins keep native behaviour (they only ever
/// receive the ships of their own sector, with the P270 cloak gate intact) — so
/// there is no information leak for a normal player.
/// <para>
/// Mechanism: the client's free camera names the sector it is viewing via the
/// existing <c>FM_C_VIEW_CLUSTER(104)</c> request (see
/// <c>Server.Dispatch.cs</c>); an admin's request is recorded in
/// <see cref="_spectateSector"/> and the per-peer ship broadcast
/// (<c>BroadcastShipsUpdatePeer</c>) serves that sector's candidates and relaxes
/// the cloak predicate for that peer. A non-admin's request is a no-op (native's
/// flying-ship gate). LAN/demo-grade (documented in GUIDE/SECURITY).
/// </summary>
public sealed partial class Server
{
    /// <summary>Per-peer override sector for an admin spectator's free camera
    /// (ConnectionId → sector id). Present only while that admin is spectating a
    /// sector other than their own; absence/removal = normal own-sector view.</summary>
    private readonly Dictionary<ConnectionId, short> _spectateSector = new();

    /// <summary>
    /// P612 — pure, headless-testable admin-spectator predicate: is an allow-listed
    /// name a spectator admin (the passive viewing privilege)? Requires the
    /// <c>adminCheats</c> section enabled and the character name to match an
    /// <see cref="AdminCheatsConfig.AllowNames"/> entry (case-insensitive, with
    /// <c>*</c>-prefix wildcards via <see cref="AdminNameMatches"/>).
    /// </summary>
    internal static bool AdminSpectatorAllowed(AdminCheatsConfig? cfg, string? name)
    {
        if (cfg is not { Enabled: true }) return false;
        if (string.IsNullOrEmpty(name)) return false;
        foreach (var allowed in cfg.AllowNames)
            if (AdminNameMatches(allowed, name))
                return true;
        return false;
    }

    /// <summary>
    /// P655: spectator eligibility also requires the configured bearer, or a
    /// verified loopback socket when no secret is configured. Name matching
    /// alone never grants visibility to a remote connection.
    /// </summary>
    private bool IsSpectatorAdmin(ConnectionId from)
        => AdminSpectatorAllowed(_config.LiveSettings?.AdminCheats,
            _peerNames.TryGetValue(from, out var n) ? n : null)
            && PrivilegedConnection(from, _config.LiveSettings?.AdminCheats?.Secret, _adminAuthenticated);

    /// <summary>Record the sector an admin spectator's free camera is viewing
    /// (NA=-1 clears the override → back to own sector).</summary>
    private void RecordSpectateSector(ConnectionId from, short sectorId)
    {
        if (sectorId == -1)
        {
            _spectateSector.Remove(from);
            return;
        }
        // Only a real sector of the current mission is accepted (never an
        // arbitrary id) — defensively gate so a spoofed request can't target
        // a non-existent cluster.
        if (!_peerShips.TryGetValue(from, out var live) || SectorById(live.Ship.Mission, sectorId) is null) return;
        _spectateSector[from] = sectorId;
    }

    /// <summary>Look up a sector only in the requester's mission.</summary>
    private static Sector? SectorById(Mission? mission, short id)
        => mission?.Sectors.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// The sector whose ships this peer's broadcast should serve: the admin
    /// spectator's viewed sector (when set and valid), else the peer's own
    /// sector. Pure decision used by <c>BroadcastShipsUpdatePeer</c>.
    /// </summary>
    private Sector BroadcastSectorFor(ConnectionId from, Sector ownSector)
    {
        if (_spectateSector.TryGetValue(from, out var sid) && _peerShips.TryGetValue(from, out var live) && SectorById(live.Ship.Mission, sid) is { } spec)
            return spec;
        return ownSector;
    }
}

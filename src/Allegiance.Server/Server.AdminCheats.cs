using System.Globalization;
using System.Security.Cryptography;
using Allegiance.Core.Collision;
using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>
/// P554 — the privileged admin-cheat hook: the port of native's
/// <c>DoCheatCode</c> (<c>FedSrv.CPP:2814</c>), called from the chat handler's
/// <c>ChatMessage</c> branch (see <c>Server.Dispatch.cs</c>) exactly where native
/// calls it (<c>FedSrv.CPP:5023</c>, debug builds only): before target selection
/// and relay, on every inbound chat line. A <c>true</c> return means the line was
/// a cheat and is CONSUMED — never forwarded as chat (<c>bForward = false</c>,
/// <c>FedSrv.CPP:5027</c>); a <c>false</c> return means the line is ordinary chat
/// and falls through to the normal relay (native: <c>fIsCheat = false</c>).
/// <para>Privilege mirrors native <c>CanCheat</c>/<c>UTL::PrivilegedUser</c>
/// (<c>FedSrv.CPP:664</c>) via <c>liveSettings.adminCheats</c> and is OFF unless
/// explicitly configured (<c>ServerConfig.AdminCheatsConfig</c>).</para>
/// <para>Only the <c>relocate</c> token is ported (the arena-relocation need);
/// native's other tokens (<c>stations</c>, <c>gimme</c>, <c>fourteen</c>,
/// <c>fifteen</c>, <c>ragnarok</c>, <c>capture</c>, <c>createdrones</c>,
/// <c>jpd</c>, <c>fireaway</c>, <c>winwinwin</c>, <c>checkbank</c>,
/// <c>IfYouWishUponAStar</c>, ... — <c>FedSrv.CPP:2822-3022</c>) are listed as
/// follow-ups, not ported.</para>
/// </summary>
public sealed partial class Server
{
    /// <summary>
    /// P554 — is this connection privileged to issue admin cheats? The port of
    /// native's <c>!CanCheat() &amp;&amp; !UTL::PrivilegedUser(name)</c> join gate
    /// (<c>FedSrv.CPP:664</c>): privilege = <c>liveSettings.adminCheats.Enabled</c>
    /// AND server-verifiable privilege provenance (P655), plus a character name
    /// in <c>AllowNames</c> (case-insensitive,
    /// mirroring native's upper-cased allow-list comparison,
    /// <c>_Utility/Utility.cpp:60-67</c>). Disabled unless configured, so a
    /// default server never interprets any chat line as a cheat.
    /// </summary>
    /// <summary>An <c>AllowNames</c> entry matches a character name case-insensitively
    /// (native upper-cases both sides, <c>Utility.cpp:60-67</c>). An entry ending in
    /// <c>*</c> is a PREFIX match (e.g. <c>"mcp-*"</c>) — needed because the MCP
    /// harness names its bots <c>mcp-bNNN-RRRR</c> with a random suffix, so exact
    /// names can't be listed in advance. Native's <c>PrivilegedUser</c> likewise
    /// matches name patterns (the <c>@HQ</c>/<c>@Dev</c>/… suffixes, :46-82).</summary>
    internal static bool AdminNameMatches(string allowed, string name)
    {
        if (string.IsNullOrEmpty(allowed) || string.IsNullOrEmpty(name)) return false;
        if (allowed.Length > 1 && allowed[^1] == '*')
            return name.StartsWith(allowed[..^1], StringComparison.OrdinalIgnoreCase);
        return allowed.Equals(name, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// P602 — when a shared secret is configured, a privileged connection is
    /// trusted only if its cheat line presents that secret as the leading
    /// whitespace-delimited token (<c>"&lt;secret&gt; relocate …"</c>). Returns
    /// true and yields the remainder (the actual cheat line) when the first
    /// token equals <paramref name="secret"/> (constant-time comparison); false
    /// when the secret is absent/unset or the first token doesn't match —
    /// the line is then ordinary chat, never a cheat. <c>remainder</c> is the
    /// stripped rest of the line ("" when the line was just the secret).
    /// </summary>
    internal static bool ConsumeSecretPrefix(string? secret, string message, out string remainder)
    {
        remainder = "";
        if (string.IsNullOrEmpty(secret) || string.IsNullOrEmpty(message)) return false;
        string trimmed = message.TrimStart();
        int sp = IndexWhitespace(trimmed);
        string first = sp < 0 ? trimmed : trimmed[..sp];
        if (!FixedTimeEquals(secret!, first)) return false;
        remainder = sp < 0 ? "" : trimmed[(sp + 1)..].Trim();
        return true;
    }

    /// <summary>Constant-time secret comparison (length + bytes), so the
    /// presence/absence of a single correct prefix character doesn't leak via
    /// timing on a shared network.</summary>
    private static bool FixedTimeEquals(string a, string b)
    {
        byte[] ab = System.Text.Encoding.UTF8.GetBytes(a);
        byte[] bb = System.Text.Encoding.UTF8.GetBytes(b);
        return CryptographicOperations.FixedTimeEquals(ab, bb);
    }

    /// <summary>
    /// P602 — the boot banner for the admin-cheat hook. Returns null when admin
    /// cheats are not enabled (nothing printed, matching P554's silent default).
    /// When enabled, returns the active banner; when additionally bound to a
    /// NON-loopback address (this server always binds 0.0.0.0/all interfaces),
    /// appends a deployment warning; credentials use the plaintext game transport.
    /// </summary>
    internal static string? AdminCheatBootWarning(AdminCheatsConfig? cfg, bool boundToLoopbackOnly)
    {
        if (cfg is not { Enabled: true }) return null;
        string banner = "[boot] *** ADMIN CHEATS ACTIVE (liveSettings.adminCheats.enabled=true) *** "
            + $"allowNames=[{string.Join(", ", cfg.AllowNames)}] — those names may issue the 'relocate' cheat on their OWN ship only; "
            + "port extension (native has no relocate; FedSrv.CPP:2814 DoCheatCode); consumed, never broadcast as chat";
        if (boundToLoopbackOnly)
            return banner + " ***";
        string secretNote = string.IsNullOrEmpty(cfg.Secret)
            ? "NO shared secret set: privileges restricted to verified loopback sockets."
            : "A shared secret is set — authenticate at logon or with a secret-prefixed command.";
        return banner + "; *** WARNING: THE SERVER LISTENS ON A NON-LOOPBACK ADDRESS WITH ADMIN CHEATS ENABLED — "
            + "NEVER ENABLE ADMIN CHEATS ON A PUBLIC/SHARED SERVER (NAMES ARE ONLY FILTERS; PRIVILEGES REQUIRE SERVER-VERIFIED PROVENANCE, P655). "
            + secretNote + " ***";
    }

    private bool CanUseAdminCheats(ConnectionId from, Ship ship)
    {
        var cfg = _config.LiveSettings?.AdminCheats;
        if (cfg is not { Enabled: true } || !PrivilegedConnection(from, cfg.Secret, _adminAuthenticated)) return false;
        if (!_peerNames.TryGetValue(from, out var name) || string.IsNullOrEmpty(name)) return false;
        foreach (var allowed in cfg.AllowNames)
            if (AdminNameMatches(allowed, name))
                return true;
        return false;
    }

    /// <summary>
    /// P554 — the <c>DoCheatCode</c> equivalent. Returns <c>true</c> when the line
    /// was a recognized cheat and is CONSUMED (never forwarded as chat — native
    /// <c>bForward=false</c>, <c>FedSrv.CPP:5027</c>); <c>false</c> = ordinary chat.
    /// A non-privileged sender's line is never even considered a cheat (falls
    /// through to normal relay — the "refused and broadcast as normal chat"
    /// behaviour); with the option disabled this is true even for an allow-listed
    /// name.
    /// </summary>
    private bool TryHandleAdminCheat(ConnectionId from, Ship ship, ChatMessage chat)
    {
        if (chat.Message is not { Length: > 0 } msg) return false;
        var authCfg = _config.LiveSettings?.AdminCheats;
        // Authenticate a secret-bearing command before enabling passive viewing.
        // Only enabled, allow-listed peers may use this bootstrap.
        if (AdminSpectatorAllowed(authCfg, _peerNames.GetValueOrDefault(from))
            && ConsumeSecretPrefix(authCfg?.Secret, msg, out _)) _adminAuthenticated.Add(from);
        if (!CanUseAdminCheats(from, ship)) return false;

        // Strip a presented bearer. Authenticated logon sessions may also send
        // commands without repeating it. Secret-bearing unknown commands are
        // consumed below so credentials cannot fall through to chat relay.
        var cfg = _config.LiveSettings?.AdminCheats;
        bool presentedSecret = ConsumeSecretPrefix(cfg?.Secret, msg, out var command);
        if (presentedSecret) msg = command;
        // LOGONREQ bearer authentication also authorizes subsequent commands.
        // The secret need not be repeated in every MCP chat line.

        if (msg.Length == 0) return presentedSecret;

        // Token = first whitespace-delimited word (native ZString::GetToken,
        // FedSrv.CPP:2819), matched case-insensitively like native's lstrcmpi.
        string trimmed = msg.TrimStart();
        int sp = IndexWhitespace(trimmed);
        string token = sp < 0 ? trimmed : trimmed[..sp];
        string rest = sp < 0 ? "" : trimmed[(sp + 1)..].Trim();

        if (token.Equals("relocate", StringComparison.OrdinalIgnoreCase))
        {
            HandleRelocateCheat(from, ship, rest);
            return true;                                    // consumed, never broadcast as chat
        }

        // P612 — sim.timeScale (admin-only cinematic slow motion), clamped to
        // [0.1, 1.0]. Consumed like every admin cheat.
        if (token.Equals("timescale", StringComparison.OrdinalIgnoreCase))
        {
            HandleTimescaleCheat(from, ship, rest);
            return true;
        }

        // Privileged but an unrecognized/out-of-scope token → not a cheat
        // (native fIsCheat=false) → forwarded as ordinary chat.
        return presentedSecret;
    }

    private static int IndexWhitespace(string s)
    {
        for (int i = 0; i < s.Length; i++)
            if (s[i] is ' ' or '\t' or '\r' or '\n') return i;
        return -1;
    }

    /// <summary>
    /// P554 — the <c>relocate</c> token. PORT EXTENSION: native has no teleport/
    /// relocate cheat at all (grep of <c>DoCheatCode</c>, <c>FedSrv.CPP:2822-
    /// 3022</c>, finds no move/teleport token). Syntax:
    /// <c>relocate &lt;shipId&gt; &lt;sectorId&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; [fx fy fz]</c>.
    /// Moves <c>&lt;shipId&gt;</c> to sector <c>&lt;sectorId&gt;</c> at
    /// <c>(x,y,z)</c> (facing <c>(fx,fy,fz)</c> if given, else the ship's current
    /// forward) via <see cref="RelocateShipToSector"/> — the SAME server path a
    /// warp transit uses. <b>Ownership:</b> a privileged player may relocate a
    /// ship they control or launched — their own ship, or an MCP-harness
    /// (<c>mcp-</c>-prefixed) bot (see <see cref="MayRelocate"/>). The line is
    /// always consumed when it starts with <c>relocate</c> (never relayed as
    /// chat); a malformed or refused relocation is answered with an individual
    /// no-filter chat reply to the sender (native cheats reply via
    /// <c>SendChatf(CHAT_INDIVIDUAL_NOFILTER)</c>, e.g. <c>FedSrv.CPP:2834,2909</c>).
    /// </summary>
    /// <summary>P554 — the harness character-name prefix that marks a connection
    /// as one of the MCP's own bots (the same prefix testHarness uses, default
    /// <c>"mcp-"</c>). Used by the relocate ownership rule to recognize "the
    /// MCP's own bots" (the population a privileged MCP operator launched).</summary>
    private string HarnessPrefix =>
        _config.LiveSettings?.TestHarness?.CharacterNamePrefix is { Length: > 0 } p ? p : "mcp-";

    /// <summary>
    /// A privileged sender can relocate their own ship or an authenticated
    /// test-harness connection. A matching prefix alone is insufficient (P655).
    /// </summary>
    private bool MayRelocate(Ship senderShip, Ship targetShip, string? targetName)
    {
        if (ReferenceEquals(senderShip, targetShip)) return true;          // own ship
        var targetConn = ConnOf(targetShip);
        return targetConn is { } conn && HarnessGrantFor(conn) is not null;  // the MCP's own bots
    }

    private void HandleRelocateCheat(ConnectionId from, Ship ship, string rest)
    {
        var parts = rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5 && parts.Length != 8)
        {
            SendAdminReply(from, ship.Id, "relocate: need <shipId> <sectorId> <x> <y> <z> [fx fy fz]");
            return;
        }
        if (!short.TryParse(parts[0], out short targetShipId)
            || !short.TryParse(parts[1], out short sectorId)
            || !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float x)
            || !float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float y)
            || !float.TryParse(parts[4], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
        {
            SendAdminReply(from, ship.Id, "relocate: bad numbers — expected <shipId:int> <sectorId:int> <x> <y> <z> [fx fy fz]");
            return;
        }

        // Resolve the TARGET ship (the MCP drives bots that are separate wire
        // peers; the probe issues the cheat naming the bot's shipId).
        Ship? target = null; string? targetName = null;
        foreach (var kv in _peerShips)
        {
            if (kv.Value.Ship.Id == targetShipId)
            {
                target = kv.Value.Ship;
                if (_peerNames.TryGetValue(kv.Key, out var nm)) targetName = nm;
                break;
            }
        }
        if (target is null)
        {
            SendAdminReply(from, ship.Id, $"relocate: no live peer ship {targetShipId}");
            return;
        }

        // Ownership: the privileged player controls or launched the target (own
        // ship, or an MCP-harness bot). Consumed either way — never relayed as chat.
        if (!MayRelocate(ship, target, targetName))
        {
            SendAdminReply(from, ship.Id, $"relocate: ship {targetShipId} is not a ship you control or launched (not your own, and not an MCP-harness bot) — refused");
            return;
        }

        var dest = target.Mission?.Sectors.FirstOrDefault(s => s.Id == sectorId);
        if (dest is null)
        {
            SendAdminReply(from, ship.Id, $"relocate: no sector {sectorId} in this mission");
            return;
        }
        if (target.Sector is not { } src)
        {
            SendAdminReply(from, ship.Id, "relocate: ship is not in a sector (docked/unplaced) — cannot relocate");
            return;
        }

        Vector3 pos = new(x, y, z);
        Vector3 fwd = target.Orientation.GetForward();
        if (parts.Length == 8)
        {
            if (!float.TryParse(parts[5], NumberStyles.Float, CultureInfo.InvariantCulture, out float fx)
                || !float.TryParse(parts[6], NumberStyles.Float, CultureInfo.InvariantCulture, out float fy)
                || !float.TryParse(parts[7], NumberStyles.Float, CultureInfo.InvariantCulture, out float fz)) return;
            fwd = new Vector3(fx, fy, fz);
        }

        if (RelocateShipToSector(target, dest, pos, fwd))
            SendAdminReply(from, ship.Id, $"relocate: ship {target.Id} -> sector {sectorId} ({dest.Name}) at ({x:F1},{y:F1},{z:F1})");
        else
            SendAdminReply(from, ship.Id, $"relocate: could not move ship {target.Id} to sector {sectorId}");
    }

    /// <summary>
    /// P554 — relocate a ship to a destination sector at a given pose using the
    /// SAME server path a real warp transit uses: physically move the ship
    /// (source-sector remove / dest add / <c>Sector</c> / <c>Position</c> /
    /// <c>Orientation</c>, the native <c>SetCluster</c> relocation) and zero its
    /// velocity, then raise the site's <c>ShipWarped</c> hook, which the Server
    /// has wired to its per-peer <c>SET_CLUSTER</c> broadcaster
    /// (<c>Server.Lifecycle.cs:679 → Server.OnShipWarped</c>). That is exactly
    /// the wire notification a real warp transit sends the owner's client, so
    /// the bot's ClientWorld learns the new sector and pose (a bare server move
    /// that skips this would leave the client's world stale in its home sector).
    /// <para>This is the extraction of the in-process arena relocator
    /// (<c>WireArenaRelocator</c>, <c>P539dFairContactLiveTests.cs</c>) so the
    /// P539d test and the network cheat share ONE implementation.</para>
    /// </summary>
    public bool RelocateShipToSector(Ship ship, Sector dest, Vector3 pos, Vector3 facing)
    {
        // Validate before mutating sector membership or any component of pose.
        if (!FiniteVector(pos) || !FiniteVector(facing)
            || !float.IsFinite(facing.LengthSquared()) || facing.LengthSquared() <= 0f
            || ship.Mission is null || !ship.Mission.Sectors.Contains(dest)) return false;
        if (ship.Sector is not { } src) return false;
        if (!ReferenceEquals(src, dest))
        {
            src.Remove(ship);
            dest.Add(ship);
            ship.Sector = dest;
        }
        ship.Position = pos;
        ship.Orientation.Set(facing, new Vector3(0f, 1f, 0f));
        ship.Velocity = Vector3.Zero;                       // native warp transit zeroes velocity on exit
        _host.ShipWarped?.Invoke(ship, src, dest);          // SET_CLUSTER to the owner's peer (Server.OnShipWarped)
        return true;
    }

    /// <summary>Private chat reply to one connection — the port of native
    /// <c>SendChatf(NULL, CHAT_INDIVIDUAL_NOFILTER, shipID, NA, text)</c> (the
    /// exact shape <c>SendBallotReply</c> uses; native cheats reply the same way,
    /// FedSrv.CPP:2834).</summary>
    private static bool FiniteVector(Vector3 v) => float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);

    private void SendAdminReply(ConnectionId to, short shipId, string text)
    {
        var msg = new ChatMessage
        {
            Message = text,
            SidSender = shipId,
            ChatTarget = (int)ChatTarget.IndividualNoFilter,
            OidRecipient = shipId,
        };
        SendPeer(to, FedPacket.Pack(new[] { msg.Encode() }));
    }
}

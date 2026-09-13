namespace Allegiance.Core.Wire;

/// <summary>
/// <para>
/// The permanent, code-visible registry of native FEDMSGs this port has
/// <b>confirmed, and deliberately decided not to build</b> — as distinct
/// from the much larger "not yet ported" set (tracked in the wiki
/// roadmap, still real work with a real path forward). Everything listed
/// here was investigated against the live native source
/// (<c>~/Development/Allegiance/src</c>) and found to be either dead
/// weight in native itself, or structurally inapplicable to this port's
/// own architecture. Re-verified in full on 2026-09-13 (a prior pass this
/// session briefly mis-classified <see cref="Fmid.AsteroidDrained"/> as
/// live before re-checking its actual call site — every entry below was
/// re-checked against the live source, not carried over from memory).
/// </para>
/// <para>
/// This exists so a future contributor grepping for a message id here
/// gets a real, cited answer instead of silence or a stale guess. See
/// <c>allegiance-fedmsg-roadmap.md</c> in the Domingo LLM Wiki for the
/// full running tally and the "genuinely blocked, real path exists"
/// bucket this registry deliberately excludes.
/// </para>
/// </summary>
public static class NotSupportedFedMsgs
{
    /// <summary>The native fmid numbers this class documents, exactly as
    /// declared in <c>Inc/Messages.h</c>.</summary>
    public enum Fmid
    {
        /// <summary>27.</summary>
        Poster = 27,
        /// <summary>36.</summary>
        UrlRoot = 36,
        /// <summary>117.</summary>
        Mute = 117,
        /// <summary>122.</summary>
        SetTeamInvestor = 122,
        /// <summary>131.</summary>
        Threat = 131,
        /// <summary>180.</summary>
        AsteroidDrained = 180,
        /// <summary>193.</summary>
        Bandwidth = 193,
        /// <summary>15.</summary>
        SetTeamInfo = 15,
        /// <summary>107.</summary>
        IcqChatAck = 107,
        /// <summary>11.</summary>
        StaticMapInfo = 11,
        /// <summary>183.</summary>
        ShipReset = 183,
    }

    /// <summary>Why a message is unsupported: dead weight in native
    /// itself (nobody ever sends or fully wires it up, so there is
    /// nothing to be faithful <em>to</em>), or a real native feature that
    /// is structurally inapplicable to this port's own architecture.
    /// </summary>
    public enum Reason
    {
        /// <summary>Native itself never sends this, or the handler that
        /// would act on it is disabled/commented out — porting it would
        /// mean inventing behavior native doesn't actually have.</summary>
        DeadInNative,
        /// <summary>Native uses this for real, but it depends on a
        /// concept (a lobby, squads, client-claimed positions, an
        /// account/SQL layer, ...) that this port's own architecture
        /// deliberately does not have.</summary>
        NotApplicableToThisPort,
    }

    /// <summary>One registry entry: why, and the real citation trail.
    /// </summary>
    public readonly record struct Entry(Reason Reason, string Explanation);

    /// <summary>
    /// The full registry. Every entry's <see cref="Entry.Explanation"/>
    /// names the real native file/line this was verified against —
    /// re-check the live source before trusting an old citation if this
    /// file hasn't been touched in a while.
    /// </summary>
    public static readonly IReadOnlyDictionary<Fmid, Entry> All = new Dictionary<Fmid, Entry>
    {
        [Fmid.Poster] = new(Reason.DeadInNative,
            "A real client receive-handler exists (clintlib/appmsg.cpp:112), " +
            "but zero senders exist anywhere in the native tree (server or " +
            "client) — nothing ever constructs or sends POSTER(27)."),

        [Fmid.UrlRoot] = new(Reason.DeadInNative,
            "Same shape as POSTER: a real client receive-handler exists " +
            "(clintlib/appmsg.cpp:75), zero senders anywhere."),

        [Fmid.Mute] = new(Reason.DeadInNative,
            "Zero references anywhere in the native tree outside the bare " +
            "message declaration in Messages.h/AllMessages.h — no sender, " +
            "no handler, no client-side consumer of any kind."),

        [Fmid.SetTeamInvestor] = new(Reason.DeadInNative,
            "Zero references anywhere in the native tree outside the bare " +
            "message declaration — same shape as MUTE(117)."),

        [Fmid.Threat] = new(Reason.DeadInNative,
            "Zero references anywhere in the native tree outside the bare " +
            "message declaration — same shape as MUTE(117)/SET_TEAM_INVESTOR(122)."),

        [Fmid.AsteroidDrained] = new(Reason.DeadInNative,
            "The sole call site (IasteroidIGC::AdjustOre) is wrapped in a " +
            "literal /* NYI -- re-enable if He3 does not regenerate */ " +
            "comment block (Igc/asteroidigc.h:225-230) — disabled because " +
            "native asteroids regenerate ore, the same design this port's " +
            "own Asteroid.Regenerate already follows, so the drain-to-zero " +
            "condition this message would announce never permanently fires."),

        [Fmid.Bandwidth] = new(Reason.DeadInNative,
            "The server-side FM_C_BANDWIDTH case is wrapped in a literal " +
            "/* pkk May 6th: Disabled bandwidth patch ... */ comment block " +
            "(FedSrv/FedSrv.CPP:7730-7738) — the server never handles it. " +
            "Client-side send code still exists (WinTrek/trekigc.cpp:3524, " +
            "WinTrek/WinTrek.cpp:6200) but sends into a void with no real " +
            "effect."),

        [Fmid.SetTeamInfo] = new(Reason.NotApplicableToThisPort,
            "Entirely squad-gated in native (GetIsMemberOfSquad/bSquadGame/" +
            "GetCanLeadSquad, FedSrv/FedSrv.CPP:7430-7454) — this port has " +
            "no squad concept anywhere."),

        [Fmid.IcqChatAck] = new(Reason.NotApplicableToThisPort,
            "A 2000s-era ICQ-instant-messenger character-link feature, SQL " +
            "-backed. The entire handler is wrapped in " +
            "#if !defined(ALLSRV_STANDALONE) in native itself " +
            "(FedSrv/FedSrv.CPP:5255-5282) — compiled OUT of the exact " +
            "standalone/no-lobby/no-account-layer build mode this port " +
            "already deliberately mirrors. See docs/LOBBY_SOCIAL_NA_FINDINGS_P129.md."),

        [Fmid.StaticMapInfo] = new(Reason.NotApplicableToThisPort,
            "Native's per-server browsable map/mission-template catalog for " +
            "a lobby's game-creation UI (FedSrv/FedSrv.CPP:189-231, boot-time " +
            "load). This port has no lobby, no mission browser, no map-" +
            "selection UI anywhere — it boots exactly one mission per " +
            "process from config. Re-confirmed 2026-09-13 after a same" +
            "-session slip briefly proposed reviving this as \"tractable\" " +
            "without checking the existing finding first — see " +
            "docs/LOBBY_SOCIAL_NA_FINDINGS_P129.md (original, 2026-09-10)."),

        [Fmid.ShipReset] = new(Reason.NotApplicableToThisPort,
            "Native sends this only when it REJECTS a client-claimed ship " +
            "position (an anti-cheat plausibility check inside " +
            "CFSPlayer::SetShipUpdate, fsship.cpp:1192-1200, on top of a " +
            "per-update cookie check, FedSrv/FedSrv.CPP:4851-4865). This " +
            "port's own SHIP_UPDATE dispatch (Server.cs, the ShipUpdate.Fmid " +
            "branch) was built fully server-authoritative from the earliest " +
            "movement work: the client payload carries only control inputs " +
            "(yaw/pitch/roll/throttle/state), never a claimed position — the " +
            "server's own physics tick is the sole authority, and the code's " +
            "own comment says so outright (\"the payload's position/velocity/" +
            "orientation echoes are ignored as authority\"). The desync this " +
            "message exists to correct cannot occur here: the client never " +
            "gets to claim a position in the first place."),
    };
}

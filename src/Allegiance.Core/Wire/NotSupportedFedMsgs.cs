namespace Allegiance.Core.Wire;

/// <summary>
/// Registry of historical native-message exclusions and their current status.
/// P868 distinguishes dead native declarations from live behavior still awaiting
/// implementation or a product decision. A different port architecture is not
/// proof that a native behavior is inapplicable. The complete classification is
/// recorded in docs/parity/p868-na-audit.csv.
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
        /// <summary>113.</summary>
        MissionParams = 113,
    }

    /// <summary>Current status of a historical exclusion. Live behavior
    /// remains pending unless an explicit product decision resolves it.</summary>
    public enum Reason
    {
        /// <summary>Native itself never sends this, or the handler that
        /// would act on it is disabled/commented out — porting it would
        /// mean inventing behavior native doesn't actually have.</summary>
        DeadInNative,
        /// <summary>Legacy value retained for compatibility. A different
        /// architecture alone is not an exclusion under the P868 ruling.</summary>
        NotApplicableToThisPort,
        /// <summary>Legacy value retained for compatibility. Recheck the
        /// actual native declarations before making this claim.</summary>
        GenuinelyUnrecoverable,
        /// <summary>Live native behavior that remains wholly or partly unported.
        /// This is an implementation gap, never an n/a classification.</summary>
        PendingParity,
        /// <summary>Live external-platform integration awaiting an explicit
        /// product decision; remains partial in the parity matrix.</summary>
        RequiresProductDecision,
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

        [Fmid.SetTeamInfo] = new(Reason.PendingParity,
            "Historical exclusion superseded by P853: Server.MissionParams handles " +
            "leader-authorized squad/name changes as well as the S-to-C relay " +
            "(FedSrv/FedSrv.CPP:7430-7467; FedSrv/fsmission.cpp:1564-1600). " +
            "The client squad-selection/invitation surface needs separate certification; " +
            "this entry must not be used to claim that the inbound handler is absent " +
            "or that a different provisioning architecture makes the behavior n/a."),

        [Fmid.IcqChatAck] = new(Reason.RequiresProductDecision,
            "Live external ICQ character-link integration (FedSrv/FedSrv.CPP:5255-5282; " +
            "WinTrek/WinTrek.cpp:11053-11060). Its standalone build guard is not proof " +
            "that this port's online identity architecture cannot support the behavior. " +
            "P868 leaves the product decision and parity row partial."),

        [Fmid.MissionParams] = new(Reason.PendingParity,
            "The native declaration exists at Igc/igc.h:1109-1251; the earlier " +
            "unrecoverable-layout claim was incorrect. MissionParamsMsg and " +
            "Server.MissionParams implement the codec, owner/stage/validation guards " +
            "and relay (FedSrv/FedSrv.CPP:7518-7597). Native live side-list growth " +
            "(FedSrv/fsmission.cpp:1846-1852) remains clamped to boot-time side count " +
            "in this port, so P868 records the remaining branch as partial."),
    };
}

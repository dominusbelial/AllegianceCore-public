using Allegiance.Core;
using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>The PostPlainText consumer, WinTrek/trekigc.cpp:3067-3083.
    /// Game1.UpdateFeedback drains this queue into the HUD under the session gate.</summary>
    public void PostPlainText(bool critical, string text) =>
        PostPlainText(critical, text, soundAlreadyQueued: false);

    private void PostPlainText(bool critical, string text, bool soundAlreadyQueued)
    {
        _feedbackLines.Add(new FeedbackLine(text, critical, NowSeconds(), SoundAlreadyQueued: soundAlreadyQueued));
        if (_feedbackLines.Count > MaxFeedbackLines) _feedbackLines.RemoveAt(0);
    }

    /// <summary>WinTrek/trekigc.cpp:3086-3102. Composite format replaces printf.</summary>
    public void PostText(bool critical, string format, params object?[] args) =>
        PostPlainText(critical, CallbackText.Format(format, args));

    /// <summary>Text belongs only to the actual local ship, including while boarded;
    /// the source carrier is eligible for sound, not text (trekigc.cpp:2994-3018).</summary>
    public void PostNotificationText(short modelShipId, bool critical, string format, params object?[] args)
    {
        if (MyShipId == modelShipId) PostText(critical, format, args);
    }

    // ── P787b — SIM-0195: the resource-pool notification texts ─────────────
    // Native posts "Ammo depleted." / "Reloading ammo..." / "Fuel depleted." /
    // "Reloading fuel..." from the CLIENT's own IGC sim when the pool's
    // SetAmmo/SetFuel crosses to zero and the auto-reload attempt resolves
    // (Igc/shipIGC.h:597-650). The port is a pure-renderer client: the same
    // edges are derived from the own ship's wire ammo/fuel fraction — the
    // positive→≈0 crossing is the SetAmmo(0)/SetFuel(0) transition; a
    // recovery above the epsilon while the decision is pending is the
    // successful auto-reload ("Reloading …" — the pool refills synchronously
    // on the server and the wire fraction comes back inside the grace
    // window); the grace elapsing with the pool still ≈0 is the failed
    // reload ("… depleted.") — the same philosophy as WeaponReloadTracker's
    // HUD statuses (0.35 s grace; P361's reload is instant).

    /// <summary>The grace a pool-zero crossing waits for the wire fraction to
    /// recover before being resolved as "depleted" (seconds).</summary>
    public const double ReloadFeedbackGraceSeconds = 0.35;

    private double _ammoDepletedDeadline = -1;
    private double _fuelDepletedDeadline = -1;

    /// <summary>Arm one pool's depletion decision on a positive→≈0 fraction
    /// crossing — the client-visible shape of native's
    /// <c>SetAmmo(0)</c>/<c>SetFuel(0)</c> transition (shipIGC.h:609/638).</summary>
    private void WatchPoolDepletion(float prevFraction, float nextFraction, float epsilon, ref double deadline)
    {
        if (prevFraction > epsilon && nextFraction <= epsilon)
            deadline = NowSeconds() + ReloadFeedbackGraceSeconds;
    }

    /// <summary>Resolve one pending pool decision: a recovery above the
    /// epsilon posts the reload text (native's success leg, shipIGC.h:617/647);
    /// the grace window elapsing still-empty posts the depleted text (native's
    /// failure leg, shipIGC.h:611-613/640-642). Called once per frame from
    /// <see cref="UpdateOwnStatusAlarms"/> under the gate.</summary>
    private void TickPoolDepletion(float ammoFraction, float fuelFraction,
        float ammoEpsilon, float fuelEpsilon)
    {
        double now = NowSeconds();
        if (_ammoDepletedDeadline > 0)
        {
            if (ammoFraction > ammoEpsilon)
            {
                _ammoDepletedDeadline = -1;
                PostPlainText(critical: false, "Reloading ammo...");
            }
            else if (now >= _ammoDepletedDeadline)
            {
                _ammoDepletedDeadline = -1;
                PostPlainText(critical: false, "Ammo depleted.");
            }
        }
        if (_fuelDepletedDeadline > 0)
        {
            if (fuelFraction > fuelEpsilon)
            {
                _fuelDepletedDeadline = -1;
                PostPlainText(critical: false, "Reloading fuel...");
            }
            else if (now >= _fuelDepletedDeadline)
            {
                _fuelDepletedDeadline = -1;
                PostPlainText(critical: false, "Fuel depleted.");
            }
        }
    }

    /// <summary>P760b/SIM-0197 — one killed model awaiting the game thread.
    /// Native's <c>TargetKilled</c> compares the victim against the ship's
    /// <c>c_cmdCurrent</c> target, which can be ANY model type
    /// (<c>trekigc.cpp:2936-2938</c>), so the entry carries the object type.
    /// In production the client only learns of ship deaths (KILL_SHIP), so the
    /// ship type is the only producer; the type field keeps the comparison
    /// honest for the c_cmdCurrent slot's station/probe/warp targets.</summary>
    public readonly record struct TargetKilledModel(IgcObjectType ObjectType, short ModelId);

    private readonly List<TargetKilledModel> _targetKilled = [];

    /// <summary>P760b — the pending killed-model queue in arrival order. Game1
    /// drains it via <see cref="TakeTargetKilledModels"/> once per frame and
    /// performs the native retarget on the game thread (the previous P760 seam
    /// raised the callback inside <see cref="Apply(KillShip)"/> on the receive
    /// thread, where it mutated Game1._autoTarget, called
    /// CommitTargetForPlayer and read the own pose).</summary>
    public IReadOnlyList<TargetKilledModel> TargetKilledModels => _targetKilled;

    /// <summary>P760b — atomically drain (clear and return) the pending
    /// killed-model entries. Called once per frame by Game1 under the gate
    /// lock, like the other world→Game1 handoffs (TakeFeedbackLines,
    /// TakeCameraOverrideRequests, TakeSoundEvents).</summary>
    public List<TargetKilledModel> TakeTargetKilledModels()
    {
        var copy = new List<TargetKilledModel>(_targetKilled);
        _targetKilled.Clear();
        return copy;
    }

    /// <summary>P760b — enqueue a killed model for the game thread. Called from
    /// <c>Apply(KillShip)</c> for a true death only (an in-space eject keeps the
    /// ship alive as a Lifepod and must not retarget anyone).</summary>
    internal void RecordTargetKilled(IgcObjectType objectType, short modelId) =>
        _targetKilled.Add(new TargetKilledModel(objectType, modelId));

    public readonly record struct MoneyRequestState(short SenderId, int Amount, short HullId, double ExpiresAt);
    private MoneyRequestState? _moneyRequest;

    /// <summary>P755 — raised when the local player accepts/rejects a queued
    /// command or money request and the native client would send the
    /// voAffirmativeSound/voNegativeSound reply. Game1 subscribes and routes it
    /// through <see cref="ConnectionSession.SendChat"/>.</summary>
    public event Action<ChatMessage>? OutgoingChatRequested;

    /// <summary>WinTrek.cpp:7686-7687,10728-10759: one queued request, 15 seconds;
    /// expiry is strict. Reading it never transfers money.</summary>
    public MoneyRequestState? PendingMoneyRequest =>
        _moneyRequest is { } request && NowSeconds() <= request.ExpiresAt ? request : null;

    public void GetMoneyRequest(short senderId, int amount, short hullId)
    {
        // appmsg.cpp:2633-2635 only calls back for a resolved ship.
        if (!_ships.ContainsKey(senderId)) return;
        _myQueuedCommand = null;
        _lastCommandOrMoneySender = senderId;
        _moneyRequest = new(senderId, amount, hullId, NowSeconds() + 15);
        if (senderId != MyShipId)
        {
            string hull = MissionCore?.HullTypes.TryGetValue(hullId, out var type) == true ? type.Name : $"hull {hullId}";
            PostText(true, "{0} has requested ${1} to buy a {2}.  Press Accept command to approve it.",
                DisplayNameFor(senderId), amount, hull);
        }
    }

    /// <summary>WinTrek.cpp:10627-10652: consume once, clamp to the current wallet;
    /// nothing is donated when the requester is the local ship itself (native
    /// <c>pshipSender != trekClient.GetShip()</c>, WinTrek.cpp:10639). The caller
    /// sends PLAYER_DONATE; only the server changes the wallets. P755 also sends
    /// the native voAffirmativeSound reply to the requester.</summary>
    public PlayerDonate? AcceptMoneyRequest()
    {
        var request = PendingMoneyRequest;
        _moneyRequest = null;
        if (request is not { } r || r.SenderId == MyShipId || !_ships.ContainsKey(r.SenderId))
        {
            _lastCommandOrMoneySender = Na;
            return null;
        }
        int amount = Math.Min(r.Amount, MyMoney ?? 0);
        SendAffirmativeChat(r.SenderId);
        _lastCommandOrMoneySender = Na;
        return amount > 0 ? new PlayerDonate { ShipID = r.SenderId, MoneyGiven = amount } : null;
    }

    /// <summary>WinTrek.cpp:10583-10623: explicit reject sends the native
    /// voNegativeSound reply to the requester (P755).</summary>
    public void RejectMoneyRequest()
    {
        if (_moneyRequest is { } request && request.SenderId != MyShipId && _ships.ContainsKey(request.SenderId))
            SendNegativeChat(request.SenderId);
        _moneyRequest = null;
        _lastCommandOrMoneySender = Na;
    }

    // P755 — native SoundIDs for the queued-command/money-request replies.
    // sounddef.mdl:458/1549 → 1178 (voAffirmativeSound); :465/1556 → 1185 (voNegativeSound).
    private const short VoAffirmativeSoundId = 1178;
    private const short VoNegativeSoundId = 1185;

    private void SendAffirmativeChat(short recipient)
    {
        if (recipient < 0 || recipient == MyShipId) return;
        OutgoingChatRequested?.Invoke(new ChatMessage
        {
            Message = "Affirmative.",
            MessageIsAbsent = false,
            SidSender = MyShipId ?? -1,
            ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = recipient,
            VoiceOver = VoAffirmativeSoundId,
        });
    }

    private void SendNegativeChat(short recipient)
    {
        if (recipient < 0 || recipient == MyShipId) return;
        OutgoingChatRequested?.Invoke(new ChatMessage
        {
            Message = "Negative.",
            MessageIsAbsent = false,
            SidSender = MyShipId ?? -1,
            ChatTarget = (int)ChatTarget.Individual,
            OidRecipient = recipient,
            VoiceOver = VoNegativeSoundId,
        });
    }

    /// <summary>Native ChangeGameState refreshes the game-state container after
    /// KILL_SHIP (clintlib/appmsg.cpp:2905; WinTrek/WinTrek.h:258). Subscribers
    /// observe the already-applied state, under the caller's gate.</summary>
    public event Action? GameStateChanged;
    public long GameStateRevision { get; private set; }
    public void ChangeGameState()
    {
        GameStateRevision++;
        GameStateChanged?.Invoke();
    }

    /// <summary>BaseClient::ReceiveChat notifies after insertion
    /// (clintlib/clintlib.cpp:2160-2172; WinTrek/trekigc.cpp:4788-4822).
    /// The same callback covers ordinary chat and chat carrying a buoy.</summary>
    public event Action? ChatReceived;
    public void ReceiveChat(ChatMessage message)
    {
        if (IsPilotMuted(message.SidSender)) return;
        // P787b — SIM-0172: native's default-on unknown-VC filter
        // (trekigc.cpp:4494-4496, m_bFilterUnknownChats = true,
        // trekigc.cpp:2464): a human sender's null-text sonic chat whose
        // caption is "Unknown chat" is dropped entirely — no event-log line,
        // no sound, no callback. (The other three filter toggles default off /
        // to no-filter — FilterChatsToAll/FilterQuickComms false,
        // FilterLobbyChats mode 0/3 — so the port's defaults match native's.)
        if (message.MessageIsAbsent && message.VoiceOver != Na
            && Allegiance.Core.Audio.NativeVoiceCatalog.Caption(message.VoiceOver) == "Unknown chat"
            && _ships.TryGetValue(message.SidSender, out var vcSender)
            && vcSender.Player is { } vcInfo && vcInfo.IsHuman)
            return;
        var voice = Allegiance.Core.Audio.NativeVoiceCatalog.Resolve(message.VoiceOver, message.Message, message.MessageIsAbsent);
        if (message.MessageIsAbsent && message.VoiceOver != Na)
            message.Message = Allegiance.Core.Audio.NativeVoiceCatalog.Caption(message.VoiceOver);
        _events.Record(message);
        // Resolve captions and voice before notifying listeners, retaining the
        // P710 precedence and P732 immutable voice template (trekigc.cpp:4530-4560).
        short sender = message.SidSender;
        bool senderIsLeader = sender != Na
            && _ships.TryGetValue(sender, out var entity)
            && TeamLeaderShipIdFor(entity.SideId) == sender;
        var kind = ClassifyChatSound(message, MyShipId, senderIsLeader);
        if (kind is { } k && (k != ClientSoundEventKind.ChatVoiceOver || voice is not null))
            RecordSoundEvent(k, k == ClientSoundEventKind.ChatVoiceOver ? message.VoiceOver : sender, voice: voice);
        if (message.SidSender == -1 && message.ChatTarget == (int)ChatTarget.Individual
            && message.OidRecipient == MyShipId && NotificationCue.IsNotification(message.VoiceOver))
            PostPlainText(message.VoiceOver == NotificationCue.Critical, message.Message, soundAlreadyQueued: true);
        ChatReceived?.Invoke();
    }

    public void ReceiveChat(ChatBuoy message)
    {
        if (IsPilotMuted(message.Cd.SidSender)) return;
        RegisterCommandBuoy(message.Db); // trekigc.cpp:4494-4496: import the buoy before its order

        // P787b — SIM-0172: the same default-on unknown-VC filter as the chat
        // path (native's buoys go through the same ReceiveChat,
        // trekigc.cpp:4494-4496).
        if (message.MessageIsAbsent && message.Cd.VoiceOver != Na
            && Allegiance.Core.Audio.NativeVoiceCatalog.Caption(message.Cd.VoiceOver) == "Unknown chat"
            && _ships.TryGetValue(message.Cd.SidSender, out var vcSender)
            && vcSender.Player is { } vcInfo && vcInfo.IsHuman)
            return;
        var voice = Allegiance.Core.Audio.NativeVoiceCatalog.Resolve(message.Cd.VoiceOver, message.Message, message.MessageIsAbsent);
        if (message.MessageIsAbsent && message.Cd.VoiceOver != Na)
            message.Message = Allegiance.Core.Audio.NativeVoiceCatalog.Caption(message.Cd.VoiceOver);
        _events.Record(message);
        var chat = new ChatMessage { Message = message.Message, Cd = message.Cd, MessageIsAbsent = message.MessageIsAbsent };
        var kind = ClassifyChatSound(chat, MyShipId, senderIsLeader: false);
        if (kind == ClientSoundEventKind.ChatVoiceOver && voice is not null)
            RecordSoundEvent(kind.Value, message.Cd.VoiceOver, voice: voice);
        ChatReceived?.Invoke();
    }
}

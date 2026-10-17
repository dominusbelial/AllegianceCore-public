using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.ClientCore.State;

public sealed partial class ClientWorld
{
    /// <summary>The PostPlainText consumer, WinTrek/trekigc.cpp:3067-3083.
    /// Game1.UpdateFeedback drains this queue into the HUD under the session gate.</summary>
    public void PostPlainText(bool critical, string text)
    {
        _feedbackLines.Add(new FeedbackLine(text, critical, NowSeconds()));
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

    public readonly record struct MoneyRequestState(short SenderId, int Amount, short HullId, double ExpiresAt);
    private MoneyRequestState? _moneyRequest;

    /// <summary>WinTrek.cpp:7686-7687,10728-10759: one queued request, 15 seconds;
    /// expiry is strict. Reading it never transfers money.</summary>
    public MoneyRequestState? PendingMoneyRequest =>
        _moneyRequest is { } request && NowSeconds() <= request.ExpiresAt ? request : null;

    public void GetMoneyRequest(short senderId, int amount, short hullId)
    {
        // appmsg.cpp:2633-2635 only calls back for a resolved ship.
        if (!_ships.ContainsKey(senderId)) return;
        _myQueuedCommand = null;
        _moneyRequest = new(senderId, amount, hullId, NowSeconds() + 15);
        if (senderId != MyShipId)
        {
            string hull = MissionCore?.HullTypes.TryGetValue(hullId, out var type) == true ? type.Name : $"hull {hullId}";
            PostText(true, "{0} has requested ${1} to buy a {2}.  Press Accept command to approve it.",
                DisplayNameFor(senderId), amount, hull);
        }
    }

    /// <summary>WinTrek.cpp:10627-10652: consume once, clamp to the current wallet.
    /// The caller sends PLAYER_DONATE; only the server changes the wallets.</summary>
    public PlayerDonate? AcceptMoneyRequest()
    {
        var request = PendingMoneyRequest;
        _moneyRequest = null;
        if (request is not { } r || r.SenderId == MyShipId || !_ships.ContainsKey(r.SenderId)) return null;
        int amount = Math.Min(r.Amount, MyMoney ?? 0);
        return amount > 0 ? new PlayerDonate { ShipID = r.SenderId, MoneyGiven = amount } : null;
    }

    public void RejectMoneyRequest() => _moneyRequest = null;

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
        _events.Record(message);
        // Preserve the P710 announcement path before notifying P709 listeners
        // (WinTrek/trekigc.cpp:4536-4560,4788-4822).
        short sender = message.SidSender;
        bool senderIsLeader = sender != Na
            && _ships.TryGetValue(sender, out var entity)
            && TeamLeaderShipIdFor(entity.SideId) == sender;
        if (ClassifyChatSound(message, MyShipId, senderIsLeader) is { } kind)
            RecordSoundEvent(kind, sender);
        if (message.SidSender == -1 && message.ChatTarget == (int)ChatTarget.Individual
            && message.OidRecipient == MyShipId && NotificationCue.IsNotification(message.VoiceOver))
            PostPlainText(message.VoiceOver == NotificationCue.Critical, message.Message);
        ChatReceived?.Invoke();
    }

    public void ReceiveChat(ChatBuoy message)
    {
        _events.Record(message);
        ChatReceived?.Invoke();
    }
}

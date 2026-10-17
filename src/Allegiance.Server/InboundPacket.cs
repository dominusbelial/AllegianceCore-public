using Allegiance.Core.Wire;

namespace Allegiance.Server;

/// <summary>State-independent wire work only. The receive worker publishes this
/// packet after CRC, framing, header and codec validation. No world, peer map,
/// rate budget, clock, capture stream or transport send is accessed here.
/// Errors travel with the command so the existing per-message barriers still
/// report/refuse them on the simulation thread without losing later messages.</summary>
internal sealed class InboundPacket
{
    internal InboundMessage[] Messages { get; private init; } = [];
    internal FedCrcException? Error { get; private init; }

    internal static InboundPacket Prepare(ReadOnlySpan<byte> data, bool decode)
    {
        try
        {
            var messages = FedPacket.Unpack(data);
            var prepared = new InboundMessage[messages.Length];
            for (int i = 0; i < messages.Length; i++)
                prepared[i] = new InboundMessage(messages[i], decode);
            return new InboundPacket { Messages = prepared };
        }
        catch (FedCrcException e) { return new InboundPacket { Error = e }; }
    }
}

internal sealed class InboundMessage
{
    internal byte[] Bytes { get; }
    internal ushort Fmid { get; }
    private readonly object? _value;
    private readonly Exception? _error;
    private readonly bool _decoded;

    internal InboundMessage(byte[] bytes, bool decode)
    {
        Bytes = bytes;
        // FedPacket accepts cbmsg < header size; preserve the old header-error
        // barrier rather than letting such a message abort the receive callback.
        try
        {
            Fmid = FedMessageHeader.Read(bytes).Fmid;
            if (decode) _value = Decode();
        }
        catch (Exception e) { _error = e; }
        _decoded = decode;
    }

    internal T Read<T>()
    {
        if (_error is { } error) throw error;
        return (T)(_decoded ? _value! : Decode()!);
    }

    internal bool BadHeader => Bytes.Length < FedMessageHeader.HeaderSize;

    // Explicit, AOT-safe dispatch: keep this list aligned with ProcessInboundPacket.
    // All authority and economy/loadout/hostile-input rules remain in its handlers.
    private object? Decode() => Fmid switch
    {
        ShowcaseFinaleRequest.Fmid => ShowcaseFinaleRequest.Decode(Bytes),
        Ping.Fmid => Ping.Decode(Bytes),
        LogonRequest.Fmid => LogonRequest.Decode(Bytes),
        ShipUpdate.Fmid => ShipUpdate.Decode(Bytes),
        ActiveTurretUpdate.Fmid => ActiveTurretUpdate.Decode(Bytes),
        InactiveTurretUpdate.Fmid => InactiveTurretUpdate.Decode(Bytes),
        ChatMessage.Fmid => ChatMessage.Decode(Bytes),
        Vote.Fmid => Vote.Decode(Bytes),
        ChatBuoy.Fmid => ChatBuoy.Decode(Bytes),
        HighlightCluster.Fmid => HighlightCluster.Decode(Bytes),
        Docked.Fmid => Docked.Decode(Bytes),
        BuyLoadoutRequest.Fmid => BuyLoadoutRequest.Decode(Bytes),
        PositionReq.Fmid => PositionReq.Decode(Bytes),
        PositionAck.Fmid => PositionAck.Decode(Bytes),
        DelPositionReq.Fmid => DelPositionReq.Decode(Bytes),
        ForceTeamReady.Fmid => ForceTeamReady.Decode(Bytes),
        StartGame.Fmid => StartGame.Decode(Bytes),
        RandomizeTeams.Fmid => RandomizeTeams.Decode(Bytes),
        AutoDonate.Fmid => AutoDonate.Decode(Bytes),
        RequestMoney.Fmid => RequestMoney.Decode(Bytes),
        PlayerDonate.Fmid => PlayerDonate.Decode(Bytes),
        LockLobby.Fmid => LockLobby.Decode(Bytes),
        LockSides.Fmid => LockSides.Decode(Bytes),
        SideInactive.Fmid => SideInactive.Decode(Bytes),
        AutoAccept.Fmid => AutoAccept.Decode(Bytes),
        ChangeTeamCiv.Fmid => ChangeTeamCiv.Decode(Bytes),
        ChangeAlliance.Fmid => ChangeAlliance.Decode(Bytes),
        PlayerReady.Fmid => PlayerReady.Decode(Bytes),
        Suicide.Fmid => Suicide.Decode(Bytes),
        Logoff.Fmid => Logoff.Decode(Bytes),
        ViewClusterRequest.Fmid => ViewClusterRequest.Decode(Bytes),
        BoardShip.Fmid => BoardShip.Decode(Bytes),
        PromoteRequest.Fmid => PromoteRequest.Decode(Bytes),
        SwapPart.Fmid => SwapPart.Decode(Bytes),
        Reload.Fmid => Reload.Decode(Bytes),
        DropPart.Fmid => DropPart.Decode(Bytes),
        SetWingId.Fmid => SetWingId.Decode(Bytes),
        TreasureAck.Fmid => TreasureAck.Decode(Bytes),
        RipcordRequest.Fmid => RipcordRequest.Decode(Bytes),
        BucketDonate.Fmid => BucketDonate.Decode(Bytes),
        OrderChange.Fmid => OrderChange.Decode(Bytes),
        FireExpendableRequest.Fmid => FireExpendableRequest.Decode(Bytes),
        FireMissile.Fmid => FireMissile.Decode(Bytes),
        ReqPingData.Fmid => ReqPingData.Decode(Bytes),
        _ => null, // unknown fmids still use the existing rate-limited refusal
    };
}

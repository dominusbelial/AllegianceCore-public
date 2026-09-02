using Allegiance.Core.Collision;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;

namespace Allegiance.Bot;

/// <summary>
/// A bot — a <see cref="TcpTransport"/> client that speaks the real ported
/// messages. Connects, logs on, waits for its <c>SET_CLUSTER(7)</c> ship
/// assignment, sends <c>SHIP_UPDATE(24)</c> control input, collects
/// <c>SINGLE_SHIP_UPDATE(78)</c> observations of its own ship's position, and
/// records every <c>KILL_SHIP(105)</c> it observes on the wire.</summary>
/// <remarks>
/// <para>Designed to be driveable two ways: interactively by the console
/// <c>Program.Main</c> (step 4), and synchronously by the in-process tests
/// (verification). It implements <see cref="ITransportSite"/> so it can sit
/// directly on a client <see cref="TcpTransport"/> and route inbound datagrams
/// to decoded messages; the read happens on the transport thread, guarded by a
/// lock so a caller on another thread can query observations safely.</para>
/// <para>Nothing here assumes wall-clock cadence — a caller (console or test)
/// decides how long to observe. The transports are loopback-real: the messages
/// are genuinely encoded, framed, CRC'd and decoded over a TCP socket.</para>
/// </remarks>
public sealed class Bot : ITransportSite, IDisposable
{
    private TcpTransport? _transport;
    private readonly object _gate = new();

    private LogonAck? _ack;
    private SetCluster? _setCluster;
    private readonly List<SetCluster> _assignments = new();
    private readonly List<ShipObservation> _observations = new();
    private readonly List<KillShip> _kills = new();
    private readonly List<ChatMessage> _chats = new();
    private readonly List<ShipStatusMessage> _shipStatuses = new();
    private readonly List<ClusterInfo> _clusterInfos = new();
    private short _myShipId = -1;

    /// <summary>A SINGLE_SHIP_UPDATE observation of a ship, timestamped with the
    /// server tick index (the uint <c>Time</c> slot of the payload).</summary>
    public readonly record struct ShipObservation(short ShipId, uint Tick, Vector3 Position, Vector3 Velocity, Orientation Orientation);

    public bool Connected => _transport?.IsConnected ?? false;

    public LogonAck? Ack { get { lock (_gate) return _ack; } }

    public SetCluster? ShipAssignment { get { lock (_gate) return _setCluster; } }

    /// <summary>Every SET_CLUSTER(7) assignment received, in arrival order —
    /// the join-burst assignment plus one per warp transit (each transit makes
    /// the server re-send SET_CLUSTER with the new sector). Mirrors
    /// <see cref="ShipStatuses"/>: locked, snapshot-on-read.</summary>
    public IReadOnlyList<SetCluster> Assignments { get { lock (_gate) return _assignments.ToArray(); } }

    /// <summary>All SINGLE_SHIP_UPDATE observations received so far, in arrival order.</summary>
    public IReadOnlyList<ShipObservation> Observations { get { lock (_gate) return _observations.ToArray(); } }

    /// <summary>Clear the accumulated observations (between manoeuvres).</summary>
    public void ResetObservations() { lock (_gate) _observations.Clear(); }

    /// <summary>All KILL_SHIP(105) messages received so far, in arrival order — the
    /// deaths this bot has observed on the wire. Mirrors <see cref="Observations"/>:
    /// locked, snapshot-on-read, cleared via <see cref="ResetKills"/>.</summary>
    public IReadOnlyList<KillShip> Kills { get { lock (_gate) return _kills.ToArray(); } }

    /// <summary>Clear the accumulated kills (between engagements).</summary>
    public void ResetKills() { lock (_gate) _kills.Clear(); }

    /// <summary>All CHATMESSAGE(23) messages this bot has received, in arrival
    /// order. Mirrors <see cref="Kills"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetChatReceived"/>.</summary>
    public IReadOnlyList<ChatMessage> Chats { get { lock (_gate) return _chats.ToArray(); } }

    /// <summary>Clear the accumulated chats (between phases of a chat exchange).</summary>
    public void ResetChatReceived() { lock (_gate) _chats.Clear(); }

    /// <summary>All SHIP_STATUS(90) messages received so far, in arrival order —
    /// the per-ship status frames the server sends about this bot's own ship.
    /// Mirrors <see cref="Kills"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetShipStatuses"/>.</summary>
    public IReadOnlyList<ShipStatusMessage> ShipStatuses { get { lock (_gate) return _shipStatuses.ToArray(); } }

    /// <summary>Clear the accumulated ship statuses (between joins/phases).</summary>
    public void ResetShipStatuses() { lock (_gate) _shipStatuses.Clear(); }

    /// <summary>Every CLUSTERINFO(200) received so far, in arrival order — the
    /// map-overview message the server sends at the start of the join burst so
    /// the native client's <c>GameInfo.Clusters</c> population can engage the
    /// smart exploration branches (<c>ScoutExploreMap</c>). Mirrors
    /// <see cref="ShipStatuses"/>: locked, snapshot-on-read, cleared via
    /// <see cref="ResetClusterInfos"/>.</summary>
    public IReadOnlyList<ClusterInfo> ClusterInfos { get { lock (_gate) return _clusterInfos.ToArray(); } }

    /// <summary>Clear the accumulated cluster infos (between joins/phases).</summary>
    public void ResetClusterInfos() { lock (_gate) _clusterInfos.Clear(); }

    /// <summary>
    /// Send a chat message, encoded as a real <c>FM_CS_CHATMESSAGE(23)</c> —
    /// the mirror of <see cref="CommandControls"/> for chat. The sender ship is
    /// filled from this bot's own assignment (<c>cd.sidSender</c>,
    /// <c>Messages.h:206</c>), text is encoded as a CB_ZTS blob
    /// (<c>clintlib.cpp:2444-2445</c>), and the supported targets are
    /// <see cref="ChatTarget.Everyone"/>, <see cref="ChatTarget.Team"/>,
    /// <see cref="ChatTarget.Individual"/> (and its aliases
    /// <see cref="ChatTarget.IndividualNoFilter"/> /
    /// <see cref="ChatTarget.Group"/> / <see cref="ChatTarget.GroupNoEcho"/>),
    /// <see cref="ChatTarget.AllSector"/> and <see cref="ChatTarget.FriendlySector"/>
    /// (others are relayed to nobody by the server, which logs them — see
    /// <c>Server.cs::SelectChatTargets</c>).
    /// </summary>
    public void SendChat(
        string message,
        ChatTarget target = ChatTarget.Everyone,
        short oidRecipient = 0,
        short voiceOver = 0,
        short commandId = 0,
        bool objectModel = false,
        short oidTarget = -1,
        short otTarget = -1)
    {
        var msg = new ChatMessage
        {
            Message = message,
            SidSender = _myShipId,
            ChatTarget = (int)target,
            OidRecipient = oidRecipient,
            VoiceOver = voiceOver,
            CommandID = commandId,
            BObjectModel = objectModel,
            OidTarget = oidTarget,
            OtTarget = otTarget,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Connect to a hosting server and publish our player name.</summary>
    public void Connect(ushort port, string playerName)
    {
        _transport = new TcpTransport(this);
        _transport.Join("127.0.0.1", port, playerName);
    }

    /// <summary>Send a LOGONREQ; the ACK (or rejection) is received asynchronously.</summary>
    public void SendLogonRequest(LogonRequest req) =>
        Send(FedPacket.Pack(new[] { req.Encode() }));

    /// <summary>Command the ship's controls: the four axes and the movement-bit
    /// <paramref name="stateM"/>, encoded as a real <c>FM_C_SHIP_UPDATE(24)</c>.</summary>
    public void CommandControls(ControlData controls, int stateM = 0)
    {
        var msg = new ShipUpdate
        {
            Time = 0u,              // client's own time — the server reads controls only
            Cookie = (byte)(_ack?.Cookie ?? 0),
            Payload = ClientShipUpdate.FromControls(0u, stateM, controls),
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Block until a LOGONACK arrives (or <paramref name="timeoutMs"/>). Returns the ACK.</summary>
    public LogonAck WaitForAck(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var ack = Ack;
            if (ack is not null) return ack;
            Thread.Sleep(5);
        }
        throw new TimeoutException("timed out waiting for LOGONACK");
    }

    /// <summary>Block until the SET_CLUSTER ship assignment arrives (or <paramref name="timeoutMs"/>).</summary>
    public SetCluster WaitForShipAssignment(int timeoutMs)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline)
        {
            var set = ShipAssignment;
            if (set is not null) return set;
            Thread.Sleep(5);
        }
        throw new TimeoutException("timed out waiting for SET_CLUSTER ship assignment");
    }

    private void Send(byte[] packet) =>
        _transport!.Send(ConnectionId.Server, packet, guaranteed: true);

    // ── ITransportSite (client half: only OnReceive is raised) ─────────────
    void ITransportSite.OnPeerJoined(ConnectionId id, string name) { }
    void ITransportSite.OnPeerLeft(ConnectionId id) { }
    void ITransportSite.OnSessionLost() { }
    void ITransportSite.OnReceive(ConnectionId from, ReadOnlySpan<byte> data)
    {
        byte[][] messages;
        try { messages = FedPacket.Unpack(data.ToArray()); }
        catch (FedCrcException) { return; }   // a bad frame is not ours to adjudicate

        lock (_gate)
        {
            foreach (var m in messages)
            {
                ushort fmid;
                try { fmid = FedMessageHeader.Read(m).Fmid; }
                catch { continue; }

                switch (fmid)
                {
                    case LogonAck.Fmid:
                        _ack = LogonAck.Decode(m);
                        break;
                    case SetCluster.Fmid:
                        _setCluster = SetCluster.Decode(m);
                        _assignments.Add(_setCluster);
                        _myShipId = _setCluster.Ship.ShipId;
                        break;
                    case SingleShipUpdate.Fmid:
                        var u = SingleShipUpdate.Decode(m);
                        if (_myShipId >= 0 && u.OidTarget == _myShipId)
                            _observations.Add(new ShipObservation(
                                u.OidTarget, u.Ship.Time, u.Ship.Position, u.Ship.Velocity, u.Ship.Orientation));
                        break;
                    case KillShip.Fmid:
                        _kills.Add(KillShip.Decode(m));
                        break;
                    case ChatMessage.Fmid:
                        _chats.Add(ChatMessage.Decode(m));
                        break;
                    case ShipStatusMessage.Fmid:
                        _shipStatuses.Add(ShipStatusMessage.Decode(m));
                        break;
                    case ClusterInfo.Fmid:
                        _clusterInfos.Add(ClusterInfo.Decode(m));
                        break;
                }
            }
        }
    }

    /// <summary>Close the client socket. The server sees EOF / a dropped peer.</summary>
    public void Shutdown() => _transport?.Shutdown();

    public void Dispose() => Shutdown();
}

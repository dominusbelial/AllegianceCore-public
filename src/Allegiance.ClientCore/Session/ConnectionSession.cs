using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.Core.Transport;
using Allegiance.Core.Wire;
using Allegiance.ClientCore.State;

namespace Allegiance.ClientCore.Session;

/// <summary>
/// A <see cref="TcpTransport"/> client session that drives a <see cref="ClientWorld"/>.
/// Closely modeled on AllegianceCore's own <c>Bot.cs</c>: it connects, sends a
/// LOGONREQ, waits for the ACK, commands controls as a real <c>FM_C_SHIP_UPDATE(24)</c>,
/// and on the receive path decodes inbound messages — feeding every type
/// <see cref="ClientWorld"/> understands (plus <see cref="LogonAck"/> for connect
/// proof) into <see cref="World"/> under a lock. The shared <c>ExportShip.Fmid</c>
/// (26) is branched on its raw <c>objecttype</c> before decoding, like <c>Bot.cs</c>.
///
/// <see cref="World"/> is exposed the same way <c>Bot.cs</c> exposes its
/// <c>IReadOnlyList&lt;T&gt;</c> properties: it is the single live model, its
/// mutations all happen under <see cref="_gate"/> on the receive thread, and a
/// caller reads it (after a bounded wait/pump) on its own thread without holding
/// the lock. This is the client half of the transport, so only
/// <see cref="ITransportSite.OnReceive"/> is raised.
/// </summary>
public sealed class ConnectionSession : ITransportSite, IDisposable
{
    private TcpTransport? _transport;
    private readonly object _gate = new();
    private readonly ClientWorld _world = new();
    private LogonAck? _ack;

    /// <summary>True while the underlying transport is connected.</summary>
    public bool Connected => _transport?.IsConnected ?? false;

    /// <summary>The last LOGONACK (or rejection) received; null until one arrives.</summary>
    public LogonAck? Ack { get { lock (_gate) return _ack; } }

    /// <summary>The live world model, upserted on the receive thread under
    /// <see cref="_gate"/>. Mutations happen on the transport thread; a caller
    /// should wait for the state it expects (e.g. <see cref="ClientWorld.MyShipId"/>)
    /// rather than read blind.</summary>
    public ClientWorld World => _world;

    /// <summary>The lock the receive thread holds while upserting <see cref="World"/>.
    /// A caller that does more than a single, isolated <c>TryGetValue</c> against one
    /// of <see cref="World"/>'s dictionaries — in particular any <c>foreach</c> over
    /// <see cref="ClientWorld.Ships"/> or its sibling tables — must hold this lock
    /// for the duration, or risk a real <c>System.InvalidOperationException</c>
    /// ("Collection was modified") the moment the receive thread upserts an entry
    /// mid-enumeration. This was latent (a single peer's update cadence rarely won
    /// the race) until a burst of ~20 simultaneous ship joins made it reliably
    /// reproducible live in the MonoGame client.</summary>
    public object Gate => _gate;

    /// <summary>Connect to a hosting server and register our name. Defaults to
    /// loopback (every existing caller's behavior, unchanged) — pass
    /// <paramref name="host"/> to reach a server elsewhere on the network.
    /// Confirmed live (2026-09-15): this previously hardcoded 127.0.0.1
    /// unconditionally, silently ignoring any other host the caller supplied
    /// (Allegiance.ClientCore.MonoGame/Program.cs warned about this rather
    /// than silently dropping it, but no caller had ever lifted the actual
    /// restriction, since this project's testing was LAN-loopback only until
    /// this session's netem-latency experiment needed a real non-loopback
    /// peer).</summary>
    public void Connect(ushort port, string playerName, string host = "127.0.0.1")
    {
        _transport = new TcpTransport(this);
        _transport.Join(host, port, playerName);
    }

    /// <summary>Send a LOGONREQ; the ACK (or rejection) is received asynchronously.</summary>
    public void SendLogonRequest(LogonRequest req) =>
        Send(FedPacket.Pack(new[] { req.Encode() }));

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

    /// <summary>Command the ship's controls: the four axes and the movement-bit
    /// <paramref name="stateM"/>, encoded as a real <c>FM_C_SHIP_UPDATE(24)</c>.
    /// Mirror of <c>Bot.CommandControls</c>.</summary>
    public void CommandControls(ControlData controls, int stateM = 0)
    {
        var msg = new ShipUpdate
        {
            Time = 0u,              // client's own time — the server reads controls only
            Cookie = (byte)(Ack?.Cookie ?? 0),
            Payload = ClientShipUpdate.FromControls(0u, stateM, controls),
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send an undock announce, encoded as a real <c>FM_C_DOCKED(48)</c>
    /// — the mirror of <see cref="CommandControls"/> for the only surviving half
    /// of that message. <paramref name="stationId"/> of <c>-1</c> (NA) is the
    /// pure-undock announce the server honours (<c>Messages.h:402-404</c>;
    /// <c>stationID != NA</c> is the obsolete dock-request half, dropped by
    /// Server.cs). Same signature/semantics as <c>Bot.SendDocked</c>.</summary>
    public void SendDocked(short stationId = -1)
    {
        var msg = new Docked { StationId = stationId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_BUY_LOADOUT(4)</c> — what the native
    /// WinTrek client's Hangar screen "Launch" button sends. Same
    /// signature/semantics as <c>Bot.SendBuyLoadout</c>: <paramref name="parts"/>
    /// defaults to empty (the hull-only shape). <paramref name="fLaunch"/>=true
    /// is the real client's launch request; the server answers with
    /// <c>FM_S_BUY_LOADOUT_ACK(5)</c> and, if the ship was docked, undocks it
    /// (a second SetCluster lands).</summary>
    public void SendBuyLoadout(short hullId, bool fLaunch, IReadOnlyList<ExpandedPartData>? parts = null)
    {
        var msg = new BuyLoadoutRequest { HullId = hullId, FLaunch = fLaunch, Parts = parts ?? [] };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a chat message, encoded as a real <c>FM_CS_CHATMESSAGE(23)</c> —
    /// the mirror of <c>Bot.SendChat</c> (ported like <see cref="SendDocked"/>/
    /// <see cref="SendBuyLoadout"/> were). The sender ship is filled from this
    /// session's own assignment (<c>cd.sidSender</c>, <c>Messages.h:206</c>),
    /// text is encoded as a CB_ZTS blob (<c>clintlib.cpp:2444-2445</c>), and the
    /// supported targets mirror <c>Bot.SendChat</c> — the server relays
    /// Everyone/Team/Individual(+aliases)/AllSector/FriendlySector and drops the
    /// rest, which the server logs. The relayed message (including the sender's
    /// own echo) lands in <see cref="ClientWorld.Events.Chats"/>.</summary>
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
            SidSender = (short)(World.MyShipId ?? -1),
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

    /// <summary>Send a real <c>FM_CS_CHATBUOY(108)</c> — the message the native
    /// <c>BaseClient::SendChat</c> actually uses when the chat targets a buoy
    /// object (<c>clintlib.cpp:2460-2469</c>: <c>otTarget == OT_buoy</c> →
    /// CHATBUOY, with the target's <see cref="DataBuoyIGC db"/> exported into
    /// the payload; otherwise CHATMESSAGE). The commander's dispatch to a
    /// docked miner (<c>CommanderResearchAndExpand.cs:1615-1618</c>) is the
    /// canonical real use. Routing semantics match <see cref="SendChat"/>
    /// exactly (the server's <c>SelectChatTargets</c> is shared via
    /// <see cref="IChatRoutingTarget"/>). Same signature as
    /// <c>Allegiance.Bot.Bot.SendChatBuoy</c>; the sender ship is filled from
    /// this session's own assignment. The relayed buoy (excluding the
    /// individual-addressed case, which the server sends only to the addressed
    /// peer, and which like <see cref="SendChat"/> is not echoed back to the
    /// sender) lands in <see cref="ClientWorld.Events.ChatBuoys"/>.</summary>
    public void SendChatBuoy(
        string message,
        ChatTarget target,
        short oidRecipient,
        DataBuoyIGC db,
        short voiceOver = 0,
        short commandId = 0,
        bool objectModel = false)
    {
        var msg = new ChatBuoy
        {
            Message = message,
            Cd = new ChatData
            {
                SidSender = (short)(World.MyShipId ?? -1),
                ChatTarget = (int)target,
                OidRecipient = oidRecipient,
                VoiceOver = voiceOver,
                CommandID = commandId,
                BObjectModel = objectModel,
            },
            Db = db,
        };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_CS_PLAYER_READY(45)</c> — what the native
    /// WinTrek client's Team Lobby \"JOIN\" control sends (<c>teampane.cpp</c>),
    /// the mirror of <c>Bot.SendPlayerReady</c>. Same signature exactly. The
    /// server is expected to relay it back to everyone on the sender's side,
    /// including the sender itself (<c>Server.cs</c> relays to same-side peers);
    /// the relay lands in <see cref=\"ClientWorld.Events.PlayerReadyEchoes\"/>
    /// and updates <see cref=\"ClientWorld.Roster\"/> (the current-state map),
    /// but the sender never applies its own click locally — it waits for this
    /// relay, exactly like the native client's <c>appmsg.cpp:2297-2312</c>.</summary>
    public void SendPlayerReady(bool fReady, short shipId)
    {
        var msg = new PlayerReady { FReady = fReady, ShipId = shipId };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_SUICIDE(80)</c> — the client's "I want to
    /// die" request, the mirror of <c>Bot.SendSuicide</c>. The message is
    /// header-only (no fields to encode), so this takes no parameters. The
    /// server's <c>HandleSuicide</c> (<c>Server.cs:3869</c>) re-triggers the
    /// exact combat-kill path a normal death uses (<c>KillShipEvent</c>,
    /// launcher NULL), so this produces the same <c>KILL_SHIP(105)</c> death
    /// broadcast a combat death does — gated server-side on the mission being
    /// <c>STAGE_STARTED</c>, the ship being in a cluster, and not being docked.</summary>
    public void SendSuicide()
    {
        var msg = new Suicide();
        Send(FedPacket.Pack(new[] { msg.Encode() }));
    }

    /// <summary>Send a real <c>FM_C_FIRE_EXPENDABLE(82)</c> — the client's \"fire my
    /// mounted dispenser\" request, the mirror of <c>Bot.SendFireExpendable</c>. The
    /// single field is the <see cref="EquipmentType"/> of the mounted dispenser to
    /// fire (the server resolves it at mount 0 via <c>GetMountedPart(et, 0)</c>,
    /// <c>FedSrv.CPP:5652</c>). The server's <c>HandleFireExpendable</c>
    /// (<c>Server.cs:6658</c>) deploys the expendable (chaff for a chaff launcher),
    /// decrements its ammo, and broadcasts the
    /// <c>FE(87)+CREATE_CHAFF(109)+MISSILE_SPOOFED(110)+END_SPOOFING(111)</c> flow
    /// sector-wide. Generic over <see cref="EquipmentType"/> (not chaff-specific) so
    /// a near-future mine/probe task can reuse it with a different value.</summary>
    public void SendFireExpendable(EquipmentType et)
    {
        var msg = new FireExpendableRequest { Et = et };
        Send(FedPacket.Pack(new[] { msg.Encode() }));
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
                        _world.Apply(SetCluster.Decode(m));
                        break;
                    case SingleShipUpdate.Fmid:
                        _world.Apply(SingleShipUpdate.Decode(m));
                        break;
                    case ShipStatusMessage.Fmid:
                        _world.Apply(ShipStatusMessage.Decode(m));
                        break;
                    case ExportShip.Fmid:
                        // FM_S_EXPORT(26) is the generic object-export message:
                        // ships (OT_ship=0), clusters (OT_cluster=13), stations
                        // (OT_station=1), warps (OT_warp=7) and asteroids
                        // (OT_asteroid=5) all ride this fmid, distinguished by
                        // the 4-byte `objecttype` at message offset 8. Branch on
                        // ObjectType first (same approach as Allegiance.Bot.Bot.cs),
                        // decode the matching codec, feed it to the world.
                        if (m.Length >= 12)
                        {
                            int ot = BitConverter.ToInt32(m.AsSpan(8, 4));
                            if (ot == ExportShip.OtShip)
                                _world.Apply(ExportShip.Decode(m));
                            else if (ot == ExportCluster.OtCluster)
                                _world.Apply(ExportCluster.Decode(m));
                            else if (ot == ExportStation.OtStation)
                                _world.Apply(ExportStation.Decode(m));
                            else if (ot == ExportWarp.OtWarp)
                                _world.Apply(ExportWarp.Decode(m));
                            else if (ot == ExportAsteroid.OtAsteroid)
                                _world.Apply(ExportAsteroid.Decode(m));
                        }
                        break;
                    case PlayerInfo.Fmid:
                        _world.Apply(PlayerInfo.Decode(m));
                        break;
                    case ClusterInfo.Fmid:
                        _world.Apply(ClusterInfo.Decode(m));
                        break;
                    case DockedNotify.Fmid:
                        _world.Apply(DockedNotify.Decode(m));
                        break;
                    case BuyLoadoutAck.Fmid:
                        _world.Apply(BuyLoadoutAck.Decode(m));
                        break;
                    case ChatMessage.Fmid:
                        _world.Apply(ChatMessage.Decode(m));
                        break;
                    case ChatBuoy.Fmid:
                        _world.Apply(ChatBuoy.Decode(m));
                        break;
                    case Payday.Fmid:
                        _world.Apply(Payday.Decode(m));
                        break;
                    case KillShip.Fmid:
                        _world.Apply(KillShip.Decode(m));
                        break;
                    case PlayerReady.Fmid:
                        _world.Apply(PlayerReady.Decode(m));
                        break;
                    case MissionDef.Fmid:
                        _world.Apply(MissionDef.Decode(m));
                        break;
                    case MissionStageMessage.Fmid:
                        _world.Apply(MissionStageMessage.Decode(m));
                        break;
                    case SetStartTime.Fmid:
                        _world.Apply(SetStartTime.Decode(m));
                        break;
                    case GameOver.Fmid:
                        _world.Apply(GameOver.Decode(m));
                        break;
                }
            }
        }
    }

    /// <summary>Close the client socket. The server sees EOF / a dropped peer.</summary>
    public void Shutdown() => _transport?.Shutdown();

    public void Dispose() => Shutdown();
}

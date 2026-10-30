using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private readonly CommandActionInput _commandActionInput = new();
    private Allegiance.Core.Model.StaticCore? _missionCatalogCore;

    private readonly Dictionary<string, Allegiance.Core.Model.StaticCore> _assetMissionCores = new(StringComparer.OrdinalIgnoreCase);

    // Packaged/Android clients use IAssetSource, while bot sessions use the
    // shared on-disk cache. This resolver runs only at mission entry under Gate.
    private Allegiance.Core.Model.StaticCore? ResolveAssetMissionCore(string name)
    {
        if (name.Length == 0 || name != System.IO.Path.GetFileName(name) || name.Contains('\\')) return null;
        string key = name.EndsWith(".igc", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
        if (_assetMissionCores.TryGetValue(key, out var cached)) return cached;
        string file = key + ".igc";
        if (!_assets.Exists(file)) return null;
        var core = Allegiance.Core.Model.StaticCore.Load(IgcReader.Parse(Assets.AssetSources.ReadAllBytes(_assets, file)));
        _assetMissionCores[key] = core;
        return core;
    }

    private (long Generation, short? Side) _investmentContext = (-1, null);
    private void UpdateMissionCatalog()
    {
        Allegiance.Core.Model.StaticCore? core;
        lock (_session.Gate)
        {
            core = _session.World.MissionCore;
            var context = (_session.World.MissionGeneration, _session.World.MySideId);
            if (_investmentContext != context)
            {
                _investmentContext = context;
                _investDefs = null;
                _investActive = false;
                _investCursor = 0;
                _dockedActive = false;
                _shopModel = null;
                _dockOfferTechs = null;
                _dockOfferStationType = -1;
            }
        }
        if (core is null || ReferenceEquals(core, _missionCatalogCore)) return;
        _missionCatalogCore = core;
        _shopCatalog = ShopCatalog.FromCore(core);
        _projectileTypes = core.ProjectileTypes;
    }

    private void HandleCommandActionKeys(KeyboardState keys, bool composing)
    {
        CommandActionInput.Result result;
        AutopilotTarget? selection;
        ChatMessage? reply = null;
        _session.World.OutgoingChatRequested += OnOutgoingChat;
        try
        {
            lock (_session.Gate)
            {
                result = _commandActionInput.Step(keys, composing, KeyBindingStore.Instance.Map, _session.World);
                selection = CommandSelection(_session.World, result.Selection);
                if (result.JoinWing is { } wing && _session.World.MyShipId is { } me)
                    _session.SendWingId(me, wing);
                // A rescuing ship also tells the pod to pick it up (native 10664-10677).
                if (result.Request is { CommandId: OrderChange.CidPickup } && result.Selection is { TargetType: IgcObjectType.Ship } pickup
                    && _session.World.MyShipId is { } rescuer && _session.World.Ships.TryGetValue(rescuer, out var own)
                    && _session.World.MissionCore?.HullTypes.TryGetValue(own.HullId, out var hull) == true
                    && (hull.HabmCapabilities & DataHullTypeIGC.HabmRescue) != 0)
                    _session.SendChat("", Allegiance.Core.Sim.ChatTarget.Individual, pickup.TargetId,
                        commandId: OrderChange.CidPickup, objectModel: true, oidTarget: rescuer, otTarget: (short)IgcObjectType.Ship);
            }
        }
        finally
        {
            _session.World.OutgoingChatRequested -= OnOutgoingChat;
        }
        if (reply is { } chat)
            _session.SendChat(chat.Message, (Allegiance.Core.Sim.ChatTarget)chat.ChatTarget, chat.OidRecipient,
                voiceOver: chat.VoiceOver, absentMessage: chat.MessageIsAbsent);
        if (result.Donation is { } donation) _session.SendPlayerDonation(donation);
        if (result.Request is { CommandId: not OrderChange.CidNone }) _sound?.PlayCommandAccepted();
        if (result.Request is { } request)
            _session.SendOrderChange(request.ShipID, request.CommandId, request.Command, request.ObjectType, request.ObjectID);
        if (result.ChangeSelection)
        {
            _autoTarget = selection;
            // HUD-8b — native command issue/target-command writes BOTH c_cmdCurrent
            // and c_cmdAccepted (WinTrek.cpp:10681-10682; TK_TargetCommand
            // WinTrek.cpp:10408-10412), so the current target command follows the
            // accepted target here — not the autopilot slot.
            SendCurrentCommandToServer(selection);
            if (selection is { } chosen && result.Engage) EngageAutopilotToward(chosen, "execute:");
            RefreshWindowTitle();
        }
        void OnOutgoingChat(ChatMessage m) => reply = m;
    }

    // TK_TargetCommand uses the accepted slot, never the current selection or queue.
    internal static AutopilotTarget? CommandSelection(ClientWorld world, QueuedCommandState? command)
    {
        if (command is not { } c || !world.TryCommandTarget(c, out _, out var name)) return null;
        TargetKind? kind = c.TargetType switch
        {
            IgcObjectType.Ship => TargetKind.Ship, IgcObjectType.Station => TargetKind.Station,
            IgcObjectType.Asteroid => TargetKind.Asteroid, IgcObjectType.Warp => TargetKind.Warp,
            IgcObjectType.Probe => TargetKind.Probe, IgcObjectType.Buoy => TargetKind.Buoy, IgcObjectType.Treasure => TargetKind.Treasure, _ => null,
        };
        // HUD-12b — native TK_TargetCommand copies the ACCEPTED slot's id
        // verbatim into c_cmdCurrent (WinTrek.cpp:10408-10412), including
        // c_cidNone; only c_cidDefault resolves through GetDefaultOrder.
        return kind is { } k ? new AutopilotTarget(k, c.TargetId, name, c.CommandId, UseDefault: false) : null;
    }

    // HUD-8b — the reverse mapping for the native c_cmdCurrent slot: a selected
    // AutopilotTarget becomes a QueuedCommandState so ComputeOrderArrows can reuse
    // the same command-position resolution as the accepted arrow. A plain
    // T-selection carries CommandId=c_cidNone (native SetTarget's
    // `cid == c_cidDefault → GetDefaultOrder` fallback, WinTrek.cpp:8827-8833);
    // the concrete action id is preserved when the selection came from a command
    // (CommandSelection).
    internal static QueuedCommandState? CurrentCommandState(AutopilotTarget? target)
    {
        if (target is not { } t) return null;
        var type = ObjectTypeFor(t.Kind);
        return type is { } ot ? new QueuedCommandState(t.CommandId, ot, t.Id) : null;
    }

    /// <summary>The orderable target kinds → native <c>ObjectType</c>; null for a
    /// cluster rectangle, which is not a model target (WinTrek.cpp:8845-8854).</summary>
    internal static IgcObjectType? ObjectTypeFor(TargetKind kind) => kind switch
    {
        TargetKind.Ship => IgcObjectType.Ship, TargetKind.Station => IgcObjectType.Station,
        TargetKind.Asteroid => IgcObjectType.Asteroid, TargetKind.Warp => IgcObjectType.Warp,
        TargetKind.Buoy => IgcObjectType.Buoy,
        TargetKind.Probe => IgcObjectType.Probe, TargetKind.Treasure => IgcObjectType.Treasure,
        _ => null,
    };

    /// <summary>HUD-11 — the exact <c>c_cmdCurrent</c> payload native
    /// <c>SetTarget</c> sends (WinTrek.cpp:8827-8854). A plain target pick carries
    /// <c>c_cidNone</c> in the <see cref="AutopilotTarget"/>; native treats
    /// <c>c_cidDefault</c> as "resolve GetDefaultOrder(pmodel)" and sends that
    /// concrete id. A target selected from a command keeps its explicit id.</summary>
    internal readonly record struct CurrentCommandSet(short CommandId, IgcObjectType ObjectType, short ObjectId);

    /// <summary>Native <c>GetDefaultOrder</c> switches on <c>m_pilotType</c>
    /// (<c>shipIGC.h:2078-2100</c>); a human is <c>c_ptPlayer</c>, so the
    /// Miner/Builder/Layer branches never apply to the own ship even when its
    /// hull carries those capability bits. The remaining hull bits still feed
    /// the player branch (rescue, lifepod, carrier landing, fighter).</summary>
    internal static short PlayerPilotRoles(short hullRoles) =>
        (short)(hullRoles & ~(DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder));

    internal static CurrentCommandSet? CurrentCommandSetFor(
        AutopilotTarget? target, short ownRoleBits, bool ownIsLayer, bool ownHasParent,
        in CommandTargetProfile profile)
    {
        if (target is not { } t) return null;
        var type = ObjectTypeFor(t.Kind);
        if (type is null) return null;
        // HUD-12b — only a real c_cidDefault resolves; plain picks carry
        // c_cidNone + UseDefault as the port's c_cidDefault marker, while
        // TK_TargetSelf / TK_TargetCommand carry c_cidNone + UseDefault false and
        // are sent with the model and the no-command id, exactly native SetTarget
        // (WinTrek.cpp:8827-8854).
        bool resolveDefault = t.CommandId == OrderChange.CidDefault
            || (t.CommandId == OrderChange.CidNone && t.UseDefault);
        if (!resolveDefault) return new(t.CommandId, type.Value, t.Id);
        var order = CommandOrderResolver.ResolveDefaultOrder(ownRoleBits, ownIsLayer, ownHasParent, profile);
        return order is { } o
            ? new(o.CommandId, o.ObjectType, o.ObjectId)
            : new(OrderChange.CidNone, type.Value, t.Id);
    }

    // HUD-8b — native SetTarget sends ORDER_CHANGE command=c_cmdCurrent
    // (WinTrek.cpp:8838-8844); the server writes Ship.CommandTarget, which drives
    // the lead predictor (indicator.cpp:199-360). The local Apply installs the
    // client-side MyCurrentCommand slot the red order arrow reads.
    private void SendCurrentCommandToServer(AutopilotTarget? target)
    {
        short? me;
        short ownRoles = 0;
        bool ownIsLayer = false, ownHasParent = false;
        CommandTargetProfile profile = default;
        lock (_session.Gate)
        {
            var world = _session.World;
            me = world.MyShipId;
            if (me is { } shipId)
            {
                if (world.Ships.TryGetValue(shipId, out var own))
                {
                    // Native GetDefaultOrder is evaluated on the SHIP (SetTarget,
                    // WinTrek.cpp:8830-8831): the own hull's role decides the
                    // default command, not the commandee's.
                    ownRoles = PlayerPilotRoles(ShipRoleBits(own.HullId));
                    ownIsLayer = false;   // a human is c_ptPlayer, never c_ptLayer
                    ownHasParent = own.ParentId != -1;
                }
                if (target is { } t) profile = BuildTargetProfile(world, t);
            }
        }
        if (me is not { } myId) return;
        // HUD-11 — native SetTarget resolves c_cidDefault through
        // GetDefaultOrder(pmodel); HUD-12b keeps the concrete id for default
        // picks and sends c_cidNone WITH the model for TK_TargetSelf /
        // TK_TargetCommand (WinTrek.cpp:8827-8854,10410,10442).
        SendCurrentCommand(myId, target, ownRoles, ownIsLayer, ownHasParent, profile,
            (shipId, commandId, command, objectType, objectId) =>
                _session.SendOrderChange(shipId, commandId, command, objectType, objectId));
    }

    /// <summary>HUD-12b — the send half of <c>SetTarget</c>, split out so a test
    /// can drive the exact payload through a recording sink. Native sends the
    /// model whenever one is selected, even with <c>c_cidNone</c>; only a null
    /// (or non-model cluster) target becomes <c>OT_invalid/NA</c>
    /// (<c>WinTrek.cpp:8846-8854</c>).</summary>
    internal static void SendCurrentCommand(
        short myId, AutopilotTarget? target, short ownRoles, bool ownIsLayer, bool ownHasParent,
        in CommandTargetProfile profile,
        Action<short, short, short, IgcObjectType, short> send)
    {
        if (CurrentCommandSetFor(target, ownRoles, ownIsLayer, ownHasParent, profile) is { } cmd)
            send(myId, cmd.CommandId, OrderChange.CmdCurrent, cmd.ObjectType, cmd.ObjectId);
        else
            send(myId, OrderChange.CidNone, OrderChange.CmdCurrent, (IgcObjectType)(-1), -1);
    }
}

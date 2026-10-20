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
            IgcObjectType.Probe => TargetKind.Probe, IgcObjectType.Treasure => TargetKind.Treasure, _ => null,
        };
        return kind is { } k ? new AutopilotTarget(k, c.TargetId, name, c.CommandId) : null;
    }
}

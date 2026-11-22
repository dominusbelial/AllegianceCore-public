using Allegiance.ClientCore.MonoGame.Flight;
using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.ClientCore.State;
using Allegiance.Core;
using Allegiance.Core.Wire;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

public sealed partial class Game1
{
    private Vector3 _browserTargetEye;
    private BrowserTargeting.Choice? _browserSelectedTarget;
    private double _browserLastMissile = double.NegativeInfinity;
    private RadarMode _browserRadarMode = RadarMode.Default;
    public object KeyboardMetrics => new
    {
        keyHelpOpen = KeyHelpOpen, keyHelpTopic = _browserHelpManual?.CurrentTopic,
        bindings = KeyBindingStore.Instance.Map.Definitions.Select(d => new { action = d.Name, key = KeyBindingStore.Instance.Map.LabelOf(d.Name) }).ToArray(),
        targetId = _browserSelectedTarget?.Id, targetType = _browserSelectedTarget?.Type.ToString(), targetName = _browserSelectedTarget?.Name,
        speed = _ownSim.Velocity.Length(), forward = _ownSim.Orientation.GetForward(), up = _ownSim.Orientation.GetUp(),
        cargoMount = _browserCargoMount, inventoryPartId = _browserInventoryPart?.PartId, inventoryMount = _browserInventoryPart?.MountId,
        roll = _ownSim.Controls.Roll, radarMode = _browserRadarMode.ToString(),
    };

    private void ApplyBrowserKeyboardActions(KeyboardState keys, ShipEntity own)
    {
        bool Edge(string action) => ActionDown(keys, action) && !ActionDown(_lastFlightKeys, action);
        var world = _session.World;
        if (Edge("TargetNothing")) { SelectBrowserTarget(null); _browserAutoTarget = null; }
        else
        {
            foreach (var def in InputActionCatalog.Definitions)
            {
                if (def.Category != "TARGETING / AUTOPILOT" || !def.Name.StartsWith("Target", StringComparison.Ordinal)
                    || def.Name is "TargetNothing" or "TargetSelf" or "TargetCommand" || !Edge(def.Name)) continue;
                var choices = BrowserTargeting.Gather(world, _shopCatalog!, own.ShipId);
                short roles = _shopCatalog?.Core?.HullTypes.GetValueOrDefault(own.HullId).HabmCapabilities ?? 0;
                var target = BrowserTargeting.Select(def.Name, choices, _browserSelectedTarget, _ownSim.Position,
                    own.SectorId, own.SideId, roles, world.AreAllied, ProjectBrowserTarget);
                if (target is not null) SelectBrowserTarget(target);
            }
        }
        if (Edge("ToggleAutopilot"))
        {
            if (_browserAutoTarget is not null) _browserAutoTarget = null;
            else
            {
                if (_browserSelectedTarget is null)
                {
                    var picked = BrowserTargeting.Select("TargetCenter", BrowserTargeting.Gather(world, _shopCatalog!, own.ShipId), null,
                        _ownSim.Position, own.SectorId, own.SideId, 0, world.AreAllied, ProjectBrowserTarget);
                    if (picked is not null) SelectBrowserTarget(picked);
                }
                if (_browserSelectedTarget is { } target) EngageAutopilotToward(ToAutopilot(target), "Autopilot to " + target.Name);
            }
        }
        // Native GetThrottle (WinTrek.cpp:10966-10974); use the same shared conversion as desktop.
        if (ActionDown(keys, "MatchSpeed") && _browserSelectedTarget is { } selected && _ownSim.HaveHull && _ownSim.MaxSpeed > 0)
        {
            float speed = selected.Type == IgcObjectType.Ship && world.Ships.TryGetValue(selected.Id, out var ship) && ship.Latest is { } pose ? pose.Velocity.Length() : 0;
            _throttle = FlightInput.MatchSpeedThrottle(speed, _ownSim.MaxSpeed.Value);
        }
        if (Edge("Throttle33")) _throttle = -.3f;
        if (Edge("Throttle66")) _throttle = .3f;
        if (ActionDown(keys, "FireMissile") && _nowSeconds - _browserLastMissile >= .25 && world.MissileLock.IsReady)
        { _session.SendFireMissile(); _browserLastMissile = _nowSeconds; }
        if (!ActionDown(keys, "FireMissile")) _browserLastMissile = _nowSeconds - .25;
        if (Edge("CycleRadar")) _browserRadarMode = RadarLod.Next(_browserRadarMode);
        if (Edge("CycleView") || Edge("ViewExternal")) ToggleContactView();
        if (Edge("CycleHudSkin")) _hudSettings.Style = _hudSettings.Style == HudStyle.Classic ? HudStyle.Modern : HudStyle.Classic;
        if (Edge("ScreenShot")) DownloadScreenshot?.Invoke();
    }

    private (float Sx, float Sy)? ProjectBrowserTarget(BrowserTargeting.Choice target)
    {
        // Use the exact eye and floating-origin view/projection the browser
        // rendered, including Watch/contact views. Same clipping and metric as
        // desktop Game1.Targeting.CollectTargetCandidates (P256/P580).
        var relative = PoseConverter.ToXna(target.Position) - _browserTargetEye;
        var clip = Vector4.Transform(new Vector4(relative, 1), _rockFrustum.Matrix);
        if (clip.W <= 1e-6f) return null;
        float sx = clip.X / clip.W, sy = clip.Y / clip.W;
        return MathF.Abs(sx) > 1 || MathF.Abs(sy) > 1 ? null : (sx, sy);
    }

    private static AutopilotTarget ToAutopilot(BrowserTargeting.Choice target) => new(target.Type switch
    {
        IgcObjectType.Ship => TargetKind.Ship, IgcObjectType.Station => TargetKind.Station,
        IgcObjectType.Asteroid => TargetKind.Asteroid, IgcObjectType.Warp => TargetKind.Warp, _ => TargetKind.Probe,
    }, target.Id, target.Name);

    private void SelectBrowserTarget(BrowserTargeting.Choice? target)
    {
        _browserSelectedTarget = target;
        if (_session.World.MyShipId is not { } ship) return;
        // Native SetTarget resolves c_cidDefault locally (WinTrek.cpp:8827-8854).
        // Source-share the desktop's complete native GetDefaultOrder table.
        short command = OrderChange.CidNone;
        if (target is not null && _session.World.Ships.TryGetValue(ship, out var own))
        {
            var kind = ToAutopilot(target).Kind;
            bool? friendly = target.Side is { } side ? side == own.SideId || _session.World.AreAllied(own.SideId, side) : null;
            var profile = new CommandTargetProfile(kind, target.Id, friendly,
                target.Type == IgcObjectType.Ship ? target.Roles : null,
                target.Type == IgcObjectType.Station ? target.Roles : null, null);
            short roles = _shopCatalog?.Core?.HullTypes.GetValueOrDefault(own.HullId).HabmCapabilities ?? 0;
            roles = (short)(roles & ~(DataHullTypeIGC.HabmMiner | DataHullTypeIGC.HabmBuilder));
            command = CommandOrderResolver.ResolveDefaultOrder(roles, false, own.ParentId != -1, profile)?.CommandId ?? OrderChange.CidNone;
        }
        _session.SendOrderChange(ship, command, OrderChange.CmdCurrent, target?.Type ?? (IgcObjectType)(-1), target?.Id ?? -1);
    }
}

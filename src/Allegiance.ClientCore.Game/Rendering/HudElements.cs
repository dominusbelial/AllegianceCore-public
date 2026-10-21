namespace Allegiance.ClientCore.MonoGame.Rendering;

public enum HudAnchor { Center, Top, TopLeft, TopRight, Bottom, BottomLeft, BottomRight, Left, Right, Projected }

/// <summary>Native pane coordinates (+Y up) or projected screen coordinates
/// (+Y down). No bitmap dimensions or skin scale are part of this contract.</summary>
public readonly record struct HudPlacement(HudAnchor Anchor, float X, float Y, bool YUp = true);

/// <summary>A native element's per-frame availability and semantic payload.
/// Unported/unknown values stay null and invisible, so another renderer cannot
/// accidentally present invented zero readings. Rich contact/target/weapon
/// payloads remain in HudContext/HudFrame rather than being flattened to text.</summary>
public readonly record struct HudElement(string Id, bool Available, bool Visible,
    HudPlacement? Placement, float? Value = null, string? Text = null, HudColor? Color = null, HudPoint? ScreenPosition = null);

public static class HudElements
{
    // Native source/units/visibility provenance for each ID is recorded in
    // HUD1_assets/inventory.json and the hud rows of parity-matrix.csv.
    private static readonly (string Id, HudPlacement? Placement)[] Native =
    [
        ("Hull integrity", new(HudAnchor.Center, -65.5f, 35.5f, false)),
        ("Shield charge", new(HudAnchor.Center, 65.5f, 35.5f, false)),
        ("Energy reserve", new(HudAnchor.Center, 126.5f, -112.5f, false)),
        ("Fuel reserve", new(HudAnchor.Center, 126.5f, 93.5f, false)),
        ("Ammunition reserve", new(HudAnchor.Center, -126.5f, -112.5f, false)),
        ("Thrust setting", new(HudAnchor.Center, -126.5f, 93.5f, false)),
        ("Speed", new(HudAnchor.Center, -39f, -88f, true)),
        ("Kill bonus", new(HudAnchor.Center, -39f, -138f, true)),
        ("Roll tutorial cue", new(HudAnchor.Center, -110f, -170f, true)),
        ("Signature percentage", new(HudAnchor.Center, -39f, -107f, true)),
        ("Detection eye", new(HudAnchor.Center, 70f, -185f, true)),
        ("Cloak active", new(HudAnchor.Center, -200f, -160f, true)),
        ("Cloak strength and drained state", null),
        ("Autopilot status", new(HudAnchor.Center, -143f, 102f, true)),
        ("Vector lock status", new(HudAnchor.Center, 122f, 97f, true)),
        ("Carried flag", new(HudAnchor.Center, 107f, 110f, true)),
        ("Carried artifact", new(HudAnchor.Center, -161f, 110f, true)),
        ("Target name", new(HudAnchor.Bottom, -80f, 150f, true)),
        ("Target type description", null),
        ("Target side identity", null),
        ("Target hull", new(HudAnchor.Bottom, -85f, 58f, true)),
        ("Target shield", new(HudAnchor.Bottom, 47f, 58f, true)),
        ("Target range or sector", new(HudAnchor.Bottom, 80f, 58f, true)),
        ("Target speed", new(HudAnchor.Bottom, -228f, 58f, true)),
        ("Target ore", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Target kill bonus", new(HudAnchor.Bottom, -40f, 135f, true)),
        ("Target in weapon range", new(HudAnchor.Bottom, -120f, 115f, true)),
        ("Target model preview", new(HudAnchor.Bottom, 270f, 9f, true)),
        ("Target time to intercept", new(HudAnchor.Bottom, 242f, 10f, true)),
        ("Cockpit centre reticle", new(HudAnchor.Center, 0f, 0f, true)),
        ("Velocity direction marker", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Joystick turning indicator", null),
        ("Ballistic lead marker", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Aim hit-probability predictor", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Chase weapon aim without target", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Accepted and current order turn arrows", null),
        ("Selected target bracket", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Missile lock flower", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Training HUD callouts", null),
        ("Radar viewport and round border", null),
        ("Radar ship contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar station contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar warp contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar asteroid contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar missile contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar probe and mine contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar treasure and tech contacts", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar accepted order brackets", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar queued order brackets", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar nearest enemy bracket", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar threat damage flash", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar command subjects", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar carried flag marker", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar carried artifact marker", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar ripcord marker", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar construction highlight", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar name labels", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar range labels", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar hull and shield bars", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar ore and miner fill bars", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar friendly detection eyes", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar friendly ripcord seconds", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar command verb labels", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar LOD modes", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Radar range fade", new(HudAnchor.Projected, 0f, 0f, true)),
        ("Sector location", new(HudAnchor.TopRight, -180f, -37f, true)),
        ("Turret carrier identity", new(HudAnchor.TopRight, -180f, -75f, true)),
        ("Observer and turret board state", null),
        ("Accepted order text", new(HudAnchor.TopLeft, 50f, -37f, true)),
        ("Queued order text", new(HudAnchor.TopLeft, 50f, -37f, true)),
        ("Chat history", new(HudAnchor.Top, -160f, -126f, true)),
        ("Chat composition and recipient", new(HudAnchor.Bottom, -150f, 148f, true)),
        ("Command recipient verb and wing controls", new(HudAnchor.Bottom, -135f, 40f, true)),
        ("Tip text lines", null),
        ("Critical tip text lines", null),
        ("Vote banner", null),
        ("Money credits", null),
        ("Lives remaining", new(HudAnchor.TopLeft, 40f, -97f, true)),
        ("Mission kills", new(HudAnchor.TopLeft, 40f, -177f, true)),
        ("Mission countdown", new(HudAnchor.TopLeft, 40f, -137f, true)),
        ("Ripcord countdown", new(HudAnchor.Center, -75f, 15f, true)),
        ("Eject pod identity panel", new(HudAnchor.Center, -86f, -87f, true)),
        ("Lifepod oxygen endurance", new(HudAnchor.Center, -20f, -77f, true)),
        ("Death banner", new(HudAnchor.Center, -105f, 15f, true)),
        ("Boundary and crowd warnings", null),
        ("Incoming missile warning", null),
        ("Damage flash timer", null),
        ("HUD style and software layouts", null),
        ("Console pane visibility and slide animation", null),
        ("HUD base class contract", null),
        ("Game list screen boundary", null),
        ("Flight equipment inventory pane", new(HudAnchor.BottomRight, -36f, 40f)),
        ("Equipment part identity and key", null),
        ("Equipment selected and active state", null),
        ("Equipment quantity and empty warning", null),
        ("Equipment mounting progress", null),
        ("Launcher arming progress", null),
        ("Equipment weapon range", null),
        ("Equipment shot damage", null),
        ("Equipment burst rate", null),
        ("Equipment shield strength", null),
        ("Equipment shield recharge seconds", null),
        ("Equipment afterburner top speed", null),
        ("Equipment afterburner endurance", null),
        ("Equipment cloak endurance", null),
        ("Inventory turret gunner identity", null),
        ("Inventory cargo slots", null),
        ("Inventory own ship name", null),
        ("Equipment empty slot affordances", null),
        ("Command view ship summary", new(HudAnchor.Left, 36f, 120f)),
    ];

    public static IReadOnlyList<HudElement> Build(HudModel model)
    {
        var result = new List<HudElement>(Native.Length);
        var f = model.Telemetry;
        var c = model.Context;
        // HUD-11 — count every radar overlay/label row in ONE pass over the
        // contacts (HUD-10 ran nine separate Count(...) walks plus an uncached
        // RadarLabelModel.Resolve per contact). The label decision reuses the
        // HUD-5b cache when the caller supplied snapshot buffers, so a steady
        // frame allocates no RadarLabel/string per contact.
        int acceptedOrders = 0, queuedOrders = 0, nearestEnemy = 0, threatFlash = 0;
        int subjects = 0, carriedFlag = 0, carriedArtifact = 0, ripcord = 0, highlight = 0;
        int shipContacts = 0, stationContacts = 0, warpContacts = 0, asteroidContacts = 0;
        int missileContacts = 0, probeMineContacts = 0, treasureTechContacts = 0;
        int nameLabels = 0, rangeLabels = 0, hullShieldBars = 0, fillBars = 0, eyes = 0, ripSeconds = 0, verbLabels = 0;
        var labels = model.RadarLabels;
        for (int i = 0; i < c.Contacts.Count; i++)
        {
            var contact = c.Contacts[i];
            if (contact.AcceptedOrder) acceptedOrders++;
            if (contact.QueuedOrder) queuedOrders++;
            if (contact.NearestEnemy) nearestEnemy++;
            if (contact.Threat || contact.ThreatFlash) threatFlash++;
            if (contact.Subject) subjects++;
            if (contact.CarriedFlag) carriedFlag++;
            if (contact.CarriedArtifact) carriedArtifact++;
            if (contact.Ripcord) ripcord++;
            if (contact.Highlight) highlight++;
            switch (contact.Kind)
            {
                case HudContactKind.Ship: shipContacts++; break;
                case HudContactKind.Station: stationContacts++; break;
                case HudContactKind.Warp: warpContacts++; break;
                case HudContactKind.Asteroid: asteroidContacts++; break;
                case HudContactKind.Missile: missileContacts++; break;
                case HudContactKind.Probe: case HudContactKind.Mine: probeMineContacts++; break;
                case HudContactKind.Treasure: case HudContactKind.Tech: treasureTechContacts++; break;
            }
            var label = labels is { } cache
                ? cache.Resolve(i, contact, c.RadarMode, c.CommandView).Label
                : RadarLabelModel.Resolve(contact, c.RadarMode, c.CommandView);
            if (label is not { } l) continue;
            if (l.Name is not null) nameLabels++;
            if (l.RangeText is not null) rangeLabels++;
            if (l.Hull is not null || l.Shield is not null) hullShieldBars++;
            if (l.Fill is not null) fillBars++;
            if (l.Eye) eyes++;
            if (l.RipcordSeconds is not null) ripSeconds++;
            if (l.CommandId is not null) verbLabels++;
        }
        var console = c.Console;
        foreach (var (id, placement) in Native)
        {
            var element = new HudElement(id, false, false, placement);
            GaugeKind? kind = id switch
            {
                "Hull integrity" => GaugeKind.Hull, "Shield charge" => GaugeKind.Shield,
                "Energy reserve" => GaugeKind.Energy, "Fuel reserve" => GaugeKind.Fuel,
                "Ammunition reserve" => GaugeKind.Ammo, "Thrust setting" => GaugeKind.Thrust,
                _ => null
            };
            if (kind is { } gaugeKind && f is not null)
            {
                var g = model.Gauges.Single(g => g.Kind == gaugeKind);
                element = element with { Available = true, Visible = g.Visible, Value = g.Fraction, Color = g.Color };
            }
            else
            {
                bool instrument = model.InstrumentsVisible && f is not null;
                var status = model.Status;
                switch (id)
                {
                    case "Speed": Set(f?.Speed, instrument, model.SpeedUnit); break;
                    case "Kill bonus": Set(status.KillBonusPercent, instrument && status.KillBonusPercent is not null); break;
                    case "Signature percentage": Set(status.SignaturePercent, instrument && status.SignaturePercent is not null); break;
                    case "Detection eye": Set(status.DetectionEyeVisible ? 1 : 0, instrument && status.DetectionEyeVisible); break;
                    case "Cloak active": Set(status.CloakActive ? 1 : 0, instrument && status.CloakActive); break;
                    case "Cloak strength and drained state": Set(status.CloakStrengthPercent ?? (status.CloakDrained ? 1 : 0), instrument && (status.CloakActive && status.CloakStrengthPercent is not null || status.CloakDrained)); break;
                    case "Autopilot status": Set(status.AutopilotEngaged ? 1 : 0, instrument && status.AutopilotEngaged); break;
                    case "Vector lock status": Set(status.VectorLockEngaged ? 1 : 0, instrument && status.VectorLockEngaged); break;
                    case "Carried flag": Set(status.CarriedFlag ? 1 : 0, instrument && status.CarriedFlag); break;
                    case "Carried artifact": Set(status.CarriedArtifact ? 1 : 0, instrument && status.CarriedArtifact); break;
                    case "Target name": Pane(c.TargetPane?.Name); break;
                    case "Target type description": Pane(c.TargetPane?.TypeDescription); break;
                    case "Target side identity": Pane(c.TargetPane?.SideName, color: c.TargetPane?.SideColor); break;
                    case "Target hull": Pane(null, c.TargetPane?.HullFraction); break;
                    // HUD-18 — RANGE:n when a distance resolved, else the native
                    // LOCATION:<sector> fallback (dialog.mdl:4053-4060); the row is
                    // present when either text is available.
                    case "Target range or sector":
                        Pane(c.TargetPane is { Distance: null, SectorName: { } sector }
                            ? HudStringCache.Location(sector) : null,
                            c.TargetPane?.Distance);
                        break;
                    case "Target shield": Pane(null, c.TargetPane?.ShieldFraction); break;
                    case "Target speed": Pane(null, c.TargetPane?.Speed); break;
                    case "Target kill bonus": Pane(null, c.TargetPane?.KillBonusPercent); break;
                    case "Target in weapon range": Pane(null, c.TargetPane is { } tp ? (tp.InRange ? 1 : 0) : null); break;
                    case "Target time to intercept":
                        Pane(c.TargetPane?.TimeToIntercept is { } tti ? HudStringCache.Tti(tti) : null);
                        break;
                    case "Target ore": Target(null, c.Target?.OreFraction); break;
                    case "Cockpit centre reticle": element = element with { Available = true, Visible = model.ReticleVisible }; break;
                    case "Velocity direction marker":
                        Set(f?.Speed, model.VelocityMarkerVisible);
                        if (c.VelocityMarker is { } velocityMarker) element = element with { ScreenPosition = velocityMarker };
                        break;
                    case "Joystick turning indicator":
                        if (f is { } turningTelemetry) element = element with
                        {
                            Available = true,
                            Visible = model.JoystickIndicatorVisible,
                            Value = MathF.Min(1f, MathF.Sqrt(turningTelemetry.TurnYaw * turningTelemetry.TurnYaw + turningTelemetry.TurnPitch * turningTelemetry.TurnPitch))
                        };
                        break;
                    case "Ballistic lead marker":
                        Set(c.Lead?.Green, model.LeadMarkerVisible);
                        if (c.Lead is { } leadMarker) element = element with { ScreenPosition = leadMarker.Center };
                        break;
                    case "Aim hit-probability predictor":
                        Set(c.Lead?.Green, model.AimPredictorVisible);
                        if (c.Lead is { } aimLead) element = element with { ScreenPosition = c.Camera == CameraMode.Cockpit ? null : aimLead.Center };
                        break;
                    case "Chase weapon aim without target":
                        Set(c.ChaseAim is not null ? 1 : null, model.ChaseAimVisible);
                        if (c.ChaseAim is { } chaseAim) element = element with { ScreenPosition = chaseAim };
                        break;
                    case "Accepted and current order turn arrows":
                        element = element with
                        {
                            Available = c.OrderArrows is not null,
                            Visible = model.OrderArrowsVisible,
                            Value = c.OrderArrows is { } oa && (oa.AcceptedDirection is not null || oa.CurrentDirection is not null) ? 1 : null,
                            Text = c.OrderArrows is { } orderArrowsText ? (orderArrowsText.SameTarget ? "accepted=current" : "accepted+current") : null
                        };
                        break;
                    case "Selected target bracket": Target(null); break;
                    case "Missile lock flower":
                        Set(c.Lock is { } l ? (float)l.State : null,
                            model.IndicatorsVisible && c.Lock is { State: not Allegiance.ClientCore.State.MissileLockFeedback.LockState.None });
                        if (c.Lock is { } targetLock) element = element with { ScreenPosition = targetLock.Center };
                        break;
                    case "Radar viewport and round border":
                        Set(c.RoundRadar ? 1 : 0, model.FlightVisible); break;
                    case "Radar LOD modes":
                        Set((float)c.RadarMode, model.FlightVisible); break;
                    case "Radar ship contacts": Set(shipContacts, model.FlightVisible); break;
                    case "Radar station contacts": Set(stationContacts, model.FlightVisible); break;
                    case "Radar warp contacts": Set(warpContacts, model.FlightVisible); break;
                    case "Radar asteroid contacts": Set(asteroidContacts, model.FlightVisible); break;
                    case "Radar missile contacts": Set(missileContacts, model.FlightVisible); break;
                    case "Radar probe and mine contacts": Set(probeMineContacts, model.FlightVisible); break;
                    case "Radar treasure and tech contacts": Set(treasureTechContacts, model.FlightVisible); break;
                    case "Radar accepted order brackets": Set(acceptedOrders, model.FlightVisible && acceptedOrders > 0); break;
                    case "Radar queued order brackets": Set(queuedOrders, model.FlightVisible && queuedOrders > 0); break;
                    case "Radar nearest enemy bracket": Set(nearestEnemy, model.FlightVisible && nearestEnemy > 0); break;
                    case "Radar threat damage flash": Set(threatFlash, model.FlightVisible && threatFlash > 0); break;
                    case "Radar command subjects": Set(subjects, model.FlightVisible && subjects > 0); break;
                    case "Radar carried flag marker": Set(carriedFlag, model.FlightVisible && carriedFlag > 0); break;
                    case "Radar carried artifact marker": Set(carriedArtifact, model.FlightVisible && carriedArtifact > 0); break;
                    case "Radar ripcord marker": Set(ripcord, model.FlightVisible && ripcord > 0); break;
                    case "Radar construction highlight": Set(highlight, model.FlightVisible && highlight > 0); break;
                    case "Radar name labels": Set(nameLabels, model.FlightVisible && nameLabels > 0); break;
                    case "Radar range labels": Set(rangeLabels, model.FlightVisible && rangeLabels > 0); break;
                    case "Radar hull and shield bars": Set(hullShieldBars, model.FlightVisible && hullShieldBars > 0); break;
                    case "Radar ore and miner fill bars": Set(fillBars, model.FlightVisible && fillBars > 0); break;
                    case "Radar friendly detection eyes": Set(eyes, model.FlightVisible && eyes > 0); break;
                    case "Radar friendly ripcord seconds": Set(ripSeconds, model.FlightVisible && ripSeconds > 0); break;
                    case "Radar command verb labels": Set(verbLabels, model.FlightVisible && verbLabels > 0); break;
                    case "Tip text lines": Notices(false); break;
                    case "Critical tip text lines": Notices(true); break;
                    case "Sector location":
                        element = element with { Available = console?.SectorName is not null,
                            Visible = model.FlightVisible && console?.SectorName is not null, Text = console?.SectorName }; break;
                    case "Turret carrier identity":
                        element = element with { Available = console is { BoardState: 2, CarrierName: not null },
                            Visible = model.FlightVisible && console is { BoardState: 2, CarrierName: not null },
                            Text = console is { BoardState: 2 } ? console.CarrierName : null }; break;
                    case "Observer and turret board state":
                        // Native BoardTextImage is loadout-only (dialog.mdl:2106); the
                        // value is exposed in the model, not drawn in flight.
                        element = element with { Available = console is not null, Visible = false,
                            Value = console?.BoardState, Text = console is { } bt ? HudConsoleFormat.BoardLabel(bt.BoardState, bt.CarrierName) : null }; break;
                    case "Accepted order text":
                        element = element with { Available = console?.AcceptedOrder is not null,
                            Visible = model.Visible && console?.AcceptedOrder is not null, Text = console?.AcceptedOrder }; break;
                    case "Queued order text":
                        element = element with { Available = console?.QueuedOrder is not null,
                            Visible = model.Visible && console?.QueuedOrder is not null, Text = console?.QueuedOrder }; break;
                    case "Vote banner":
                        element = element with { Available = console?.VoteText is not null,
                            Visible = model.Visible && console?.VoteText is not null, Text = console?.VoteText }; break;
                    case "Lives remaining": Set(console?.Lives is { } lives ? lives : null, model.FlightVisible && console?.Lives is not null); break;
                    case "Mission kills": Set(console?.MissionKills is { } kills ? kills : null, model.FlightVisible && console?.MissionKills is not null); break;
                    case "Chat history":
                        element = element with { Available = c.Chat is not null, Visible = model.Visible && c.Chat?.Lines.Count > 0, Text = c.Chat is { } chat ? string.Join("\n", chat.Lines) : null }; break;
                    case "Chat composition and recipient":
                        element = element with { Available = c.Chat is not null, Visible = model.Visible && c.Chat?.Composing == true, Text = c.Chat is { } compose ? compose.Recipient + compose.Input : null }; break;
                    case "Flight equipment inventory pane":
                        Inv(c.InventoryPane?.ShipName, c.InventoryPane is not null); break;
                    case "Equipment part identity and key":
                        InvText(Join(Rows().Where(r => r.Key.Length > 0).Select(r => $"{r.Key} {r.Name}"))); break;
                    case "Equipment selected and active state":
                        InvText(Join(Rows().Where(r => r.Selected || r.Active).Select(r => $"{r.Key}{r.Name}"))); break;
                    case "Equipment quantity and empty warning":
                        InvText(Join(Rows().Where(r => r.OutOfAmmo || r.Quantity is not null)
                            .Select(r => r.OutOfAmmo ? $"A {r.Name}" : $"{r.Name} x{r.Quantity:0.####}"))); break;
                    case "Equipment mounting progress":
                    {
                        var row = Rows().FirstOrDefault(r => r.ReadyState == 1);
                        Inv(row.StatsText, row.ReadyState == 1, row.MountedFraction); break;
                    }
                    case "Launcher arming progress":
                    {
                        var row = Rows().FirstOrDefault(r => r.ReadyState == 2);
                        Inv(row.StatsText, row.ReadyState == 2, row.ArmedFraction); break;
                    }
                    case "Equipment weapon range":
                        InvText(Join(Rows().Where(r => r.IsWeapon).Select(r => r.StatsText))); break;
                    case "Equipment shot damage":
                        InvText(Join(Rows().Where(r => r.IsWeapon).Select(r => r.StatsText))); break;
                    case "Equipment burst rate":
                        InvText(Join(Rows().Where(r => r.IsWeapon).Select(r => r.StatsText))); break;
                    case "Equipment shield strength":
                        InvText(Join(Rows().Where(r => r.Kind == HudInventoryRowKind.Shield).Select(r => r.StatsText))); break;
                    case "Equipment shield recharge seconds":
                        InvText(Join(Rows().Where(r => r.Kind == HudInventoryRowKind.Shield).Select(r => r.StatsText))); break;
                    case "Equipment afterburner top speed":
                        InvText(Join(Rows().Where(r => r.Kind == HudInventoryRowKind.Afterburner).Select(r => r.StatsText))); break;
                    case "Equipment afterburner endurance":
                        InvText(Join(Rows().Where(r => r.Kind == HudInventoryRowKind.Afterburner).Select(r => r.StatsText))); break;
                    case "Equipment cloak endurance":
                        InvText(Join(Rows().Where(r => r.Kind == HudInventoryRowKind.Cloak).Select(r => r.StatsText))); break;
                    case "Inventory turret gunner identity":
                        Inv(null, false); break;
                    case "Inventory cargo slots":
                        InvText(Join(Rows().Where(r => r.Kind is HudInventoryRowKind.Cargo or HudInventoryRowKind.EmptyCargo)
                            .Select(r => r.Quantity is { } q ? $"{r.Key}{r.Name} x{q:0.####}" : $"{r.Key}{r.Name}"))); break;
                    case "Inventory own ship name":
                        Inv(c.InventoryPane?.ShipName, !string.IsNullOrEmpty(c.InventoryPane?.ShipName)); break;
                    case "Equipment empty slot affordances":
                        InvText(Join(Rows().Where(r => r.IsEmpty).Select(r => $"{r.Key}{r.Name}"))); break;
                    case "Command view ship summary":
                        element = element with { Available = c.CommandLines.Count > 0,
                            Visible = model.Visible && c.CommandLines.Count > 0, Text = string.Join("\n", c.CommandLines) }; break;
                    // HUD-10 — moneyPane lives only in the F5 investment pane
                    // (dialog.mdl:5199-5203,5368), so the flight HUD has no
                    // always-visible credits readout.
                    case "Money credits": Set(f?.Money, false); break;
                    case "Ripcord countdown":
                        element = element with { Available = c.Ripcord is not null, Visible = instrument && c.Ripcord is not null, Text = c.Ripcord }; break;
                    case "Eject pod identity panel": Set(f is not null ? (model.PodVisible ? 1 : 0) : null, model.PodVisible); break;
                    case "Lifepod oxygen endurance": Set(f?.FuelFraction, model.PodVisible); break;
                    case "Death banner": element = element with { Available = true, Visible = model.DeathVisible, Value = c.Dead ? 1 : 0 }; break;
                    case "Boundary and crowd warnings":
                        element = element with { Available = f is not null, Visible = model.Warnings.Count > 0, Text = string.Join(" / ", model.Warnings) }; break;
                    case "Damage flash timer": Set(f is { } flash ? (flash.Flashing ? 1 : 0) : null, instrument && f?.Flashing == true); break;
                }
            }
            result.Add(element);

            void Set(float? value, bool visible, string? text = null) => element = element with
            { Available = value is not null, Visible = visible, Value = value, Text = text };
            void Target(string? text, float? value = null)
            {
                bool available = c.Target is not null && (id == "Selected target bracket" || text is not null || value is not null);
                element = element with { Available = available, Visible = model.FlightVisible && available, Value = value, Text = text };
                if (c.Target is { } t) element = element with { ScreenPosition = t.Center };
            }
            void Pane(string? text, float? value = null, HudColor? color = null)
            {
                bool available = c.TargetPane is { Present: true } && (text is not null || value is not null);
                element = element with { Available = available, Visible = model.TargetPaneVisible && available, Text = text, Value = value, Color = color };
            }
            void Notices(bool critical)
            {
                if (critical)
                {
                    var tips = console?.CriticalTips ?? Array.Empty<HudNotice>();
                    element = element with { Available = tips.Count > 0, Visible = model.Visible && tips.Count > 0,
                        Text = string.Join("\n", tips.Select(t => t.Text)) };
                }
                else
                {
                    var tips = console?.OrdinaryTips ?? Array.Empty<string>();
                    element = element with { Available = tips.Count > 0, Visible = model.Visible && tips.Count > 0,
                        Text = string.Join("\n", tips) };
                }
            }
            bool InvVisible() => model.FlightVisible && c.InventoryVisible && c.InventoryPane is { Rows.Count: > 0 };
            IReadOnlyList<HudInventoryRow> Rows() => c.InventoryPane?.Rows ?? Array.Empty<HudInventoryRow>();
            void Inv(string? text, bool available, float? value = null) => element = element with
            { Available = available, Visible = InvVisible() && available, Text = text, Value = value };
            void InvText(string? text) => Inv(text, text is not null);
            static string? Join(IEnumerable<string?> parts)
            {
                var present = parts.Where(p => p is not null).ToArray();
                return present.Length == 0 ? null : string.Join("; ", present);
            }
        }
        return result.AsReadOnly();
    }
}

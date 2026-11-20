using Allegiance.ClientCore.MonoGame.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Local-skirmish spectator sector travel: Home/End step through
/// sectors, Tab opens a clickable sector map, and the free camera flown into an
/// aleph comes out in the linked sector (see <see cref="SpectatorSectorTravel"/>).</summary>
public sealed partial class Game1
{
    private bool _spectatorMapOpen;
    private StarmapLayout? _spectatorMapLayout;
    private ButtonState _spectatorMapPrevLeft = ButtonState.Released;

    /// <summary>Sector keys and map clicks; runs in the spectator frame when no
    /// menu or overlay is up.</summary>
    private void HandleSpectatorSectorInput(Func<string, bool> edge)
    {
        if (edge("ToggleStarmap")) _spectatorMapOpen = !_spectatorMapOpen;
        int step = edge("SpectatorNextSector") ? 1 : edge("SpectatorPreviousSector") ? -1 : 0;
        if (step != 0)
        {
            short[] sectors;
            lock (_session.Gate) sectors = _session.World.Clusters.Keys.ToArray();
            SpectatorGoToSector(SpectatorSectorTravel.CycleSector(sectors, _skirmishCamera.SectorId, step));
        }
        var mouse = Mouse.GetState();
        bool click = mouse.LeftButton == ButtonState.Pressed && _spectatorMapPrevLeft == ButtonState.Released;
        _spectatorMapPrevLeft = mouse.LeftButton;
        if (_spectatorMapOpen && click && IsActive && _spectatorMapLayout is { } layout)
        {
            short picked = StarmapHitTest.HitTest(layout, new System.Numerics.Vector2(mouse.X, mouse.Y));
            if (picked >= 0) { SpectatorGoToSector(picked); _spectatorMapOpen = false; }
        }
    }

    private void SpectatorGoToSector(short sector)
    {
        if (sector < 0) return;
        SpectatorSectorTravel.Arrival arrival;
        lock (_session.Gate)
        {
            if (!_session.World.Clusters.ContainsKey(sector)) return;
            arrival = SpectatorSectorTravel.ArrivalView(sector, _session.World.Stations, _session.World.Warps);
        }
        ApplySpectatorArrival(arrival, "picked");
    }

    /// <summary>The free camera flown into an aleph comes out of the destination
    /// aleph in the linked sector, facing out of it.</summary>
    private void CrossSpectatorAleph()
    {
        if (!_freeCamActive || _skirmishCamera.SectorId < 0) return;
        bool crossed;
        SpectatorSectorTravel.Arrival arrival;
        lock (_session.Gate)
            crossed = SpectatorSectorTravel.TryCrossAleph(_freeCam.Position, _skirmishCamera.SectorId, _session.World.Warps, out arrival);
        if (crossed) ApplySpectatorArrival(arrival, "aleph");
    }

    private void ApplySpectatorArrival(SpectatorSectorTravel.Arrival arrival, string how)
    {
        _skirmishCamera.GoToSector(arrival.SectorId);
        _freeCamActive = true;
        _orbitCamActive = false;
        _cameraMode = CameraMode.FreeCam;
        _freeCam.Teleport(arrival.Eye);
        (_freeCam.Yaw, _freeCam.Pitch) = SpectatorSectorTravel.YawPitch(arrival.Forward);
        _observerSampleAt = double.NegativeInfinity;   // observe the new sector next frame
        Console.WriteLine($"[skirmish] spectator camera -> sector {arrival.SectorId} ({how})");
    }

    /// <summary>Tab sector map: the mission's sectors at their authored map
    /// positions, aleph links, ship counts; the viewed sector highlighted.</summary>
    private void DrawSpectatorSectorMap()
    {
        if (!LocalSpectating || !_spectatorMapOpen) { _spectatorMapLayout = null; return; }
        var sites = new List<StarmapLayout.ClusterSite>();
        var links = new HashSet<(short, short)>();
        var counts = new Dictionary<short, int>();
        lock (_session.Gate)
        {
            var world = _session.World;
            foreach (var (id, c) in world.Clusters) sites.Add(new StarmapLayout.ClusterSite(id, c.Name, c.ScreenX, c.ScreenY));
            foreach (var wp in world.Warps.Values)
                if (world.Warps.TryGetValue(wp.DestinationId, out var d) && d.ClusterId != wp.ClusterId)
                    links.Add(wp.ClusterId < d.ClusterId ? (wp.ClusterId, d.ClusterId) : (d.ClusterId, wp.ClusterId));
            foreach (var ship in world.Ships.Values) counts[ship.SectorId] = counts.GetValueOrDefault(ship.SectorId) + 1;
        }
        sites.Sort((a, b) => a.Id.CompareTo(b.Id));
        var vp = GraphicsDevice.Viewport;
        float w = MathF.Min(vp.Width * 0.6f, 960f), h = MathF.Min(vp.Height * 0.62f, 720f);
        float x = (vp.Width - w) / 2f, y = (vp.Height - h) / 2f;
        var layout = StarmapLayout.Fit(sites, x, y + 30f, w, h - 30f, 60f);
        _spectatorMapLayout = layout;

        _spriteBatch.Begin();
        _spriteBatch.Draw(_whitePixel, new Rectangle((int)x, (int)y, (int)w, (int)h), new Color(0, 0, 0, 215));
        _spriteBatch.DrawString(_hudSmall, "Sectors - click to go there   Home/End next/previous   Tab close", new Vector2(x + 14f, y + 10f), Color.Cyan);
        foreach (var (a, b) in links)
            if (layout.TryGetPoint(a, out var pa) && layout.TryGetPoint(b, out var pb))
                DrawSpectatorMapLine(new Vector2(pa.X, pa.Y), new Vector2(pb.X, pb.Y), new Color(90, 110, 130));
        foreach (var site in sites)
        {
            if (!layout.TryGetPoint(site.Id, out var p)) continue;
            bool here = site.Id == _skirmishCamera.SectorId;
            int size = here ? 14 : 10;
            _spriteBatch.Draw(_whitePixel, new Rectangle((int)p.X - size / 2, (int)p.Y - size / 2, size, size), here ? Color.Cyan : Color.LightGray);
            string label = counts.TryGetValue(site.Id, out int n) && n > 0 ? $"{site.Name} ({n})" : site.Name;
            label = FontText.Safe(_hudSmall, label);
            var sz = _hudSmall.MeasureString(label);
            _spriteBatch.DrawString(_hudSmall, label, new Vector2(p.X - sz.X / 2f, p.Y + 10f), here ? Color.Cyan : Color.White);
        }
        _spriteBatch.End();
    }

    private void DrawSpectatorMapLine(Vector2 a, Vector2 b, Color color)
    {
        var d = b - a;
        float length = d.Length();
        if (length < 1f) return;
        _spriteBatch.Draw(_whitePixel, a, null, color, MathF.Atan2(d.Y, d.X), Vector2.Zero, new Vector2(length, 2f), SpriteEffects.None, 0f);
    }
}

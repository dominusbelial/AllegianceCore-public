using Allegiance.ClientCore.MonoGame.Rendering;
using Allegiance.Core;
using Microsoft.Xna.Framework;

namespace Allegiance.ClientCore.MonoGame;

/// <summary>Advance one sector clock in Update; evaluate a rock's angle only
/// after its sphere passes culling. Native rate × elapsed simulation seconds
/// (Igc/modelIGC.cpp:85-99) preserves phase while a rock is off screen.</summary>
public sealed class BrowserRockSpins
{
    private readonly Dictionary<short, double> _seconds = new();
    private readonly Dictionary<short, (AsteroidSpin Spin, short Sector, double Epoch)> _rocks = new();
    public void Register(DataAsteroidIGC rock)
    {
        if (!_rocks.ContainsKey(rock.AsteroidId))
            _rocks[rock.AsteroidId] = (AsteroidSpin.FromWire(rock.Rotation), rock.ClusterId, _seconds.GetValueOrDefault(rock.ClusterId));
    }
    public void Advance(short sector, double simulationSeconds) => _seconds[sector] = _seconds.GetValueOrDefault(sector) + simulationSeconds;
    public float Angle(short id)
    {
        var rock = _rocks[id];
        return (float)(rock.Spin.Rate * (_seconds.GetValueOrDefault(rock.Sector) - rock.Epoch));
    }
    public Matrix Rotation(short id)
    {
        var spin = _rocks[id].Spin;
        return Matrix.CreateFromAxisAngle(new Vector3(spin.Axis.X, spin.Axis.Y, spin.Axis.Z), Angle(id));
    }
    public void Clear() { _seconds.Clear(); _rocks.Clear(); }
}

namespace Allegiance.ClientCore.MonoGame.Flight;

/// <summary>
/// The per-mounted-system status the weapon-status HUD list shows. Mirrors
/// native's real "Reloading ammo..." / "Ammo depleted." distinction (P361):
/// <see cref="Ready"/> = the weapon can fire (ammo available, or an energy /
/// non-ammo system that never consumes the pool); <see cref="Reloading"/> = an
/// auto-reload attempt is in flight (either a successful RELOAD(129) relay was
/// just seen — native's "Reloading ammo..." — or the magazine has emptied and a
/// reload is being awaited); <see cref="Depleted"/> = the magazine is at zero and
/// the auto-reload attempt FAILED (no spare pack in cargo) — native's "Ammo
/// depleted." case.
/// </summary>
public enum WeaponStatus
{
    /// <summary>Can fire: ammo &gt; 0, or a system that does not consume the ammo pool.</summary>
    Ready,

    /// <summary>Part is mounting, or its magazine load deadline has not passed.</summary>
    NotReady,

    /// <summary>An auto-reload attempt is in flight (RELOAD relay seen, or magazine just emptied).</summary>
    Reloading,

    /// <summary>Magazine at zero, the auto-reload attempt failed (no spare in cargo). Native's "Ammo depleted."</summary>
    Depleted,
}

/// <summary>Identity + ammo semantics of one mounted system, fed to
/// <see cref="WeaponReloadTracker.Update"/> each flight frame.</summary>
public readonly record struct WeaponSlotDescriptor(
    int MountId,
    string Name,
    bool ConsumesAmmo,
    short CapacityAmmo,
    bool IsReady = true);

/// <summary>One rendered row of the weapon-status HUD list — a snapshot taken by
/// <see cref="WeaponReloadTracker.Update"/>, pure data (no MonoGame types) so a
/// list is trivially constructible/assertable in tests and in <c>HudFrame</c>.</summary>
public readonly record struct WeaponStatusRow(
    int MountId,
    string Name,
    bool ConsumesAmmo,
    short Ammo,
    short CapacityAmmo,
    WeaponStatus Status);

/// <summary>
/// Pure (MonoGame-free) derivation of each mounted weapon system's status from
/// two cheap client-observable signals, following the same philosophy as
/// <see cref="DamageFlashTracker"/> (which infers damage from the hull fraction
/// because there is no discrete "just hit" wire event on the client):
///
/// <list type="bullet">
/// <item><b>Success — the RELOAD(129) relay.</b> P361's auto-reload on success
/// is SYNCHRONOUS on the server: the spare pack is pulled and the pool refills
/// inside the same sim tick, and the server then broadcasts the applied entries
/// sector-wide as RELOAD(129) (<c>Server.Combat.BroadcastAutoReload</c>). The
/// wire ammo fraction therefore never shows 0 for a successful reload — the only
/// authoritative success signal a client can see is that relay. This tracker
/// treats a just-arrived relay as native's "Reloading ammo..." notification:
/// it shows the mount(s) RELOADING for a brief <see cref="ReloadShowSeconds"/>
/// window (native's announcement has no duration either — it is a one-shot text)
/// then back to <see cref="WeaponStatus.Ready"/>.</item>
/// <item><b>Failure — the ammo trajectory.</b> On failure (no spare in cargo)
/// nothing is relayed and the pool stays at zero, so the own ship's live ammo
/// fraction stays ~0; after <see cref="DepleteThresholdSeconds"/> of sustained
/// emptiness the mount is shown <see cref="WeaponStatus.Depleted"/> (native's
/// "Ammo depleted."), reverting to Ready once the fraction recovers.</item>
/// </list>
///
/// <para>This port models ammo as ONE shared magazine (<c>Ship.Ammo</c>,
/// <c>Weapon.cs</c> drains it for every projectile weapon), so all projectile
/// mounts deplete/reload together — faithful to the port's sim, not a display
/// bug. Systems that never consume the pool (energy weapons, missile launchers)
/// are <see cref="WeaponStatus.Ready"/> regardless and never enter
/// Reloading/Depleted, which is what makes distinct mounted systems show
/// genuinely independent statuses.</para>
///
/// <para>Timeline comes from the caller as <paramref name="nowSec"/> on a
/// monotonically increasing clock (<c>Game1._nowSeconds</c>).</para>
/// </summary>
public sealed class WeaponReloadTracker
{
    /// <summary>
    /// Ammo fraction at or below which the magazine counts as "effectively
    /// empty" for reload purposes. The wire fraction is a 0..240 byte
    /// (<c>CompactQuant.DePercent</c>), so granularity is ~0.004; 0.016 (~4/240)
    /// sits comfortably above rounding/jitter noise while still meaning ~0.
    /// Recovery is judged against the same epsilon.
    /// </summary>
    public const float EmptyEpsilon = 0.016f;

    /// <summary>
    /// How long the fraction must stay ~0 before a Reloading system is declared
    /// Depleted. P361's debounce is 0.5/c_fcidMountRate = 0.05s and a successful
    /// reload is instant; 0.35s is a generous grace that never mislabels a
    /// successful recovery (which is also signalled by the RELOAD relay) while
    /// clearly separating the sustained-zero "no spare in cargo" failure.
    /// </summary>
    public const float DepleteThresholdSeconds = 0.35f;

    /// <summary>
    /// How long a successful reload (a seen RELOAD(129) relay) is shown as
    /// RELOADING on the list before returning to Ready — the port's display of
    /// native's momentary "Reloading ammo..." announcement (native's success is
    /// synchronous, so there is no real reload duration to track).
    /// </summary>
    public const float ReloadShowSeconds = 0.6f;

    private readonly Dictionary<int, (WeaponStatus status, float zeroSince, float reloadUntil, bool waitingForRelay)> _states = new();

    /// <summary>Reset all per-mount state (e.g. on a hull/loadout change).</summary>
    public void Reset()
    {
        _states.Clear();
    }

    /// <summary>
    /// Advance the tracker one frame and compute the status of every supplied
    /// mount. Order preserved from <paramref name="slots"/>. Pure — no IO, no
    /// MonoGame, fully unit-testable.
    /// </summary>
    /// <param name="nowSec">Monotonic client seconds.</param>
    /// <param name="ammoFrac">The local ship's live ammo fraction (0..1).</param>
    /// <param name="reloadArrived">True iff a RELOAD(129) relay for the local
    /// ship arrived since the previous frame (a successful P361 auto-reload).</param>
    /// <param name="slots">The mounted systems to evaluate.</param>
    public IReadOnlyList<WeaponStatusRow> Update(
        float nowSec, float ammoFrac, bool reloadArrived, IReadOnlyList<WeaponSlotDescriptor> slots)
    {
        var rows = new List<WeaponStatusRow>(slots.Count);
        foreach (var s in slots)
        {
            if (!s.IsReady)
            {
                rows.Add(new WeaponStatusRow(s.MountId, s.Name, s.ConsumesAmmo,
                    (short)Math.Clamp((int)MathF.Round(s.CapacityAmmo * ammoFrac), 0, s.CapacityAmmo),
                    s.CapacityAmmo, WeaponStatus.NotReady));
                continue;
            }
            if (!s.ConsumesAmmo)
            {
                // Energy weapon / missile launcher: never touches the shared pool.
                rows.Add(new WeaponStatusRow(s.MountId, s.Name, false, 0, s.CapacityAmmo, WeaponStatus.Ready));
                continue;
            }

            _states.TryGetValue(s.MountId, out var st);

            if (reloadArrived)
            {
                // Successful P361 auto-reload relayed to our ship: show the
                // "Reloading ammo..." moment for a brief window, then Ready.
                st = (WeaponStatus.Reloading, nowSec, nowSec + ReloadShowSeconds, false);
            }
            else
            {
                switch (st.status)
                {
                    case WeaponStatus.Ready:
                        if (ammoFrac <= EmptyEpsilon)
                            st = (WeaponStatus.Reloading, nowSec, float.MaxValue, true); // emptied, awaiting signal
                        break;
                    case WeaponStatus.Reloading:
                        if (!st.waitingForRelay)
                        {
                            // Relay-driven window: time-based (ammo is already back).
                            if (nowSec >= st.reloadUntil) st.status = WeaponStatus.Ready;
                        }
                        else
                        {
                            // Zero-transition-driven: awaiting a relay / recovery.
                            if (ammoFrac > EmptyEpsilon) st.status = WeaponStatus.Ready;      // recovered
                            else if (nowSec - st.zeroSince >= DepleteThresholdSeconds) st.status = WeaponStatus.Depleted;
                        }
                        break;
                    case WeaponStatus.Depleted:
                        if (ammoFrac > EmptyEpsilon) st.status = WeaponStatus.Ready;          // replenished another way
                        break;
                }
            }
            _states[s.MountId] = st;

            short ammo = (short)Math.Clamp((int)MathF.Round(s.CapacityAmmo * ammoFrac), 0, s.CapacityAmmo);
            rows.Add(new WeaponStatusRow(s.MountId, s.Name, true, ammo, s.CapacityAmmo, st.status));
        }

        return rows;
    }
}

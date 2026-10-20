using Allegiance.Core.Model;
using Allegiance.Core.Sim;
using Allegiance.Core.Wire;

namespace Allegiance.Core;

/// <summary>
/// P758 — the client-side mount decision for an offered part treasure, the
/// port of the <c>c_tcPart</c> case of native <c>IshipIGC::HitTreasure</c>
/// (<c>src/Igc/shipIGC.h:1239-1305</c>). Native's client
/// <c>FM_S_ACQUIRE_TREASURE</c> handler answers the offer with
/// <c>FM_C_TREASURE_ACK</c> whose <c>mountID</c> is
/// <c>m_ship-&gt;HitTreasure(c_tcPart, treasureID, amount)</c>
/// (<c>clintlib/appmsg.cpp:270-275</c>) — this module reproduces exactly that
/// mount search so the port's real client answers offers the way native's does.
/// <para>The native search, quoted from <c>shipIGC.h:1257-1305</c>:</para>
/// <list type="number">
/// <item><b>Real mounts first.</b> <c>maxMounts</c> is
/// <c>GetMaxWeapons()</c> for a weapon part, else 1 (the single equipment
/// socket, <c>shipIGC.h:1263-1266</c>). For each mount that
/// <c>CanMount(ppt, mount)</c>: an <b>empty</b> location wins immediately
/// (<c>ppart == NULL → break</c>, :1271-1272); an occupied location only wins
/// when it holds the <b>same part type</b> <em>and</em> is a launcher with
/// room (<c>a &lt; maxAmount &amp;&amp; a + amount &lt;= maxAmount</c>,
/// :1274-1281) — the merge-into-existing-magazine rule.</item>
/// <item><b>Cargo fallback.</b> If no real mount was found, the same search
/// repeats over the cargo slots <c>-1..-c_maxCargo</c> (:1285-1301): first
/// empty slot wins, else a same-type launcher with room.</item>
/// <item><b>Full.</b> No location at all → the caller plays
/// <c>salCargoFullSound</c> and answers <c>c_mountNA</c> (the decline
/// sentinel, :1302-1304) — the server then drops the part back into space as
/// a fresh treasure.</item>
/// </list>
/// <para><b>Amount capacity.</b> Native's <c>maxAmount = ppt-&gt;GetAmount(this)</c>
/// (:1277) is the per-mount capacity including the ship's side GA; the port
/// reuses <see cref="Ship.ClampPartAmount(DataHullTypeIGC, StaticCore, DataPartTypeIGC, int)"/>
/// (which encodes the same hull capacities: ammo/fuel pools, magazine/
/// dispenser/chaff capacities with the expendable-size divisor) as the
/// client-side estimate — the authoritative grant stays server-side in
/// <c>Server.ShipCommands.HandleTreasureAck</c>, exactly as native's does in
/// <c>FedSrv.CPP:6094-6152</c>.</para>
/// </summary>
public static class TreasureMountPicker
{
    /// <summary><c>c_mountNA</c> (<c>igc.h:845-846</c>): the decline sentinel —
    /// no real mount and no cargo slot could take the part.</summary>
    public const sbyte MountNA = -Ship.CargoCapacity - 1;   // -6

    /// <summary>
    /// Pick the mount for an offered part treasure, following the native
    /// <c>HitTreasure(c_tcPart, …)</c> search order. <paramref name="mounted"/>
    /// is the ship's current equipped parts (non-negative mounts);
    /// <paramref name="cargo"/> its cargo hold entries (native negative mounts,
    /// carried on this port's wire as high-bit bytes, :<see cref="ExpandedPartData.MountId"/>).
    /// </summary>
    /// <returns>The chosen mount (real mount ≥ 0, cargo −1..−5, or
    /// <see cref="MountNA"/> when nothing can take the part).</returns>
    public static sbyte PickMount(
        in DataHullTypeIGC hull,
        StaticCore? core,
        in DataPartTypeIGC part,
        IReadOnlyList<ExpandedPartData> mounted,
        IReadOnlyList<ExpandedPartData> cargo,
        short amount)
    {
        // shipIGC.h:1263-1266: a weapon searches every hardpoint, every other
        // equipment type its single socket.
        int maxMounts = part.EquipmentType == EquipmentType.Weapon ? hull.MaxWeapons : 1;

        // Pass 1 — real mounts (shipIGC.h:1268-1282).
        for (int mount = 0; mount < maxMounts; mount++)
        {
            if (!hull.CanMountPart(part, mount)) continue;
            var existing = PartAt(core, mounted, part.EquipmentType, mount);
            if (existing is null)
                return (sbyte)mount;                                  // empty location wins
            if (existing.Value.PartId == part.PartId && IsLauncher(part)
                && LauncherHasRoom(hull, core, part, existing.Value.Amount, amount))
                return (sbyte)mount;                                  // merge into the magazine
        }

        // Pass 2 — cargo slots -1..-c_maxCargo (shipIGC.h:1284-1301).
        for (int slot = 0; slot < Ship.CargoCapacity; slot++)
        {
            var existing = CargoAt(cargo, slot);
            if (existing is null)
                return (sbyte)(-slot - 1);                            // first empty cargo slot
            if (existing.Value.PartId == part.PartId && IsLauncher(part)
                && LauncherHasRoom(hull, core, part, existing.Value.Amount, amount))
                return (sbyte)(-slot - 1);                            // merge into the cargo magazine
        }

        // shipIGC.h:1302-1304: no place to put it — the caller plays the
        // cargo-full cue and the offer is declined with c_mountNA.
        return MountNA;
    }

    /// <summary>The native launcher test <c>IlauncherIGC::IsLauncher</c>
    /// (<c>shipIGC.h:1275</c>; the port's <c>Ship.IsLauncherType</c>):
    /// magazines, dispensers and chaff launchers stack amounts; nothing else
    /// merges.</summary>
    public static bool IsLauncher(in DataPartTypeIGC part)
        => part.Type is DataPartTypeIGC.ChaffLauncher
           || part.EquipmentType is EquipmentType.Magazine or EquipmentType.Dispenser;

    /// <summary>The merge rule <c>(a &lt; maxAmount) &amp;&amp; ((a + amount)
    /// &lt;= maxAmount)</c> (<c>shipIGC.h:1277-1280</c>) against the port's
    /// hull-capacity table.</summary>
    private static bool LauncherHasRoom(in DataHullTypeIGC hull, StaticCore? core,
        in DataPartTypeIGC part, short current, short amount)
    {
        short maxAmount = Ship.ClampPartAmount(hull, core, part, short.MaxValue);
        return current < maxAmount && current + amount <= maxAmount;
    }

    /// <summary>Native <c>PartLocation(et, mount)</c> for a real mount: the
    /// equipped part of this equipment type at this mount index. The wire
    /// loadout carries at most one part per (type, mount); the entry's
    /// equipment type is resolved through the static core exactly as the
    /// server's merge branch does (<c>Server.ShipCommands.HandleTreasureAck</c>:
    /// <c>p.EquipmentType == partType.EquipmentType &amp;&amp; p.MountId == mount</c>).
    /// An entry whose part type cannot be resolved is treated as occupied —
    /// never double-mounted.</summary>
    private static ExpandedPartData? PartAt(StaticCore? core, IReadOnlyList<ExpandedPartData> parts,
        EquipmentType et, int mount)
    {
        for (int i = 0; i < parts.Count; i++)
        {
            if (parts[i].MountId != (byte)mount) continue;
            if (core?.PartTypes.TryGetValue(parts[i].PartId, out var existing) is true
                && existing.EquipmentType != et) continue;
            return parts[i];
        }
        return null;
    }

    /// <summary>Native <c>PartLocation(NA, mount)</c> for cargo: the part in
    /// cargo slot <paramref name="slot"/> (native mount
    /// <c>-slot-1</c>, carried on the port's wire as a high-bit
    /// <see cref="ExpandedPartData.MountId"/> byte).</summary>
    private static ExpandedPartData? CargoAt(IReadOnlyList<ExpandedPartData> cargo, int slot)
    {
        byte wire = unchecked((byte)(-slot - 1));
        for (int i = 0; i < cargo.Count; i++)
            if (cargo[i].MountId == wire)
                return cargo[i];
        return null;
    }
}

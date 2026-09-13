using Allegiance.Core.Model;
using Allegiance.Core.Sim;

namespace Allegiance.Server;

/// <summary>
/// P197 — the in-memory, <b>connection-lifetime-only</b> per-player economy
/// state: a personal <see cref="Money"/> wallet plus the optional
/// <see cref="AutoDonateTarget"/> that payday income is redirected to.
/// </summary>
/// <remarks>
/// <para><b>Placement rationale.</b> Native keeps the wallet and the
/// auto-donate relationship on <c>CFSPlayer</c> / the ship
/// (<c>fsship.h:41-42</c> <c>GetMoney/SetMoney</c>, <c>fsship.h:342-351</c>
/// <c>GetAutoDonate/SetAutoDonate</c>) — the same per-connection lifetime as
/// this port's <see cref="PlayerScore"/>, but a <em>different concern</em>.
/// <see cref="PlayerScore"/> is deliberately rank/score-only (P193) and is
/// consumed untouched by P194/P195; folding economy fields into it would blur
/// its contract and risk nothing but noise for its existing consumers. So this
/// task introduces a <em>sibling</em> type, <see cref="PlayerWallet"/>, living
/// on the same <c>LiveShip</c> record next to <see cref="PlayerScore"/> —
/// identical lifetime (allocated at logon, discarded at disconnect) but a
/// clean separation of scoring state from economic state. One wallet per
/// connection is the same one-ship-per-connection simplification this port
/// already makes throughout (each <c>_peerShips</c> entry is exactly one
/// player-piloted ship).</para>
///
/// <para><b>Money width.</b> <see cref="Money"/> is <c>int</c>, not
/// <c>long</c>: native <c>Money</c> = <c>AGCMoney</c> = 32-bit <c>long</c>
/// (<c>AGCIDL.idl:246</c>), matching the already-established width of the
/// <c>Payday</c>(114)/<c>SetMoney</c>(115)/<c>MoneyChange</c>(60) wire codecs
/// in this port (P51/P118).</para>
///
/// <para><b>AutoDonateTarget is a <see cref="Ship"/> reference</b>, mirroring
/// native's <c>IshipIGC*</c> (the ship being donated to — the
/// <c>GetIGCShip()->GetAutoDonate()</c> relationship). Resolving the target
/// back to a player/wallet for a payday credit or a one-time gift is a
/// <c>_peerShips</c> reverse lookup by ship reference (a player-piloted ship
/// is always in the live map). A <c>null</c> target means the player keeps
/// their own payday share (native's leader / opted-out case).</para>
///
/// <para>Everything here is written only on the sim thread (the same thread
/// that owns <c>_peerShips</c> and every payday/join/ballot mutation), so no
/// extra locking is needed — mirroring <see cref="PlayerScore"/> and the
/// <c>OnPayday</c> broadcast.</para>
/// </remarks>
public sealed class PlayerWallet
{
    /// <summary>The player's personal wallet balance (native <c>Money</c>,
    /// 32-bit). Defaults to <c>0</c>.</summary>
    public int Money { get; set; }

    /// <summary>The <see cref="Ship"/> this player's payday income is donated
    /// to, or <c>null</c> to keep their own earnings (native
    /// <c>GetIGCShip()->GetAutoDonate()</c>, <c>null</c> for a leader / opted
    /// out player). Defaults to <c>null</c>.</summary>
    public Ship? AutoDonateTarget { get; set; }
}

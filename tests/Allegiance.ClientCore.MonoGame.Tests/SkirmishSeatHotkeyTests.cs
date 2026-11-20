namespace Allegiance.ClientCore.MonoGame.Tests;

/// <summary>Regression (2026-11-20 playtest): holding Ctrl to fire missiles and pressing a
/// number changed the skirmish seat. Ctrl+number belongs to native flight bindings while
/// flying, so the port-only seat hotkeys work only while spectating.</summary>
public sealed class SkirmishSeatHotkeyTests
{
    [Fact]
    public void Flying_pilot_never_triggers_a_seat_change()
        => Assert.False(Game1.SeatHotkeysLive(spectating: false, chatActive: false, keyBindVisible: false, seatChangePending: false));

    [Fact]
    public void Spectator_can_join_a_side()
        => Assert.True(Game1.SeatHotkeysLive(spectating: true, chatActive: false, keyBindVisible: false, seatChangePending: false));

    [Theory]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, true)]
    public void Spectator_keys_are_suppressed_by_chat_bindings_or_a_pending_change(bool chat, bool binds, bool pending)
        => Assert.False(Game1.SeatHotkeysLive(true, chat, binds, pending));
}

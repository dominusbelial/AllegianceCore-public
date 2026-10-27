using Allegiance.Core;
using Allegiance.Core.Sim;
using Allegiance.TestInfra;

namespace Allegiance.Core.Tests;

public sealed partial class StationCaptureLiveTests
{
    [SkippableTheory]
    [Trait("Tier", "Live")]
    [InlineData(true)] [InlineData(false)]
    public void P832e_Nonwinning_capture_vacates_docked_pilots_only_when_old_side_is_active(bool active)
    {
        Require.Artwork();
        using var seat = Seat(new Spec(false, Far), new Spec(true, Far), new Spec(false, Far));
        var mission = seat.Base.Mission!;
        mission.Sides.Single(s => s.Id == seat.SideA).Active = active;
        var docked = seat.Ships[0]; var captor = seat.Ships[1];
        docked.StationId = BaseId;
        // Fixture has no victory goal: native fsship.cpp:693-695 vacates the
        // old occupants before the captor finishes docking. Full shields gate
        // is already covered by StationCaptureLiveTests.
        seat.Base.ReceiveDamage(0, 100, 0, default, default, null);
        captor.Position = AtBase;
        PumpUntil(seat.Server, () => seat.Base.SideId == seat.SideB && captor.StationId == BaseId, 6000);
        Assert.Equal(seat.SideB, seat.Base.SideId);
        Assert.Equal(BaseId, captor.StationId);
        if (active) Assert.Null(docked.StationId);
        else Assert.Equal(BaseId, docked.StationId);
        Assert.Equal(MissionStage.Started, mission.Stage);
    }
}

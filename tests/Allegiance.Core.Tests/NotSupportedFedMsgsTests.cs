using Allegiance.Core.Wire;
using Xunit;

namespace Allegiance.Core.Tests;

/// <summary>
/// Pins the shape of <see cref="NotSupportedFedMsgs"/> — a pure
/// documentation registry, so these are sanity checks, not behavior
/// tests: every declared <see cref="NotSupportedFedMsgs.Fmid"/> has an
/// entry, and no entry's explanation is empty.
/// </summary>
public sealed class NotSupportedFedMsgsTests
{
    [Fact]
    public void Every_declared_fmid_has_a_registry_entry()
    {
        foreach (NotSupportedFedMsgs.Fmid fmid in Enum.GetValues<NotSupportedFedMsgs.Fmid>())
            Assert.True(NotSupportedFedMsgs.All.ContainsKey(fmid), $"{fmid} has no registry entry");
    }

    [Fact]
    public void Every_entry_has_a_real_explanation()
    {
        foreach (var (fmid, entry) in NotSupportedFedMsgs.All)
            Assert.False(string.IsNullOrWhiteSpace(entry.Explanation), $"{fmid}'s explanation is empty");
    }

    [Fact]
    public void Registry_has_exactly_the_twelve_confirmed_entries()
    {
        // A deliberate low count guard: if this grows, it should be because
        // a new message was actually investigated and confirmed dead/N-A/
        // unrecoverable, not by accident. Update this count (and add a real
        // Explanation) when that happens.
        Assert.Equal(12, NotSupportedFedMsgs.All.Count);
    }
}

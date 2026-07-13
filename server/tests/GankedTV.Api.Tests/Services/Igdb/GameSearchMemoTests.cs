using FluentAssertions;
using GankedTV.Api.Services.Igdb;
using GankedTV.Api.Tests.TestSupport;

namespace GankedTV.Api.Tests.Services.Igdb;

public class GameSearchMemoTests
{
    private readonly FakeClock _clock = new(DateTimeOffset.Parse("2026-01-01T00:00:00Z"));

    [Fact]
    public void Remembered_TermExpires_AfterTtl()
    {
        var memo = new GameSearchMemo(_clock);
        var start = _clock.GetUtcNow();

        memo.Remember("satisfactory", TimeSpan.FromMinutes(15));
        memo.IsMemoized("satisfactory").Should().BeTrue();

        _clock.Set(start.AddMinutes(15).AddSeconds(1));
        memo.IsMemoized("satisfactory").Should().BeFalse();
    }

    [Fact]
    public void Forget_DropsTheTermImmediately()
    {
        var memo = new GameSearchMemo(_clock);
        memo.Remember("satisfactory", TimeSpan.FromMinutes(15));

        memo.Forget("satisfactory");

        memo.IsMemoized("satisfactory").Should().BeFalse();
    }

    [Fact]
    public void Overflow_EvictsInsteadOfGrowingUnbounded()
    {
        // Search terms are attacker-controlled, so the memo must stay bounded even when nothing
        // has expired yet. Eviction is safe: it only means re-querying IGDB for a term.
        var memo = new GameSearchMemo(_clock);
        var start = _clock.GetUtcNow();
        for (var i = 0; i <= GameSearchMemo.MaxEntries; i++)
        {
            // Stagger the writes (well inside the TTL) so the entries have distinct expiries —
            // this pins the eviction path, not the expiry path.
            _clock.Set(start.AddMilliseconds(i));
            memo.Remember($"term-{i}", TimeSpan.FromMinutes(15));
        }

        // The soonest-to-expire entry (the oldest write) is the one dropped.
        memo.IsMemoized("term-0").Should().BeFalse();
        memo.IsMemoized($"term-{GameSearchMemo.MaxEntries}").Should().BeTrue();
    }
}

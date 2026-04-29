namespace GankedTV.Api.Tests.TestSupport;

// Minimal frozen clock for tests. Returns the same UTC instant on every call until Set is called.
// Avoids pulling in Microsoft.Extensions.TimeProvider.Testing for what is a one-method need.
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Set(DateTimeOffset now) => _now = now;
}

namespace GankedTV.Api.Tests.TestSupport;

// Minimal frozen clock for tests. Returns the same UTC instant on every call until Set is called.
// Avoids pulling in Microsoft.Extensions.TimeProvider.Testing for what is a one-method need.
//
// Thread-safe: the hosted-service tests read the clock from a thread-pool worker while the test
// body may call Set on its own thread. DateTimeOffset is wider than 8 bytes, so unsynchronized
// reads can tear; a small lock keeps reads/writes atomic.
public sealed class FakeClock(DateTimeOffset start) : TimeProvider
{
    private readonly object _sync = new();
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_sync)
        {
            return _now;
        }
    }

    public void Set(DateTimeOffset now)
    {
        lock (_sync)
        {
            _now = now;
        }
    }
}

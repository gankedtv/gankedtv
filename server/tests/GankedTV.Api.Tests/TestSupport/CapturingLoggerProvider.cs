using Microsoft.Extensions.Logging;

namespace GankedTV.Api.Tests.TestSupport;

/// Minimal ILoggerProvider used by OAuth-provider tests to verify that the Debug-level branch
/// inside BuildExchangeExceptionAsync is taken. Each instance owns its own capture buffer so
/// tests don't interfere with each other.
public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly List<string> _messages = new();
    private readonly object _lock = new();

    public IReadOnlyList<string> Messages
    {
        get { lock (_lock) return _messages.ToArray(); }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this);

    public void Dispose() { }

    private sealed class CapturingLogger : ILogger
    {
        private readonly CapturingLoggerProvider _provider;
        public CapturingLogger(CapturingLoggerProvider provider) => _provider = provider;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_provider._lock)
            {
                _provider._messages.Add(formatter(state, exception));
            }
        }
    }
}

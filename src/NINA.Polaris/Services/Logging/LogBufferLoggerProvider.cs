using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// <see cref="ILoggerProvider"/> that bridges every <c>ILogger&lt;T&gt;</c>
/// in the app to the singleton <see cref="LogService"/> ring buffer.
/// Registered via factory in Program.cs so it gets the singleton
/// <see cref="LogService"/> instance:
/// <code>
/// builder.Logging.Services.AddSingleton&lt;ILoggerProvider&gt;(
///     sp => new LogBufferLoggerProvider(sp.GetRequiredService&lt;LogService&gt;()));
/// </code>
/// Logger instances are cached per category name so we don't pay the
/// allocation on every <c>ILogger&lt;Foo&gt; logger</c> resolve.
/// </summary>
public sealed class LogBufferLoggerProvider : ILoggerProvider {
    private readonly LogService _logService;
    private readonly ConcurrentDictionary<string, LogBufferLogger> _loggers = new();

    public LogBufferLoggerProvider(LogService logService) {
        _logService = logService;
    }

    public ILogger CreateLogger(string categoryName)
        => _loggers.GetOrAdd(categoryName, name => new LogBufferLogger(name, _logService));

    public void Dispose() => _loggers.Clear();
}

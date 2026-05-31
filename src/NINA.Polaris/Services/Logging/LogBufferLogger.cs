using Microsoft.Extensions.Logging;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// <see cref="ILogger"/> implementation that materialises every log
/// call into a <see cref="LogEntry"/> and stores it in the singleton
/// <see cref="LogService"/>. The provider creates one of these per
/// category name (typically the full name of <c>T</c> in
/// <c>ILogger&lt;T&gt;</c>).
///
/// Drops <see cref="LogLevel.Debug"/> + <see cref="LogLevel.Trace"/>
/// to keep the ring buffer focused on actionable events. Console
/// providers registered alongside still receive them, so dev-time
/// detail isn't lost.
/// </summary>
public sealed class LogBufferLogger : ILogger {
    private readonly string _categoryName;
    private readonly LogService _logService;

    public LogBufferLogger(string categoryName, LogService logService) {
        _categoryName = categoryName;
        _logService = logService;
    }

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public bool IsEnabled(LogLevel logLevel)
        => logLevel >= LogLevel.Information && logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
        Exception? exception, Func<TState, Exception?, string> formatter) {
        if (!IsEnabled(logLevel)) return;
        try {
            var message = formatter(state, exception) ?? string.Empty;
            var entry = new LogEntry(
                Id: 0,                       // assigned by LogService.Append
                At: DateTime.UtcNow,
                Level: MapLevel(logLevel),
                Source: "server",
                Message: message,
                Category: _categoryName,
                EventId: eventId.Id == 0 ? null : eventId.Id,
                ExceptionType: exception?.GetType().FullName,
                ExceptionMsg: exception?.Message,
                StackTrace: TrimStackTrace(exception?.StackTrace));
            _logService.Append(entry);
        } catch (Exception ex) {
            // Never let the logger itself break the calling code path.
            // Avoid recursion: do NOT use ILogger here.
            Console.Error.WriteLine($"[LogBufferLogger] failed: {ex.Message}");
        }
    }

    private static string MapLevel(LogLevel l) => l switch {
        LogLevel.Critical    => "critical",
        LogLevel.Error       => "error",
        LogLevel.Warning     => "warn",
        LogLevel.Information => "info",
        LogLevel.Debug       => "debug",
        LogLevel.Trace       => "debug",
        _                    => "info",
    };

    /// <summary>Cap stack traces at 20 lines so a runaway recursion
    /// doesn't blow up the ring buffer with a single entry.</summary>
    private static string? TrimStackTrace(string? trace) {
        if (string.IsNullOrEmpty(trace)) return trace;
        var lines = trace.Split('\n');
        if (lines.Length <= 20) return trace;
        return string.Join('\n', lines.Take(20)) + "\n   ... (truncated)";
    }

    private sealed class NullScope : IDisposable {
        public static readonly NullScope Instance = new();
        public void Dispose() { }
    }
}

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using NINA.Polaris.Services.Logging;

namespace NINA.Polaris.Middleware;

/// <summary>
/// DBGLOG-3: emit one <see cref="LogEntry"/> per HTTP request into
/// the singleton <see cref="LogService"/> ring buffer. Captures
/// method, path, status, duration (ms), remote IP. Skips static
/// assets so the buffer doesn't fill with .css/.js/.png requests,
/// and skips the logs endpoint itself so client→server log
/// forwarding doesn't produce its own HTTP entries (feedback loop).
///
/// Position in the pipeline: BEFORE <see cref="AuthMiddleware"/>,
/// so 401 rejections still produce a log row.
/// </summary>
public class RequestLoggingMiddleware {
    private readonly RequestDelegate _next;
    private readonly LogService _log;

    public RequestLoggingMiddleware(RequestDelegate next, LogService log) {
        _next = next;
        _log = log;
    }

    public async Task InvokeAsync(HttpContext ctx) {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (ShouldSkip(path)) { await _next(ctx); return; }

        var sw = Stopwatch.StartNew();
        Exception? captured = null;
        try {
            await _next(ctx);
        } catch (Exception ex) {
            // Re-throw after recording so the exception handler /
            // ASP.NET default 500-response path still runs, but the
            // entry includes the exception type even if we never get
            // a status code back (uncaught -> 500 from Kestrel).
            captured = ex;
            throw;
        } finally {
            sw.Stop();
            var status = ctx.Response?.StatusCode ?? 0;
            var level = SelectLevel(status, captured);
            var qIdx = path.IndexOf('?');
            var pathOnly = qIdx >= 0 ? path[..qIdx] : path;
            try {
                _log.Append(new LogEntry(
                    Id: 0,
                    At: DateTime.UtcNow,
                    Level: level,
                    Source: "http",
                    Message: $"{ctx.Request.Method} {pathOnly} {status} {sw.Elapsed.TotalMilliseconds:F1}ms",
                    Method: ctx.Request.Method,
                    Path: pathOnly,
                    Status: status,
                    DurationMs: sw.Elapsed.TotalMilliseconds,
                    RemoteIp: ctx.Connection.RemoteIpAddress?.ToString(),
                    ExceptionType: captured?.GetType().FullName,
                    ExceptionMsg: captured?.Message));
            } catch {
                // never let logging tear down the request pipeline
            }
        }
    }

    /// <summary>Static-asset and self-reference skip list. Static files
    /// would flood the buffer with no diagnostic value (the user can
    /// always inspect cache/network in DevTools). The /api/logs* skip
    /// is the loop-breaker: <c>POST /api/logs/client</c> from the
    /// frontend would otherwise produce both a client entry AND an
    /// http entry per call.</summary>
    private static bool ShouldSkip(string path) {
        if (string.IsNullOrEmpty(path)) return false;
        if (path.StartsWith("/api/logs", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/screenshots/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/sky/data/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/sky/js/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.StartsWith("/data/", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/favicon.ico", StringComparison.OrdinalIgnoreCase)) return true;
        if (path.Equals("/manifest.webmanifest", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static string SelectLevel(int status, Exception? ex) {
        if (ex != null) return "error";
        return status switch {
            >= 500 => "error",
            >= 400 => "warn",
            _      => "info",
        };
    }
}

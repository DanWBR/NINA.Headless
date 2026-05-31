using System.Text;
using System.Text.Json;
using NINA.Polaris.Services.Logging;

namespace NINA.Polaris.Endpoints;

/// <summary>
/// DBGLOG-4: HTTP surface for the debug log ring buffer.
///
/// <list type="bullet">
///   <item><c>GET /api/logs?since={id}&amp;max={n}&amp;level={min}&amp;source={...}&amp;search={...}</c>
///     Returns entries strictly newer than <c>since</c> (default 0,
///     pulls everything currently in the buffer), capped at
///     <c>max</c> (default 500, hard cap 5000). Filters apply
///     server-side so a mobile browser pulling a slow link doesn't
///     waste bandwidth.</item>
///   <item><c>GET /api/logs/export?format=jsonl|txt</c>
///     Streams the full buffer as a download. JSONL = one JSON
///     object per line (machine-friendly, attach to bug reports).
///     TXT = human-readable single-line format.</item>
///   <item><c>POST /api/logs/client</c>
///     Frontend forwards its own entries (toasts, exceptions,
///     apiFetch metadata). Body: <c>{ entries: [LogEntry,...] }</c>.
///     Rate-limited to 100 entries per request; the rest are
///     silently dropped to keep a runaway error loop from filling
///     the buffer.</item>
///   <item><c>DELETE /api/logs</c>
///     Clear the buffer. Cursor stays where it is so any in-flight
///     client snapshots still detect the wipe via the truncated
///     flag on their next SnapshotSince.</item>
/// </list>
///
/// All four are gated by <see cref="Middleware.AuthMiddleware"/>
/// automatically (registered under /api/*).
/// </summary>
public static class LogsEndpoints {
    public static void MapLogsEndpoints(this IEndpointRouteBuilder app) {
        var group = app.MapGroup("/api/logs");

        group.MapGet("/", (LogService svc, long? since, int? max,
                          string? level, string? source, string? search) => {
            var sinceId = since ?? 0;
            var cap = Math.Clamp(max ?? 500, 1, 5000);
            // Pull a generous pre-filter slice so post-filter we still
            // hit `cap`. Bound to MaxKept * 1.2 to avoid degenerate
            // searches that scan the whole buffer multiple times.
            var snap = svc.SnapshotSince(sinceId, max: Math.Min(LogService.MaxKept, cap * 5));
            var filtered = ApplyFilters(snap.Entries, level, source, search)
                .Take(cap)
                .ToList();
            var cursor = filtered.Count > 0 ? filtered[^1].Id : snap.Cursor;
            return Results.Ok(new {
                entries = filtered,
                cursor,
                truncated = snap.Truncated,
                oldestRetained = svc.OldestId,
                currentCursor = svc.CurrentId,
            });
        });

        group.MapGet("/export", async (LogService svc, HttpContext ctx, string? format) => {
            var fmt = (format ?? "jsonl").Trim().ToLowerInvariant();
            var ts = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
            if (fmt == "txt") {
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                ctx.Response.Headers.ContentDisposition =
                    $"attachment; filename=\"polaris-log-{ts}.txt\"";
                var snap = svc.Snapshot();
                await using var writer = new StreamWriter(ctx.Response.Body, Encoding.UTF8, leaveOpen: true);
                foreach (var e in snap) {
                    var line = FormatTxt(e);
                    await writer.WriteLineAsync(line);
                }
                await writer.FlushAsync(ctx.RequestAborted);
                return Results.Empty;
            }
            // default: jsonl
            ctx.Response.ContentType = "application/x-ndjson; charset=utf-8";
            ctx.Response.Headers.ContentDisposition =
                $"attachment; filename=\"polaris-log-{ts}.jsonl\"";
            await svc.ExportJsonlAsync(ctx.Response.Body, ctx.RequestAborted);
            return Results.Empty;
        });

        group.MapPost("/client", (LogService svc, ClientLogBatch body) => {
            if (body?.Entries == null || body.Entries.Count == 0) {
                return Results.Ok(new { accepted = 0 });
            }
            // Rate-limit: max 100 entries per POST. Excess silently
            // discarded -- a runaway error loop in the browser would
            // otherwise saturate the buffer and push everything else
            // out. The frontend debouncer caps at 50 already, so 100
            // is a generous ceiling.
            var take = Math.Min(100, body.Entries.Count);
            for (int i = 0; i < take; i++) {
                var src = body.Entries[i];
                if (src == null) continue;
                svc.Append(new LogEntry(
                    Id: 0,
                    At: src.At ?? DateTime.UtcNow,
                    Level: NormaliseLevel(src.Level),
                    Source: string.IsNullOrEmpty(src.Source) ? "client" : src.Source!,
                    Message: src.Message ?? string.Empty,
                    Category: src.Category,
                    Method: src.Method,
                    Path: src.Path,
                    Status: src.Status,
                    DurationMs: src.DurationMs,
                    ExceptionType: src.ExceptionType,
                    ExceptionMsg: src.ExceptionMsg,
                    StackTrace: src.StackTrace));
            }
            return Results.Ok(new { accepted = take, dropped = body.Entries.Count - take });
        });

        group.MapDelete("/", (LogService svc) => {
            svc.Clear();
            return Results.Ok(new { cleared = true });
        });
    }

    private static IEnumerable<LogEntry> ApplyFilters(
        IReadOnlyList<LogEntry> source,
        string? level, string? sourceFilter, string? search) {
        IEnumerable<LogEntry> q = source;
        if (!string.IsNullOrEmpty(level)) {
            var minRank = LevelRank(level);
            q = q.Where(e => LevelRank(e.Level) >= minRank);
        }
        if (!string.IsNullOrEmpty(sourceFilter) && sourceFilter != "all") {
            q = q.Where(e => string.Equals(e.Source, sourceFilter, StringComparison.OrdinalIgnoreCase));
        }
        if (!string.IsNullOrEmpty(search)) {
            var needle = search.Trim();
            if (needle.Length > 0) {
                q = q.Where(e =>
                    (e.Message?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.Category?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (e.Path?.Contains(needle, StringComparison.OrdinalIgnoreCase) ?? false));
            }
        }
        return q;
    }

    private static int LevelRank(string level) => level?.ToLowerInvariant() switch {
        "debug"    => 0,
        "info"     => 1,
        "warn"     => 2,
        "error"    => 3,
        "critical" => 4,
        _          => 1,
    };

    private static string NormaliseLevel(string? level) => level?.ToLowerInvariant() switch {
        "trace" or "debug"        => "debug",
        "info"  or "information"  => "info",
        "warn"  or "warning"      => "warn",
        "error"                   => "error",
        "critical" or "fatal"     => "critical",
        _                         => "info",
    };

    private static string FormatTxt(LogEntry e) {
        // Human-readable single-line summary, kept narrow enough to
        // wrap reasonably in a terminal: <time> <level> <source>
        // <message> [path status durationMs] [excType: excMsg].
        var sb = new StringBuilder(256);
        sb.Append(e.At.ToString("yyyy-MM-dd HH:mm:ss.fff"));
        sb.Append(' ');
        sb.Append(e.Level.ToUpperInvariant().PadRight(5));
        sb.Append(' ');
        sb.Append('[').Append(e.Source).Append(']');
        if (!string.IsNullOrEmpty(e.Category)) {
            sb.Append(' ').Append(e.Category);
        }
        sb.Append(' ').Append(e.Message);
        if (!string.IsNullOrEmpty(e.Path)) {
            sb.Append(" path=").Append(e.Path);
            if (e.Status.HasValue) sb.Append(" status=").Append(e.Status);
            if (e.DurationMs.HasValue) sb.Append(" dur=").Append(e.DurationMs.Value.ToString("F1")).Append("ms");
        }
        if (!string.IsNullOrEmpty(e.ExceptionType)) {
            sb.Append(' ').Append(e.ExceptionType).Append(": ").Append(e.ExceptionMsg);
        }
        return sb.ToString();
    }
}

/// <summary>Frontend-forwarded batch. Fields mirror
/// <see cref="LogEntry"/> with everything nullable so the JS side
/// only sends what it has.</summary>
public sealed class ClientLogBatch {
    public List<ClientLogEntry>? Entries { get; set; }
}

public sealed class ClientLogEntry {
    public DateTime? At { get; set; }
    public string? Level { get; set; }
    public string? Source { get; set; }
    public string? Message { get; set; }
    public string? Category { get; set; }
    public string? Method { get; set; }
    public string? Path { get; set; }
    public int? Status { get; set; }
    public double? DurationMs { get; set; }
    public string? ExceptionType { get; set; }
    public string? ExceptionMsg { get; set; }
    public string? StackTrace { get; set; }
}

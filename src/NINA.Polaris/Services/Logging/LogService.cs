using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services.Logging;

/// <summary>
/// Singleton ring buffer for the unified debug log. Every server-side
/// ILogger&lt;T&gt; call, every HTTP request (via
/// <see cref="Middleware.RequestLoggingMiddleware"/>), and every
/// frontend-forwarded entry (via <c>POST /api/logs/client</c>) lands
/// here. Mirrors the <see cref="NotificationService"/> shape
/// (Interlocked monotonic Id + ConcurrentQueue capped at MaxKept) so
/// the WS payload pipeline can deliver entries-since-cursor without
/// caring who produced them.
///
/// This service MUST NOT use <c>ILogger</c> itself -- any internal
/// failure would loop straight back through
/// <see cref="LogBufferLoggerProvider"/> and overflow the buffer.
/// Internal errors go to <see cref="Console.Error"/> instead.
/// </summary>
public class LogService {
    /// <summary>5000 entries ≈ 5 MB at typical entry size (~1 KB after
    /// stack traces). Covers 60-120 min of an active session at the
    /// observed 40-80 entries/min rate. Older entries roll off.</summary>
    public const int MaxKept = 5000;

    private long _nextId;
    private readonly ConcurrentQueue<LogEntry> _queue = new();
    private long _oldestId;

    /// <summary>Latest assigned Id. The WS payload includes this so
    /// the frontend can detect a buffer overflow (received cursor jumps
    /// past the locally-known max + max returned) and trigger a
    /// refetch.</summary>
    public long CurrentId => Interlocked.Read(ref _nextId);

    /// <summary>Smallest Id currently in the buffer. When a caller
    /// asks for <c>SnapshotSince(N)</c> where N is less than this,
    /// the response is marked <c>truncated: true</c> so the UI can
    /// surface "older entries dropped".</summary>
    public long OldestId => Interlocked.Read(ref _oldestId);

    /// <summary>Fires after every successful <see cref="Append"/>. The
    /// LogRotatorService subscribes to this to write entries to disk
    /// when opt-in disk persistence is on. Handlers MUST be cheap +
    /// non-throwing -- they run on the calling thread.</summary>
    public event Action<LogEntry>? Appended;

    /// <summary>Snapshot everything currently in the buffer. Used by
    /// the export endpoint.</summary>
    public IReadOnlyList<LogEntry> Snapshot() => _queue.ToArray();

    /// <summary>Return entries with Id strictly greater than
    /// <paramref name="sinceId"/>, capped at <paramref name="max"/>.
    /// Returns the new cursor (= largest Id in the returned set, or
    /// <paramref name="sinceId"/> if empty) so the caller can advance
    /// without scanning the result.</summary>
    public LogSnapshot SnapshotSince(long sinceId, int max = 500) {
        // ConcurrentQueue snapshot is O(n) but stable; cheap enough for
        // the 5000-cap buffer. Filter + take + project in one pass.
        var all = _queue.ToArray();
        var fresh = new List<LogEntry>(Math.Min(max, all.Length));
        long cursor = sinceId;
        foreach (var e in all) {
            if (e.Id <= sinceId) continue;
            fresh.Add(e);
            if (e.Id > cursor) cursor = e.Id;
            if (fresh.Count >= max) break;
        }
        var truncated = sinceId > 0 && sinceId < Interlocked.Read(ref _oldestId);
        return new LogSnapshot(fresh, cursor, truncated);
    }

    /// <summary>Append a new entry. Assigns Id, applies the sensitivity
    /// filter, enqueues, evicts head if over <see cref="MaxKept"/>,
    /// fires <see cref="Appended"/>. Returns the entry as stored
    /// (with Id populated).</summary>
    public LogEntry Append(LogEntry entry) {
        try {
            var id = Interlocked.Increment(ref _nextId);
            var sanitized = Sanitize(entry) with { Id = id };
            _queue.Enqueue(sanitized);
            // Evict head until at-or-under cap. Track oldest Id so the
            // SnapshotSince truncated flag stays honest.
            while (_queue.Count > MaxKept && _queue.TryDequeue(out var dropped)) {
                Interlocked.Exchange(ref _oldestId, dropped.Id + 1);
            }
            // First append sets oldestId to 1 (the just-assigned Id).
            if (Interlocked.Read(ref _oldestId) == 0) {
                Interlocked.CompareExchange(ref _oldestId, sanitized.Id, 0);
            }
            try { Appended?.Invoke(sanitized); }
            catch (Exception ex) {
                // A subscriber blew up. Do NOT log it via ILogger
                // (recursion), surface to stderr only.
                Console.Error.WriteLine($"[LogService] Appended subscriber threw: {ex.Message}");
            }
            return sanitized;
        } catch (Exception ex) {
            Console.Error.WriteLine($"[LogService] Append failed: {ex.Message}");
            return entry;
        }
    }

    /// <summary>Drop the buffer, bump the cursor so any
    /// in-flight client snapshots can detect the wipe.</summary>
    public void Clear() {
        while (_queue.TryDequeue(out _)) { }
        // Bumping _nextId would re-issue Ids if anyone is mid-Append;
        // safer: leave _nextId where it is. Reset oldestId to 0 so the
        // next Append re-seeds it.
        Interlocked.Exchange(ref _oldestId, 0);
    }

    /// <summary>Stream the current buffer as newline-delimited JSON.
    /// Used by the export endpoint; doesn't buffer the full payload
    /// in memory (each entry serialises + flushes individually).</summary>
    public async Task ExportJsonlAsync(Stream destination, CancellationToken ct = default) {
        var snapshot = Snapshot();
        await using var writer = new StreamWriter(destination, leaveOpen: true);
        foreach (var entry in snapshot) {
            if (ct.IsCancellationRequested) break;
            var json = JsonSerializer.Serialize(entry, _exportOptions);
            await writer.WriteLineAsync(json);
        }
        await writer.FlushAsync(ct);
    }

    private static readonly JsonSerializerOptions _exportOptions = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    // -------------------------------------------------------------
    // Sensitivity filter. Single chokepoint -- every entry passes
    // through Sanitize() before it lands in the queue, so we don't
    // need to remember to redact at each call site. Patterns kept
    // small + fast (pre-compiled regexes); add more as we find leaks.
    // -------------------------------------------------------------

    private static readonly Regex[] _redactors = new[] {
        new Regex(@"password=[^&\s]+",                      RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"token=[^&\s]+",                         RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"Authorization:\s*Bearer\s+\S+",         RegexOptions.Compiled | RegexOptions.IgnoreCase),
        new Regex(@"polaris_session=[^;]+",                 RegexOptions.Compiled | RegexOptions.IgnoreCase),
    };
    private const string Redacted = "***";

    private static string? Redact(string? s) {
        if (string.IsNullOrEmpty(s)) return s;
        foreach (var rx in _redactors) {
            s = rx.Replace(s, m => {
                var eq = m.Value.IndexOf('=');
                if (eq >= 0) return m.Value[..(eq + 1)] + Redacted;
                var colon = m.Value.IndexOf(':');
                if (colon >= 0) return m.Value[..(colon + 1)] + " " + Redacted;
                return Redacted;
            });
        }
        return s;
    }

    /// <summary>Auth endpoints sometimes leak credentials into the
    /// path/query (e.g. <c>?token=...</c>). Trim the query string for
    /// any /api/auth/* request before storing.</summary>
    private static LogEntry Sanitize(LogEntry e) {
        var path = e.Path;
        if (!string.IsNullOrEmpty(path) && path.StartsWith("/api/auth/", StringComparison.OrdinalIgnoreCase)) {
            var q = path.IndexOf('?');
            if (q > 0) path = path[..q];
        }
        return e with {
            Message       = Redact(e.Message) ?? string.Empty,
            Path          = Redact(path),
            ExceptionMsg  = Redact(e.ExceptionMsg),
            StackTrace    = Redact(e.StackTrace),
        };
    }
}

/// <summary>One row in the debug log. Server-assigned <see cref="Id"/>
/// is the only required field; everything else is optional + nullable
/// so callers only set what's relevant for their source.</summary>
public record LogEntry(
    long Id,
    DateTime At,
    string Level,
    string Source,
    string Message,
    string? Category = null,
    string? Method = null,
    string? Path = null,
    int? Status = null,
    double? DurationMs = null,
    string? RemoteIp = null,
    int? EventId = null,
    string? ExceptionType = null,
    string? ExceptionMsg = null,
    string? StackTrace = null);

/// <summary>Return shape for <see cref="LogService.SnapshotSince"/>.
/// <see cref="Truncated"/> = caller's <c>since</c> was older than the
/// oldest entry still in the buffer; some history was dropped.</summary>
public record LogSnapshot(
    IReadOnlyList<LogEntry> Entries,
    long Cursor,
    bool Truncated);

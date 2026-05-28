using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;

namespace NINA.Polaris.Services.Auth;

/// <summary>
/// AUTH-1: server-side state for the local-auth feature. Owns the
/// password hash (lives on UserProfile so it survives restart) and
/// an in-memory session store (intentionally non-persistent: server
/// restart invalidates all sessions, which is also our "I forgot
/// the password, SSH in and reset" lever).
///
/// Hashing: PBKDF2-SHA256, 100k iterations, 16-byte random salt per
/// password. AuthHashAlgo on the profile is parsed so a future move
/// to Argon2id can read old hashes without forcing a logout. All
/// password comparisons go through CryptographicOperations.
/// FixedTimeEquals to defeat timing attacks.
///
/// Sessions: 32-byte random base64-url token. SessionInfo tracks
/// LastActivityAt; ValidateToken bumps it on every hit so an
/// active session never times out. A 10-minute sweeper purges
/// stale entries.
///
/// Rate limit: per-IP failed-attempt counter, max 5 failures per
/// minute then exponential backoff capped at 1h. Successful login
/// clears the bucket for that IP. Server restart resets the
/// limiter (acceptable: lockouts are a usability speed-bump, not
/// a hard security boundary; the password hash is the real wall).
/// </summary>
public class AuthService : IDisposable {
    private readonly ProfileService _profile;
    private readonly ILogger<AuthService> _logger;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new();
    private readonly ConcurrentDictionary<string, AttemptBucket> _attempts = new();
    private readonly Timer _sweeper;

    private const int Pbkdf2Iterations = 100_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;
    private const int TokenBytes = 32;
    private const int MaxFailuresPerMinute = 5;
    private static readonly TimeSpan AttemptWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan MaxLockout = TimeSpan.FromHours(1);

    public AuthService(ProfileService profile, ILogger<AuthService> logger) {
        _profile = profile;
        _logger = logger;
        _sweeper = new Timer(_ => SweepExpired(), null,
            TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(10));
    }

    public bool IsConfigured =>
        !string.IsNullOrEmpty(_profile.Active.AuthPasswordHash);

    public bool IsEnabled => _profile.Active.AuthEnabled;

    public int SessionTimeoutHours =>
        Math.Max(1, _profile.Active.AuthSessionTimeoutHours);

    private TimeSpan SessionTtl => TimeSpan.FromHours(SessionTimeoutHours);

    /// <summary>Validates a session token and bumps its activity timestamp.
    /// Returns false for unknown / expired tokens. Loopback bypass is
    /// the middleware's responsibility, not this method's.</summary>
    public bool ValidateToken(string? token) {
        if (string.IsNullOrEmpty(token)) return false;
        if (!_sessions.TryGetValue(token, out var s)) return false;
        if (DateTime.UtcNow - s.LastActivityAt > SessionTtl) {
            _sessions.TryRemove(token, out _);
            return false;
        }
        s.LastActivityAt = DateTime.UtcNow;
        return true;
    }

    /// <summary>First-run: sets the password when none exists yet. Returns
    /// a session token on success, null when already configured (caller
    /// should redirect to /login).</summary>
    public string? SetInitialPassword(string password) {
        if (IsConfigured) return null;
        ValidatePasswordStrength(password);
        var (hash, salt) = HashPassword(password);
        _profile.Active.AuthPasswordHash = hash;
        _profile.Active.AuthPasswordSalt = salt;
        _profile.Active.AuthHashAlgo = "pbkdf2-sha256-100000";
        _profile.Save();
        _logger.LogInformation("Auth: initial password set");
        return CreateSession();
    }

    /// <summary>Change password after authenticating with the current one.
    /// Invalidates all other sessions (forces other devices to log in
    /// again with the new password). Returns the new session token to
    /// the caller so they don't get bumped to login themselves.</summary>
    public string? ChangePassword(
            string current, string newPassword, string keepSessionToken) {
        if (!IsConfigured) return null;
        if (!VerifyPassword(current)) return null;
        ValidatePasswordStrength(newPassword);
        var (hash, salt) = HashPassword(newPassword);
        _profile.Active.AuthPasswordHash = hash;
        _profile.Active.AuthPasswordSalt = salt;
        _profile.Save();
        // Drop every session except the caller's, force re-login on
        // other devices.
        foreach (var k in _sessions.Keys.ToArray()) {
            if (k != keepSessionToken) _sessions.TryRemove(k, out _);
        }
        _logger.LogInformation("Auth: password changed; {Count} other sessions invalidated",
            _sessions.Count - 1);
        return keepSessionToken;
    }

    /// <summary>Authenticate with the password. Returns null on failure
    /// (caller surfaces 401 + increments rate-limit). On success
    /// returns a new session token and clears the rate-limit bucket.</summary>
    public string? Login(string password, IPAddress? remoteIp) {
        var ipKey = remoteIp?.ToString() ?? "unknown";
        if (IsRateLimited(ipKey, out var retryAfter)) {
            _logger.LogWarning(
                "Auth: login rate-limited for {Ip} (retry in {RetryS}s)",
                ipKey, (int)retryAfter.TotalSeconds);
            return null;
        }
        if (!IsConfigured || !VerifyPassword(password)) {
            RegisterFailure(ipKey);
            return null;
        }
        _attempts.TryRemove(ipKey, out _);
        return CreateSession();
    }

    /// <summary>Drop a single session. Idempotent.</summary>
    public void Logout(string token) {
        if (string.IsNullOrEmpty(token)) return;
        _sessions.TryRemove(token, out _);
    }

    /// <summary>Toggle auth on or off, requires current password.</summary>
    public bool SetEnabled(string currentPassword, bool enabled) {
        if (!IsConfigured) return false;
        if (!VerifyPassword(currentPassword)) return false;
        _profile.Active.AuthEnabled = enabled;
        _profile.Save();
        _logger.LogInformation("Auth: enabled toggled to {Enabled}", enabled);
        return true;
    }

    /// <summary>Read-only snapshot for the /api/auth/status endpoint.</summary>
    public AuthStatusSnapshot GetStatus(string? presentedToken) => new(
        Configured: IsConfigured,
        Enabled: IsEnabled,
        Authenticated: ValidateToken(presentedToken),
        SessionTimeoutHours: SessionTimeoutHours);

    /// <summary>Cookie name for the HttpOnly session cookie, kept on the
    /// service so middleware + endpoints stay in sync.</summary>
    public const string CookieName = "polaris_session";

    public int ActiveSessionCount => _sessions.Count;

    public void Dispose() {
        _sweeper.Dispose();
    }

    // ----- internals --------------------------------------------------

    private string CreateSession() {
        var token = NewToken();
        _sessions[token] = new SessionInfo {
            Token = token,
            CreatedAt = DateTime.UtcNow,
            LastActivityAt = DateTime.UtcNow
        };
        return token;
    }

    private static string NewToken() {
        Span<byte> bytes = stackalloc byte[TokenBytes];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    private static (string hash, string salt) HashPassword(string password) {
        var saltBytes = new byte[SaltBytes];
        RandomNumberGenerator.Fill(saltBytes);
        var hashBytes = Rfc2898DeriveBytes.Pbkdf2(
            password, saltBytes, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, HashBytes);
        return (Convert.ToBase64String(hashBytes),
                Convert.ToBase64String(saltBytes));
    }

    private bool VerifyPassword(string password) {
        var hashStr = _profile.Active.AuthPasswordHash;
        var saltStr = _profile.Active.AuthPasswordSalt;
        if (string.IsNullOrEmpty(hashStr) || string.IsNullOrEmpty(saltStr))
            return false;
        var expected = Convert.FromBase64String(hashStr);
        var salt = Convert.FromBase64String(saltStr);
        // PBKDF2 only path for v1. AuthHashAlgo is parsed for future
        // migration but only the default is implemented today.
        var derived = Rfc2898DeriveBytes.Pbkdf2(
            password, salt, Pbkdf2Iterations,
            HashAlgorithmName.SHA256, expected.Length);
        return CryptographicOperations.FixedTimeEquals(derived, expected);
    }

    private static void ValidatePasswordStrength(string password) {
        if (string.IsNullOrEmpty(password))
            throw new ArgumentException("Password is required");
        if (password.Length < 8)
            throw new ArgumentException("Password must be at least 8 characters");
    }

    private void SweepExpired() {
        var cutoff = DateTime.UtcNow - SessionTtl;
        foreach (var kv in _sessions.ToArray()) {
            if (kv.Value.LastActivityAt < cutoff) {
                _sessions.TryRemove(kv.Key, out _);
            }
        }
        // Bucket cleanup, keep only the last hour's data.
        var attemptCutoff = DateTime.UtcNow - MaxLockout;
        foreach (var kv in _attempts.ToArray()) {
            if (kv.Value.LockedUntil < attemptCutoff && kv.Value.WindowStart < attemptCutoff) {
                _attempts.TryRemove(kv.Key, out _);
            }
        }
    }

    private bool IsRateLimited(string ipKey, out TimeSpan retryAfter) {
        retryAfter = TimeSpan.Zero;
        if (!_attempts.TryGetValue(ipKey, out var b)) return false;
        if (b.LockedUntil > DateTime.UtcNow) {
            retryAfter = b.LockedUntil - DateTime.UtcNow;
            return true;
        }
        return false;
    }

    private void RegisterFailure(string ipKey) {
        var now = DateTime.UtcNow;
        _attempts.AddOrUpdate(ipKey,
            _ => new AttemptBucket {
                WindowStart = now, Failures = 1, LockedUntil = DateTime.MinValue
            },
            (_, b) => {
                if (now - b.WindowStart > AttemptWindow) {
                    b.WindowStart = now;
                    b.Failures = 1;
                } else {
                    b.Failures++;
                }
                if (b.Failures > MaxFailuresPerMinute) {
                    // Exponential backoff: 1m, 2m, 4m, ... capped at 1h.
                    var over = b.Failures - MaxFailuresPerMinute;
                    var lockMinutes = Math.Min((int)MaxLockout.TotalMinutes,
                        (int)Math.Pow(2, Math.Min(6, over - 1)));
                    b.LockedUntil = now + TimeSpan.FromMinutes(lockMinutes);
                }
                return b;
            });
    }

    private sealed class SessionInfo {
        public string Token { get; set; } = "";
        public DateTime CreatedAt { get; set; }
        public DateTime LastActivityAt { get; set; }
    }

    private sealed class AttemptBucket {
        public DateTime WindowStart { get; set; }
        public int Failures { get; set; }
        public DateTime LockedUntil { get; set; }
    }
}

public record AuthStatusSnapshot(
    bool Configured,
    bool Enabled,
    bool Authenticated,
    int SessionTimeoutHours);

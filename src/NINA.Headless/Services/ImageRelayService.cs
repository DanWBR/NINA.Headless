using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Services;

public class ImageRelayService : IDisposable {
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private readonly ILogger<ImageRelayService> _logger;
    private ImageBuffer? _latestImage;
    private byte[]? _latestJpeg;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private const int MaxConsecutiveFailures = 3;

    /// <summary>Send latency above this triggers raw → JPEG downgrade.</summary>
    public TimeSpan AdaptiveDowngradeLatency { get; set; } = TimeSpan.FromSeconds(3);
    /// <summary>Number of consecutive slow frames before downgrading.</summary>
    public int AdaptiveDowngradeStreak { get; set; } = 2;
    /// <summary>Send latency below this for N frames allows JPEG → raw upgrade
    /// (only for clients we previously downgraded).</summary>
    public TimeSpan AdaptiveUpgradeLatency { get; set; } = TimeSpan.FromMilliseconds(600);
    public int AdaptiveUpgradeStreak { get; set; } = 5;
    /// <summary>Master switch — set false to pin clients to whatever mode they requested.</summary>
    public bool AdaptiveEnabled { get; set; } = true;

    public ImageRelayService(ILogger<ImageRelayService> logger) {
        _logger = logger;
    }

    public void RegisterClient(string id, System.Net.WebSockets.WebSocket ws) {
        _clients[id] = new ClientEntry(ws);
        _logger.LogInformation("Image stream client registered: {Id} (total: {Count})", id, _clients.Count);
    }

    public void UnregisterClient(string id) {
        if (_clients.TryRemove(id, out var entry)) {
            entry.SendLock.Dispose();
            _logger.LogInformation("Image stream client removed: {Id} (remaining: {Count})", id, _clients.Count);
        }
    }

    public async Task RelayImageAsync(IImageData imageData, CancellationToken ct = default) {
        var buffer = ImageBuffer.FromImageData(imageData);
        _latestImage = buffer;
        _latestJpeg = null;

        if (_clients.IsEmpty) return;

        // Prepare both formats lazily (only if needed)
        byte[]? rawFrame = null;
        byte[]? jpegFrame = null;

        bool needsRaw = false, needsJpeg = false;
        foreach (var (_, entry) in _clients) {
            if (entry.Ws.State == WebSocketState.Open) {
                if (entry.Mode == StreamMode.Raw) needsRaw = true;
                else needsJpeg = true;
            }
        }

        if (needsRaw) {
            var header = buffer.GetStreamHeader();
            var compressed = buffer.ToLz4Compressed();

            _logger.LogInformation(
                "Relaying image {W}x{H} ({BitDepth}-bit): {RawMB:F1}MB raw -> {CompMB:F1}MB LZ4 ({Ratio:F1}x) to {Count} clients",
                buffer.Width, buffer.Height, buffer.BitDepth,
                (double)imageData.Data.Length * 2 / (1024 * 1024),
                (double)compressed.Length / (1024 * 1024),
                (double)imageData.Data.Length * 2 / Math.Max(compressed.Length, 1),
                _clients.Count);

            rawFrame = new byte[4 + header.Length + compressed.Length];
            BitConverter.GetBytes(header.Length).CopyTo(rawFrame, 0);
            header.CopyTo(rawFrame, 4);
            compressed.CopyTo(rawFrame, 4 + header.Length);
        }

        if (needsJpeg) {
            jpegFrame = buffer.ToJpeg(85);
            _logger.LogInformation(
                "Relaying JPEG {W}x{H}: {SizeKB:F0}KB to JPEG clients",
                buffer.Width, buffer.Height,
                (double)jpegFrame.Length / 1024);
        }

        var deadClients = new List<string>();

        foreach (var (id, entry) in _clients) {
            if (entry.Ws.State != WebSocketState.Open) {
                deadClients.Add(id);
                continue;
            }

            var frame = entry.Mode == StreamMode.Raw ? rawFrame : jpegFrame;
            if (frame == null) continue;

            // Skip clients that are still sending the previous frame (backpressure)
            if (!entry.SendLock.Wait(0)) {
                entry.SkippedFrames++;
                if (entry.SkippedFrames % 10 == 0) {
                    _logger.LogWarning("Client {Id} skipped {Count} frames (slow consumer)", id, entry.SkippedFrames);
                }
                continue;
            }

            try {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                sendCts.CancelAfter(SendTimeout);
                var sendStart = DateTime.UtcNow;
                await entry.Ws.SendAsync(frame, WebSocketMessageType.Binary, true, sendCts.Token);
                entry.LastSendDuration = DateTime.UtcNow - sendStart;
                entry.ConsecutiveFailures = 0;
                entry.SkippedFrames = 0;

                if (AdaptiveEnabled) ApplyAdaptiveLogic(id, entry);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                entry.ConsecutiveFailures++;
                _logger.LogWarning("Send to client {Id} timed out (failure {N}/{Max})",
                    id, entry.ConsecutiveFailures, MaxConsecutiveFailures);
                if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
                    deadClients.Add(id);
            } catch (WebSocketException ex) {
                entry.ConsecutiveFailures++;
                _logger.LogWarning("WebSocket error for client {Id}: {Msg} (failure {N}/{Max})",
                    id, ex.Message, entry.ConsecutiveFailures, MaxConsecutiveFailures);
                if (entry.ConsecutiveFailures >= MaxConsecutiveFailures)
                    deadClients.Add(id);
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Unexpected error sending to client {Id}", id);
                deadClients.Add(id);
            } finally {
                entry.SendLock.Release();
            }
        }

        foreach (var id in deadClients) {
            _logger.LogInformation("Removing dead client: {Id}", id);
            UnregisterClient(id);
        }
    }

    public byte[]? GetLatestJpeg(int quality = 85) {
        if (_latestImage == null) return null;
        return _latestJpeg ??= _latestImage.ToJpeg(quality);
    }

    public ImageBuffer? GetLatestImage() => _latestImage;

    public int ClientCount => _clients.Count;

    public void Dispose() {
        foreach (var (_, entry) in _clients) {
            try { entry.Ws.Dispose(); } catch { }
            entry.SendLock.Dispose();
        }
        _clients.Clear();
    }

    public enum StreamMode { Raw, Jpeg }

    private class ClientEntry {
        public System.Net.WebSockets.WebSocket Ws { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int ConsecutiveFailures { get; set; }
        public int SkippedFrames { get; set; }
        public StreamMode Mode { get; set; } = StreamMode.Jpeg; // Default to JPEG (works everywhere)

        /// <summary>The mode the client originally asked for — never overwritten by adaptive logic.</summary>
        public StreamMode RequestedMode { get; set; } = StreamMode.Jpeg;
        /// <summary>True when adaptive logic forced us off the requested mode.</summary>
        public bool AdaptiveDowngraded { get; set; }
        /// <summary>Rolling counters used by the adaptive bandwidth heuristic.</summary>
        public int SlowFrameStreak { get; set; }
        public int FastFrameStreak { get; set; }
        public TimeSpan LastSendDuration { get; set; }

        public ClientEntry(System.Net.WebSockets.WebSocket ws) => Ws = ws;
    }

    public void SetClientMode(string id, StreamMode mode) {
        if (_clients.TryGetValue(id, out var entry)) {
            entry.Mode = mode;
            entry.RequestedMode = mode;
            entry.AdaptiveDowngraded = false;
            entry.SlowFrameStreak = 0;
            entry.FastFrameStreak = 0;
            _logger.LogInformation("Client {Id} requested {Mode} stream mode", id, mode);
        }
    }

    private void ApplyAdaptiveLogic(string id, ClientEntry entry) {
        // Only adapt raw clients down to JPEG. Pure JPEG clients have no
        // cheaper fallback, so do nothing for them.
        if (entry.RequestedMode != StreamMode.Raw && !entry.AdaptiveDowngraded) {
            entry.SlowFrameStreak = 0;
            entry.FastFrameStreak = 0;
            return;
        }

        var lat = entry.LastSendDuration;

        if (entry.Mode == StreamMode.Raw && lat >= AdaptiveDowngradeLatency) {
            entry.SlowFrameStreak++;
            entry.FastFrameStreak = 0;
            if (entry.SlowFrameStreak >= AdaptiveDowngradeStreak) {
                entry.Mode = StreamMode.Jpeg;
                entry.AdaptiveDowngraded = true;
                entry.SlowFrameStreak = 0;
                _logger.LogWarning(
                    "Adaptive bandwidth: client {Id} raw→JPEG (last send {Ms}ms >= {Threshold}ms x{Streak})",
                    id, lat.TotalMilliseconds, AdaptiveDowngradeLatency.TotalMilliseconds,
                    AdaptiveDowngradeStreak);
            }
        } else if (entry.Mode == StreamMode.Jpeg && entry.AdaptiveDowngraded
                   && lat <= AdaptiveUpgradeLatency) {
            entry.FastFrameStreak++;
            entry.SlowFrameStreak = 0;
            if (entry.FastFrameStreak >= AdaptiveUpgradeStreak) {
                entry.Mode = StreamMode.Raw;
                entry.AdaptiveDowngraded = false;
                entry.FastFrameStreak = 0;
                _logger.LogInformation(
                    "Adaptive bandwidth: client {Id} restored to raw (last send {Ms}ms <= {Threshold}ms x{Streak})",
                    id, lat.TotalMilliseconds, AdaptiveUpgradeLatency.TotalMilliseconds,
                    AdaptiveUpgradeStreak);
            }
        } else {
            // Latency in the middle band — reset both streaks
            if (entry.Mode == StreamMode.Raw) entry.SlowFrameStreak = 0;
            if (entry.Mode == StreamMode.Jpeg) entry.FastFrameStreak = 0;
        }
    }

    /// <summary>Diagnostics endpoint for the adaptive logic.</summary>
    public IEnumerable<object> GetClientStats() {
        return _clients.Select(kv => new {
            id = kv.Key,
            requestedMode = kv.Value.RequestedMode.ToString(),
            currentMode = kv.Value.Mode.ToString(),
            adaptiveDowngraded = kv.Value.AdaptiveDowngraded,
            lastSendMs = (int)kv.Value.LastSendDuration.TotalMilliseconds,
            slowStreak = kv.Value.SlowFrameStreak,
            fastStreak = kv.Value.FastFrameStreak,
            skipped = kv.Value.SkippedFrames,
            failures = kv.Value.ConsecutiveFailures
        });
    }
}

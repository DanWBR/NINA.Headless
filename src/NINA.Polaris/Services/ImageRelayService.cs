using System.Collections.Concurrent;
using System.Net.WebSockets;
using NINA.Core.Enum;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Polaris.Services;

/// <summary>
/// Broadcasts captured frames to every connected
/// <c>/ws/image-stream</c> client. Each client is fanned out
/// independently, so a slow browser doesn't stall the rest.
///
/// FIELD-3: streaming is RAW-only (uint16 pixels + LZ4-compressed
/// header carrying the Bayer pattern, ~5-15 MB per frame, decoded
/// client-side by the WASM pipeline + WebGL2 stretch / debayer).
/// The old JPEG WS path was deleted because it baked AutoStretch
/// into the JPEG server-side, which silently disabled the operator's
/// Stretch / WB controls in the browser. Adaptive bandwidth went
/// with it -- the only downgrade target was JPEG. Slow consumers are
/// handled by per-client SendLock back-pressure (frame skip) instead
/// of format switch.
///
/// <see cref="GetLatestJpeg"/> stays for the static one-shot
/// thumbnail endpoints (gallery preview, livestack preview) -- those
/// want a pre-stretched JPEG and are decoupled from the live WS path.
///
/// Holds the most recent <see cref="ImageBuffer"/> so a freshly-
/// connected client can immediately render the last frame without
/// waiting for the next capture.
/// </summary>
public class ImageRelayService : IDisposable {
    private readonly ConcurrentDictionary<string, ClientEntry> _clients = new();
    private readonly ILogger<ImageRelayService> _logger;
    private readonly ProfileService? _profiles;
    private ImageBuffer? _latestImage;
    private byte[]? _latestJpeg;

    /// <summary>The most recently relayed frame, as a decoded
    /// ushort[] pixel buffer with width/height. Null until the first
    /// capture lands. Consumed by post-processing endpoints (e.g.
    /// /api/focus/bahtinov) that want to analyse the current scene
    /// without forcing a duplicate capture. Lifetime: replaced on
    /// every RelayImageAsync call.</summary>
    public ImageBuffer? LatestImage => _latestImage;

    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private const int MaxConsecutiveFailures = 3;

    public ImageRelayService(ILogger<ImageRelayService> logger) {
        _logger = logger;
    }

    /// <summary>FIELD-2: DI overload that wires the active rig's
    /// <c>BayerPatternOverride</c> into every relayed frame. Kept as a
    /// second constructor so existing test code that builds the service
    /// without a profile still compiles and runs. In Program.cs the
    /// container picks this overload because the greediest matching
    /// constructor wins.</summary>
    public ImageRelayService(ILogger<ImageRelayService> logger,
                              ProfileService profiles) {
        _logger = logger;
        _profiles = profiles;
    }

    /// <summary>FIELD3-2: build a new IImageData with rows reversed
    /// when the active rig's VerticalFlipImage toggle is true. Returns
    /// the original IImageData unchanged when off / no profile
    /// resolved. The flip is a simple row-by-row reversal of the
    /// ushort[] buffer; ImageProperties + MetaData are shared by
    /// reference because nothing downstream mutates them. Cheap on a
    /// modern CPU (4096x4096 @ 16 bit ~ 32 MB row swap, ~5 ms).</summary>
    private IImageData ApplyVerticalFlipIfEnabled(IImageData source) {
        if (!(_profiles?.ActiveEquipmentProfile?.VerticalFlipImage ?? false)) return source;
        var w = source.Properties.Width;
        var h = source.Properties.Height;
        if (w <= 0 || h <= 0 || source.Data == null || source.Data.Length != w * h) return source;
        var flipped = new ushort[source.Data.Length];
        for (int y = 0; y < h; y++) {
            var srcRow = y * w;
            var dstRow = (h - 1 - y) * w;
            Array.Copy(source.Data, srcRow, flipped, dstRow, w);
        }
        return new NINA.Image.ImageData.BaseImageData(flipped, source.Properties, source.MetaData);
    }

    /// <summary>Resolve the active rig's Bayer override (if any) to a
    /// concrete enum value. Returns null when:
    ///   - No ProfileService injected (legacy ctor path / tests)
    ///   - No active rig
    ///   - Override is null / empty / "Auto" (honour the source)
    ///   - String doesn't parse to a known pattern (graceful fall
    ///     through to the source-reported value)
    /// </summary>
    private BayerPatternEnum? ResolveBayerOverride() {
        var raw = _profiles?.ActiveEquipmentProfile?.BayerPatternOverride;
        if (string.IsNullOrWhiteSpace(raw)) return null;
        if (string.Equals(raw, "Auto", StringComparison.OrdinalIgnoreCase)) return null;
        if (Enum.TryParse<BayerPatternEnum>(raw, ignoreCase: true, out var parsed)
                && parsed != BayerPatternEnum.None
                && parsed != BayerPatternEnum.Auto) {
            return parsed;
        }
        return null;
    }

    public void RegisterClient(string id, System.Net.WebSockets.WebSocket ws) {
        _clients[id] = new ClientEntry(ws);
        _logger.LogInformation("Image stream client registered: {Id} (total: {Count})", id, _clients.Count);
    }

    public void UnregisterClient(string id) {
        if (_clients.TryRemove(id, out var entry)) {
            var wasCapable = entry.WasmCapable;
            entry.SendLock.Dispose();
            _logger.LogInformation("Image stream client removed: {Id} (remaining: {Count})", id, _clients.Count);
            // If we just lost our last WASM-capable client, the server
            // should drop back to Full mode so capture clients without
            // WASM keep getting the accumulated preview from us.
            if (wasCapable) RaiseWasmCount();
        }
    }

    /// <summary>
    /// Broadcast a frame to every connected /ws/image-stream client.
    /// The default kind is <see cref="FrameKind.Live"/> — backwards
    /// compatible with every caller that doesn't say otherwise.
    /// </summary>
    public Task RelayImageAsync(IImageData imageData, CancellationToken ct = default)
        => RelayImageAsync(imageData, FrameKind.Live, ct);

    /// <summary>
    /// Legacy bool overload kept so we don't have to touch every
    /// caller in one pass. <paramref name="stackable"/>=false maps to
    /// <see cref="FrameKind.Preview"/>, the closest equivalent of the
    /// old "this is a one-off snap" intent.
    /// </summary>
    public Task RelayImageAsync(IImageData imageData, bool stackable, CancellationToken ct = default)
        => RelayImageAsync(imageData, stackable ? FrameKind.Live : FrameKind.Preview, ct);

    public async Task RelayImageAsync(IImageData imageData, FrameKind kind, CancellationToken ct = default) {
        var frameKind = (int)kind;
        // FIELD3-2: optional vertical flip. Some camera drivers
        // (SV405CC indi_svbony_ccd notably) deliver TOP-DOWN buffers
        // without the corresponding FITS-spec axis flip; our reader
        // loads sequentially, so the downstream Bayer pattern is
        // offset by one row -> red/green checkerboard after debayer.
        // The per-rig VerticalFlipImage profile toggle (FIELD3-2)
        // mirrors the buffer Y-direction here so RGGB stays RGGB
        // through the rest of the pipeline (live preview AND server-
        // side accumulator, since LiveStackingService's integrated
        // output re-enters this method).
        var sourceData = ApplyVerticalFlipIfEnabled(imageData);
        // FIELD-2: apply the active rig's Bayer pattern override (if
        // any) before we hand the buffer to the JPEG encoder / raw
        // header writer. Drivers that emit a wrong or missing
        // BAYERPAT keyword (notably SVBONY's indi_svbony_ccd at the
        // time of writing) would otherwise feed mono into the
        // client-side debayer and the live stack comes out grey. The
        // override is the operator's "I know better than the driver"
        // escape hatch.
        var buffer = ImageBuffer.FromImageData(sourceData, ResolveBayerOverride());
        _latestImage = buffer;
        _latestJpeg = null;

        if (_clients.IsEmpty) return;

        // FIELD-3: streaming is RAW-only. The JPEG WS path was
        // deleted (it baked AutoStretch into the JPEG server-side,
        // which silently neutered the operator's Stretch / WB
        // sliders -- the user reported this from the field). The
        // one-shot JPEG endpoints (/api/image/latest/preview,
        // /api/livestack/preview) still call GetLatestJpeg() to
        // serve gallery thumbnails, that's a different consumer
        // that wants a static stretched image. Per-frame WS goes
        // RAW + LZ4 + client-side WebGL stretch every time.
        var header = buffer.GetStreamHeader(frameKind);
        var compressed = buffer.ToLz4Compressed();

        _logger.LogInformation(
            "Relaying image {W}x{H} ({BitDepth}-bit): {RawMB:F1}MB raw -> {CompMB:F1}MB LZ4 ({Ratio:F1}x) to {Count} clients",
            buffer.Width, buffer.Height, buffer.BitDepth,
            (double)imageData.Data.Length * 2 / (1024 * 1024),
            (double)compressed.Length / (1024 * 1024),
            (double)imageData.Data.Length * 2 / Math.Max(compressed.Length, 1),
            _clients.Count);

        var rawFrame = new byte[4 + header.Length + compressed.Length];
        BitConverter.GetBytes(header.Length).CopyTo(rawFrame, 0);
        header.CopyTo(rawFrame, 4);
        compressed.CopyTo(rawFrame, 4 + header.Length);

        var deadClients = new List<string>();

        foreach (var (id, entry) in _clients) {
            if (entry.Ws.State != WebSocketState.Open) {
                deadClients.Add(id);
                continue;
            }

            var frame = rawFrame;

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
                // FIELD-3: adaptive bandwidth removed. The downgrade
                // target was JPEG, which is the path we just deleted.
                // Slow clients are now handled by the SendLock skip
                // above (back-pressure) -- they drop frames instead of
                // switching format. That's the right trade-off for
                // RAW-only streaming.
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
        var img = _latestImage;
        // Skip the encode when no real frame is buffered yet, the
        // initial state has a 0x0 ImageBuffer (placeholder), and
        // JpegHelper rightly refuses it. Surfacing null lets the
        // endpoint return 404 instead of crashing the request.
        if (img == null || img.Width <= 0 || img.Height <= 0) return null;
        try {
            return _latestJpeg ??= img.ToJpeg(quality);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "JPEG encode of {W}x{H} frame failed", img.Width, img.Height);
            return null;
        }
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

    /// <summary>FIELD-3: only <see cref="StreamMode.Raw"/> is used
    /// on the WS path now. The enum + Jpeg value stay for binary
    /// back-compat with any callers that haven't been re-built yet
    /// (the legacy SetClientMode silently rejects requests for Jpeg).
    /// </summary>
    public enum StreamMode { Raw, Jpeg }

    private class ClientEntry {
        public System.Net.WebSockets.WebSocket Ws { get; }
        public SemaphoreSlim SendLock { get; } = new(1, 1);
        public int ConsecutiveFailures { get; set; }
        public int SkippedFrames { get; set; }
        // FIELD-3: every client is RAW now. Field kept so the existing
        // GetClientStats() payload stays shape-compatible.
        public StreamMode Mode { get; set; } = StreamMode.Raw;
        public StreamMode RequestedMode { get; set; } = StreamMode.Raw;
        public TimeSpan LastSendDuration { get; set; }

        // CLST-5: client-reported WASM live-stack capability. Drives
        // the server-side LiveStackingService.Mode switch.
        public bool WasmCapable { get; set; }
        public string? WasmVersion { get; set; }

        public ClientEntry(System.Net.WebSockets.WebSocket ws) => Ws = ws;
    }

    /// <summary>FIELD-3: legacy compat shim. JPEG mode is gone, so a
    /// Jpeg request is silently coerced to Raw with a debug log. The
    /// method stays because ImageStreamHandler still calls it on the
    /// handshake message.</summary>
    public void SetClientMode(string id, StreamMode mode) {
        if (_clients.TryGetValue(id, out var entry)) {
            if (mode == StreamMode.Jpeg) {
                _logger.LogDebug(
                    "Client {Id} requested JPEG stream; coerced to Raw (JPEG WS streaming removed)", id);
            }
            entry.Mode = StreamMode.Raw;
            entry.RequestedMode = StreamMode.Raw;
        }
    }

    /// <summary>CLST-5: record whether a client has the WASM
    /// live-stack module loaded. Any change recomputes the aggregate
    /// count and raises <see cref="WasmCapableCountChanged"/> so
    /// LiveStackingService can flip into MetricsOnly mode when at
    /// least one capable client connects.</summary>
    public void SetClientCapability(string id, bool wasm, string? wasmVersion) {
        if (_clients.TryGetValue(id, out var entry)) {
            var wasCapable = entry.WasmCapable;
            entry.WasmCapable = wasm;
            entry.WasmVersion = wasmVersion;
            if (wasCapable != wasm) {
                _logger.LogInformation("Client {Id} WASM capability {Wasm} (version {Ver})",
                    id, wasm, wasmVersion ?? "unknown");
                RaiseWasmCount();
            }
        }
    }

    /// <summary>Number of currently-connected clients that have
    /// reported wasm:true via the <c>client-capability</c> WS message.
    /// The handshake in ImageStreamHandler routes incoming messages
    /// to <see cref="SetClientCapability"/>.</summary>
    public int WasmCapableClientCount => _clients.Values.Count(c => c.WasmCapable);

    /// <summary>Fires when the aggregate WASM-capable client count
    /// changes (registration, unregistration, or capability update).
    /// LiveStackingService subscribes to flip its Mode property
    /// between Full (count == 0) and MetricsOnly (count >= 1).</summary>
    public event Action<int>? WasmCapableCountChanged;

    private void RaiseWasmCount() {
        try { WasmCapableCountChanged?.Invoke(WasmCapableClientCount); }
        catch (Exception ex) { _logger.LogDebug(ex, "WasmCapableCountChanged handler threw"); }
    }

    /// <summary>Diagnostics endpoint. FIELD-3: adaptive-bandwidth
    /// counters removed; the kept fields are the ones that still
    /// matter for slow-consumer triage (skipped frames + WS send
    /// failures).</summary>
    public IEnumerable<object> GetClientStats() {
        return _clients.Select(kv => new {
            id = kv.Key,
            currentMode = kv.Value.Mode.ToString(),
            lastSendMs = (int)kv.Value.LastSendDuration.TotalMilliseconds,
            skipped = kv.Value.SkippedFrames,
            failures = kv.Value.ConsecutiveFailures
        });
    }
}

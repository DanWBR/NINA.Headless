using NINA.Core.Enum;
using NINA.Image.FileFormat.FITS;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Protocol;

namespace NINA.INDI.Devices;

public class IndiCamera : ICamera {
    private readonly IndiClient _client;
    private TaskCompletionSource<IImageData>? _exposureTcs;
    // Native CCD_VIDEO_STREAM subscribers — added by CameraStreamService
    // when a native stream is active. Frames arrive via OnBlobReceived
    // and fan out to every subscriber. Concurrent for safety against
    // late-arriving BLOBs after Stop.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, Action<IImageData>> _streamSubscribers = new();
    private int _nextSubscriberId;
    private volatile bool _isStreaming;
    // Counter of stream BLOBs that parsed as empty (FITSReader returned
    // Width=0 / Height=0 — typically a driver that doesn't actually
    // emit FITS under CCD_VIDEO_STREAM). CameraStreamService reads this
    // to decide whether native streaming is producing usable frames or
    // whether it should bail out to loop mode.
    private int _emptyStreamFrameCount;
    public int EmptyStreamFrameCount => _emptyStreamFrameCount;

    public string DeviceName { get; }
    public bool IsConnected => _client.IsConnected;

    public CameraStates State {
        get {
            var prop = _client.GetProperty(DeviceName, "CCD_EXPOSURE");
            if (prop == null) return CameraStates.NoState;
            return prop.State switch {
                IndiPropertyState.Busy => CameraStates.Exposing,
                IndiPropertyState.Ok => CameraStates.Idle,
                IndiPropertyState.Alert => CameraStates.Error,
                _ => CameraStates.Idle
            };
        }
    }

    public double Temperature => _client.GetNumber(DeviceName, "CCD_TEMPERATURE", "CCD_TEMPERATURE_VALUE");
    public int BinX => (int)_client.GetNumber(DeviceName, "CCD_BINNING", "HOR_BIN");
    public int BinY => (int)_client.GetNumber(DeviceName, "CCD_BINNING", "VER_BIN");
    public bool CoolerOn => _client.GetSwitch(DeviceName, "CCD_COOLER", "COOLER_ON");
    public double CoolerPower => _client.GetNumber(DeviceName, "CCD_COOLER_POWER", "CCD_COOLER_VALUE");
    public int MaxX => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_MAX_X");
    public int MaxY => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_MAX_Y");
    public double PixelSizeX => _client.GetNumber(DeviceName, "CCD_INFO", "CCD_PIXEL_SIZE_X");
    public double PixelSizeY => _client.GetNumber(DeviceName, "CCD_INFO", "CCD_PIXEL_SIZE_Y");
    public int BitDepth => (int)_client.GetNumber(DeviceName, "CCD_INFO", "CCD_BITSPERPIXEL");

    // INDI cameras don't surface gain in a standardised property — the
    // CCD_CONTROLS group varies by driver (gain / Gain / GAIN). Plumb a
    // best-effort read here and return 0 when nothing matches.
    public int Gain => (int)_client.GetNumber(DeviceName, "CCD_CONTROLS", "Gain");

    // ISO is not part of the INDI CCD spec — astronomy cameras report
    // analogue gain instead. Empty list signals the UI to hide the ISO
    // dropdown for INDI cameras.
    public IReadOnlyList<int> IsoOptions => Array.Empty<int>();
    public int SelectedIso => 0;

    /// <summary>Per-instance capabilities — SupportsVideoStream gets
    /// recomputed lazily from whether the driver advertises
    /// <c>CCD_VIDEO_STREAM</c> (most ZWO/QHY/gphoto drivers do).
    /// SupportsWhiteBalance flips on when CCD_CONTROLS exposes
    /// <c>WB_R</c> + <c>WB_B</c> elements (typical for ZWO/QHY OSC
    /// cameras, absent on mono).</summary>
    public CameraCapabilities Capabilities {
        get {
            var supportsStream = _client.GetProperty(DeviceName, "CCD_VIDEO_STREAM") != null;
            var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
            var supportsWb = ctrl?.Values.ContainsKey("WB_R") == true
                          && ctrl.Values.ContainsKey("WB_B");
            return CameraCapabilities.Astro with {
                SupportsVideoStream = supportsStream,
                SupportsWhiteBalance = supportsWb
            };
        }
    }

    /// <summary>Live WB_R reading; 50 (driver-typical neutral) when not exposed.</summary>
    public double WhiteBalanceR {
        get {
            var v = _client.GetNumber(DeviceName, "CCD_CONTROLS", "WB_R");
            return v > 0 ? v : 50;
        }
    }
    public double WhiteBalanceB {
        get {
            var v = _client.GetNumber(DeviceName, "CCD_CONTROLS", "WB_B");
            return v > 0 ? v : 50;
        }
    }

    /// <summary>Write gain into CCD_CONTROLS only if the driver actually
    /// advertises that property + a matching element. Some drivers
    /// (notably indi_simulator_ccd) never publish CCD_CONTROLS at all,
    /// and sending it triggers a "Property CCD_CONTROLS is not defined"
    /// dispatch error in indiserver's log. Also handles driver-specific
    /// casing — Gain (most), gain (a few), GAIN (rare).</summary>
    private async Task TrySetGainAsync(int gain, CancellationToken ct) {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl == null) return;   // driver doesn't expose CCD_CONTROLS (e.g. CCD Simulator)
        string? key = null;
        foreach (var candidate in new[] { "Gain", "gain", "GAIN" }) {
            if (ctrl.Values.ContainsKey(candidate)) { key = candidate; break; }
        }
        if (key == null) return;   // CCD_CONTROLS exists but no gain element
        try {
            await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS",
                new Dictionary<string, double> { [key] = gain }, ct);
        } catch { /* driver rejected the value (out of range?) — non-fatal */ }
    }

    /// <summary>Writes WB_R and WB_B into CCD_CONTROLS. Silent skip if
    /// the driver doesn't have one of the keys.</summary>
    public async Task SetWhiteBalanceAsync(double red, double blue, CancellationToken ct = default) {
        var ctrl = _client.GetProperty(DeviceName, "CCD_CONTROLS") as IndiNumberProperty;
        if (ctrl == null) return;
        var values = new Dictionary<string, double>();
        if (ctrl.Values.ContainsKey("WB_R")) values["WB_R"] = red;
        if (ctrl.Values.ContainsKey("WB_B")) values["WB_B"] = blue;
        if (values.Count == 0) return;
        await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS", values, ct);
    }

    public bool IsStreaming => _isStreaming;

    public IDisposable SubscribeVideoFrames(Action<IImageData> handler) {
        var id = System.Threading.Interlocked.Increment(ref _nextSubscriberId);
        _streamSubscribers[id] = handler;
        return new StreamSubscription(this, id);
    }

    private sealed class StreamSubscription : IDisposable {
        private readonly IndiCamera _cam;
        private readonly int _id;
        public StreamSubscription(IndiCamera cam, int id) { _cam = cam; _id = id; }
        public void Dispose() => _cam._streamSubscribers.TryRemove(_id, out _);
    }

    /// <summary>Toggle the driver's <c>CCD_VIDEO_STREAM</c> switch ON.
    /// Frame cadence is whatever the driver chooses (often configurable
    /// via <c>STREAMING_EXPOSURE</c> + <c>FPS</c> properties on the device).</summary>
    public async Task StartVideoStreamAsync(VideoStreamOptions? opts = null, CancellationToken ct = default) {
        if (!Capabilities.SupportsVideoStream)
            throw new NotSupportedException(
                $"INDI device {DeviceName} does not expose CCD_VIDEO_STREAM. Use loop mode instead.");

        // Honour optional per-stream overrides where the driver exposes
        // the matching properties. Silently skip when absent — different
        // drivers expose different subset of streaming knobs.
        if (opts?.ExposureSeconds is double exp && exp > 0) {
            try {
                await _client.SetNumberAsync(DeviceName, "STREAMING_EXPOSURE",
                    new Dictionary<string, double> { ["STREAMING_EXPOSURE_VALUE"] = exp }, ct);
            } catch { /* property may not exist on this driver */ }
        }
        if (opts?.Gain is int g) {
            await TrySetGainAsync(g, ct);
        }

        Interlocked.Exchange(ref _emptyStreamFrameCount, 0);
        _isStreaming = true;
        await _client.SetSwitchAsync(DeviceName, "CCD_VIDEO_STREAM",
            new Dictionary<string, bool> { ["STREAM_ON"] = true, ["STREAM_OFF"] = false }, ct);
    }

    public async Task StopVideoStreamAsync(CancellationToken ct = default) {
        if (!_isStreaming) return;
        _isStreaming = false;
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_VIDEO_STREAM",
                new Dictionary<string, bool> { ["STREAM_ON"] = false, ["STREAM_OFF"] = true }, ct);
        } catch { /* driver may already be torn down; nothing to do */ }
    }

    public IndiCamera(IndiClient client, string deviceName) {
        _client = client;
        DeviceName = deviceName;

        _client.BlobReceived += OnBlobReceived;
        _client.PropertyChanged += OnPropertyChanged;
    }

    public async Task ConnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = true, ["DISCONNECT"] = false }, ct);
        await _client.EnableBlobAsync(DeviceName, ct);
    }

    public async Task DisconnectAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CONNECTION",
            new Dictionary<string, bool> { ["CONNECT"] = false, ["DISCONNECT"] = true }, ct);
    }

    public async Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) {
        await _client.SetNumberAsync(DeviceName, "CCD_BINNING",
            new Dictionary<string, double> { ["HOR_BIN"] = binX, ["VER_BIN"] = binY }, ct);
    }

    public async Task SetTemperatureAsync(double temperature, CancellationToken ct = default) {
        // CCD_TEMPERATURE is read-only on uncooled cameras (ZWO ASI715MC,
        // most planetary CMOS). On those drivers writing it raises a
        // "Cannot set read-only property" dispatch error. Probe the
        // property — if it exists at all on a cooled camera, it's
        // writable; if missing we don't have a cooler to talk to.
        var prop = _client.GetProperty(DeviceName, "CCD_TEMPERATURE") as IndiNumberProperty;
        if (prop == null) return;
        try {
            await _client.SetNumberAsync(DeviceName, "CCD_TEMPERATURE",
                new Dictionary<string, double> { ["CCD_TEMPERATURE_VALUE"] = temperature }, ct);
        } catch { /* read-only or out-of-range on this driver — silent */ }
    }

    public async Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        // CCD_COOLER doesn't exist on uncooled cameras. Without this
        // guard the indiserver log fills with "Property CCD_COOLER is
        // not defined in ZWO CCD ASI715MC" on every UI toggle.
        var prop = _client.GetProperty(DeviceName, "CCD_COOLER") as IndiSwitchProperty;
        if (prop == null) return;
        try {
            await _client.SetSwitchAsync(DeviceName, "CCD_COOLER",
                new Dictionary<string, bool> { ["COOLER_ON"] = on, ["COOLER_OFF"] = !on }, ct);
        } catch { /* driver rejected the switch — silent */ }
    }

    /// <summary>INDI astronomy cameras don't expose ISO. No-op.</summary>
    public Task SetIsoAsync(int iso, CancellationToken ct = default) => Task.CompletedTask;

    public async Task<IImageData> CaptureAsync(double exposureSeconds, CaptureOptions? opts = null, CancellationToken ct = default) {
        _exposureTcs = new TaskCompletionSource<IImageData>();

        using var reg = ct.Register(() => _exposureTcs.TrySetCanceled());

        // opts overrides honoured per-capture so the sequencer can set
        // binning + gain inline without a separate round-trip.
        if (opts?.BinX is int bx && opts.BinY is int by) {
            await SetBinningAsync(bx, by, ct);
        }
        if (opts?.Gain is int g) {
            await TrySetGainAsync(g, ct);
        }

        await _client.SetNumberAsync(DeviceName, "CCD_EXPOSURE",
            new Dictionary<string, double> { ["CCD_EXPOSURE_VALUE"] = exposureSeconds }, ct);

        return await _exposureTcs.Task;
    }

    public async Task AbortExposureAsync(CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CCD_ABORT_EXPOSURE",
            new Dictionary<string, bool> { ["ABORT"] = true }, ct);
        _exposureTcs?.TrySetCanceled();
    }

    /// <summary>Writes CCD_FRAME (X, Y, WIDTH, HEIGHT). Passing w=0 OR
    /// h=0 resets to the full sensor (Max X/Y).</summary>
    public async Task SetSubframeAsync(int x, int y, int width, int height, CancellationToken ct = default) {
        if (width <= 0 || height <= 0) {
            x = 0; y = 0;
            width = MaxX > 0 ? MaxX : 0;
            height = MaxY > 0 ? MaxY : 0;
        }
        await _client.SetNumberAsync(DeviceName, "CCD_FRAME",
            new Dictionary<string, double> {
                ["X"] = x, ["Y"] = y,
                ["WIDTH"] = width, ["HEIGHT"] = height
            }, ct);
    }

    private void OnBlobReceived(IndiBlobProperty blob) {
        if (blob.Device != DeviceName) return;

        foreach (var (name, element) in blob.Values) {
            if (element.Data == null || element.Data.Length == 0) continue;

            try {
                var imageData = FITSReader.Read(element.Data);

                // Some INDI drivers (notably indi_asi_ccd under
                // CCD_VIDEO_STREAM mode) emit BLOBs that aren't a
                // proper FITS file — just a raw uint16 buffer. The
                // reader doesn't throw on those; it returns a
                // BaseImageData with Width=0 / Height=0 / no pixels.
                // Dispatching that downstream means CameraStreamService
                // happily fires "frames" at 5fps, ImageRelayService
                // broadcasts header-only 24-byte WS messages, and the
                // browser canvas stays black even though every counter
                // says video is working. Treat zero-sized parses as
                // failures so the streaming-fallback logic in
                // CameraStreamService kicks in.
                if (imageData.Properties.Width <= 0
                    || imageData.Properties.Height <= 0
                    || imageData.Data == null
                    || imageData.Data.Length == 0) {
                    if (_isStreaming) {
                        Interlocked.Increment(ref _emptyStreamFrameCount);
                    } else {
                        _exposureTcs?.TrySetException(
                            new InvalidDataException(
                                $"INDI BLOB from {DeviceName} parsed as empty " +
                                $"(driver may not emit FITS under CCD_VIDEO_STREAM)"));
                    }
                    continue;
                }

                imageData.MetaData.Camera.Name = DeviceName;
                imageData.MetaData.Camera.Temperature = Temperature;
                imageData.MetaData.Camera.BinX = (short)BinX;
                imageData.MetaData.Camera.BinY = (short)BinY;
                imageData.MetaData.Camera.PixelSizeX = PixelSizeX;
                imageData.MetaData.Camera.PixelSizeY = PixelSizeY;

                // Native streaming path: when CCD_VIDEO_STREAM is ON the
                // driver fires BLOBs continuously at its native cadence
                // (10-30 fps typical). Fan them out to every subscriber
                // and bypass the exposure-completion TCS so a long-pending
                // CaptureAsync isn't accidentally resolved with a stream
                // frame.
                if (_isStreaming) {
                    foreach (var sub in _streamSubscribers.Values) {
                        try { sub(imageData); } catch { /* one subscriber's bug shouldn't kill the loop */ }
                    }
                } else {
                    _exposureTcs?.TrySetResult(imageData);
                }
            } catch (Exception ex) {
                if (!_isStreaming) _exposureTcs?.TrySetException(ex);
                // While streaming, a bad frame is just a dropped frame —
                // don't poison the whole stream.
            }
        }
    }

    private void OnPropertyChanged(string device, IndiProperty prop) {
        if (device != DeviceName) return;
        // Could raise events for UI updates here
    }
}

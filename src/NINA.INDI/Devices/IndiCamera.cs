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
    /// <c>CCD_VIDEO_STREAM</c> (most ZWO/QHY/gphoto drivers do).</summary>
    public CameraCapabilities Capabilities {
        get {
            var supportsStream = _client.GetProperty(DeviceName, "CCD_VIDEO_STREAM") != null;
            return CameraCapabilities.Astro with { SupportsVideoStream = supportsStream };
        }
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
            try {
                await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS",
                    new Dictionary<string, double> { ["Gain"] = g }, ct);
            } catch { }
        }

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
        await _client.SetNumberAsync(DeviceName, "CCD_TEMPERATURE",
            new Dictionary<string, double> { ["CCD_TEMPERATURE_VALUE"] = temperature }, ct);
    }

    public async Task SetCoolerAsync(bool on, CancellationToken ct = default) {
        await _client.SetSwitchAsync(DeviceName, "CCD_COOLER",
            new Dictionary<string, bool> { ["COOLER_ON"] = on, ["COOLER_OFF"] = !on }, ct);
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
            await _client.SetNumberAsync(DeviceName, "CCD_CONTROLS",
                new Dictionary<string, double> { ["Gain"] = g }, ct);
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

    private void OnBlobReceived(IndiBlobProperty blob) {
        if (blob.Device != DeviceName) return;

        foreach (var (name, element) in blob.Values) {
            if (element.Data == null || element.Data.Length == 0) continue;

            try {
                var imageData = FITSReader.Read(element.Data);

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

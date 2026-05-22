using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Devices;

namespace NINA.Headless.Services;

public class EquipmentManager : IDisposable {
    private readonly IndiClient _indiClient;
    private readonly ILogger<EquipmentManager> _logger;

    /// <summary>Currently-selected camera, regardless of backend.
    /// Concrete implementations are <see cref="IndiCamera"/> for
    /// astronomy CCDs over INDI and the per-vendor DSLR drivers
    /// (CanonEdsdkCamera, NikonSdkCamera, SonySdkCamera) when those
    /// land. The capture endpoints and status broadcaster only depend
    /// on the <see cref="ICamera"/> contract.</summary>
    public ICamera? Camera { get; private set; }

    /// <summary>Camera driver kind currently bound to <see cref="Camera"/>.
    /// Mirrors <c>EquipmentProfile.CameraDriver</c>. Null when no
    /// camera is selected.</summary>
    public string? CameraDriver { get; private set; }

    public IndiTelescope? Telescope { get; private set; }
    public IndiFocuser? Focuser { get; private set; }
    public IndiFilterWheel? FilterWheel { get; private set; }
    public IndiRotator? Rotator { get; private set; }
    public IndiFlatDevice? FlatDevice { get; private set; }
    public IndiDome? Dome { get; private set; }
    public IndiWeather? Weather { get; private set; }

    public EquipmentManager(IndiClient indiClient, ILogger<EquipmentManager> logger) {
        _indiClient = indiClient;
        _logger = logger;
        _indiClient.DeviceFound += OnDeviceFound;
    }

    public IEnumerable<string> GetDeviceNames() => _indiClient.GetDeviceNames();

    /// <summary>Legacy entry-point — assumes the INDI driver. Kept
    /// for backwards compatibility with the existing capture-endpoint
    /// route <c>POST /api/camera/select/{deviceName}</c>.</summary>
    public ICamera SelectCamera(string deviceName)
        => SelectCamera("indi", deviceName);

    /// <summary>Select a camera by driver kind + driver-specific
    /// device id. INDI cameras are addressed by INDI device name;
    /// Alpaca cameras are addressed by <c>host:port:devnum</c>;
    /// vendor SDK cameras (Canon/Nikon/Sony) are addressed by the
    /// serial number reported by the SDK enumeration call.</summary>
    public ICamera SelectCamera(string driver, string deviceId) {
        driver = (driver ?? "indi").Trim().ToLowerInvariant();
        Camera = driver switch {
            "indi" => new IndiCamera(_indiClient, deviceId),
            "canon-edsdk" => CreateCanonCamera(deviceId),
            "nikon-sdk"   => CreateNikonCamera(deviceId),
            "sony-sdk"    => new NINA.Camera.SonySdk.SonySdkCamera(deviceId),
            // Alpaca lands in a follow-up commit.
            _ => throw new NotSupportedException(
                $"Camera driver '{driver}' is not implemented yet. " +
                "Use 'indi' (or install the matching vendor SDK)."),
        };
        CameraDriver = driver;
        _logger.LogInformation("Camera selected: driver={Driver}, id={DeviceId}",
            driver, deviceId);
        return Camera;
    }

    private static ICamera CreateCanonCamera(string deviceId) {
        if (!OperatingSystem.IsWindows()) {
            throw new NotSupportedException(
                "Canon EDSDK only runs on Windows. On Linux, use the INDI " +
                "gphoto driver instead — see docs/dslr-linux.md.");
        }
        return new NINA.Camera.CanonEdsdk.CanonEdsdkCamera(deviceId);
    }

    private static ICamera CreateNikonCamera(string deviceId) {
        if (!OperatingSystem.IsWindows()) {
            throw new NotSupportedException(
                "Nikon SDK only runs on Windows. On Linux, use the INDI " +
                "gphoto driver instead — see docs/dslr-linux.md.");
        }
        return new NINA.Camera.NikonSdk.NikonSdkCamera(deviceId);
    }

    /// <summary>List of camera driver kinds the host can offer. Always
    /// includes <c>indi</c>; vendor SDK drivers are listed only when
    /// the matching native dependency is present on the current OS.</summary>
    public IReadOnlyList<CameraDriverInfo> GetAvailableCameraDrivers() {
        var list = new List<CameraDriverInfo> {
            new("indi", "INDI", Available: true,
                Description: "Standard astronomy cameras via INDI server."),
            new("alpaca", "Alpaca (ASCOM)", Available: false,
                Description: "ASCOM-over-HTTP cameras. Wiring pending."),
        };
        if (OperatingSystem.IsWindows()) {
            // Canon EDSDK + Nikon MAID/Imaging SDKs are Windows-only.
            // Probe each so the UI can show a green check when the
            // native DLLs are reachable on the search path or a
            // "download" banner when they aren't.
            list.Add(new("canon-edsdk", "Canon EOS (EDSDK)",
                Available: NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.IsAvailable,
                Description: "Canon DSLR / mirrorless. Requires EDSDK DLLs."));
            list.Add(new("nikon-sdk", "Nikon (MAID SDK)",
                Available: NINA.Camera.NikonSdk.NikonSdkRegistry.IsAvailable,
                Description: "Nikon DSLR / Z mirrorless. Skeleton driver — " +
                    "see docs/dslr-windows-nikon.md to wire up the actual SDK."));
        }
        // Sony Camera Remote SDK ships native binaries for both
        // Windows and Linux, so it shows up on every OS — including
        // Raspberry Pi via the SDK's linux-arm64 build.
        list.Add(new("sony-sdk", "Sony (Camera Remote SDK)",
            Available: NINA.Camera.SonySdk.SonySdkRegistry.IsAvailable,
            Description: "Sony α series (Windows + Linux). Skeleton driver — " +
                "see docs/dslr-windows-sony.md to wire up the actual SDK."));
        return list;
    }

    /// <summary>Enumerate available cameras for a given driver kind.
    /// Returns INDI device names for INDI; vendor-specific discovery
    /// for the SDK drivers; empty list when the driver isn't
    /// supported on the current platform.</summary>
    public IReadOnlyList<DiscoveredCamera> GetDiscoveredCamerasFor(string driver) {
        driver = (driver ?? "indi").Trim().ToLowerInvariant();
        if (driver == "indi") {
            return GetDeviceNames()
                .Select(n => new DiscoveredCamera(n, n, n))
                .ToList();
        }
        if (driver == "canon-edsdk" && OperatingSystem.IsWindows()) {
            try {
                return EnumerateCanonCameras();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Canon EDSDK discovery failed");
                return Array.Empty<DiscoveredCamera>();
            }
        }
        if (driver == "nikon-sdk" && OperatingSystem.IsWindows()) {
            try {
                return EnumerateNikonCameras();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Nikon SDK discovery failed");
                return Array.Empty<DiscoveredCamera>();
            }
        }
        if (driver == "sony-sdk") {
            try {
                return NINA.Camera.SonySdk.SonySdkDiscovery.Enumerate()
                    .Select(e => new DiscoveredCamera(e.Id, e.Model, e.PortName))
                    .ToList();
            } catch (Exception ex) {
                _logger.LogWarning(ex, "Sony SDK discovery failed");
                return Array.Empty<DiscoveredCamera>();
            }
        }
        return Array.Empty<DiscoveredCamera>();
    }

    /// <summary>Windows-only inner helper so the platform analyzer
    /// is satisfied — the OS guard in the caller is implicit here via
    /// the attribute.</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<DiscoveredCamera> EnumerateCanonCameras()
        => NINA.Camera.CanonEdsdk.CanonEdsdkDiscovery.Enumerate()
            .Select(e => new DiscoveredCamera(e.Id, e.Model, e.PortName))
            .ToList();

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<DiscoveredCamera> EnumerateNikonCameras()
        => NINA.Camera.NikonSdk.NikonSdkDiscovery.Enumerate()
            .Select(e => new DiscoveredCamera(e.Id, e.Model, e.PortName))
            .ToList();

    public IndiTelescope SelectTelescope(string deviceName) {
        Telescope = new IndiTelescope(_indiClient, deviceName);
        _logger.LogInformation("Telescope selected: {Name}", deviceName);
        return Telescope;
    }

    public IndiFocuser SelectFocuser(string deviceName) {
        Focuser = new IndiFocuser(_indiClient, deviceName);
        _logger.LogInformation("Focuser selected: {Name}", deviceName);
        return Focuser;
    }

    public IndiFilterWheel SelectFilterWheel(string deviceName) {
        FilterWheel = new IndiFilterWheel(_indiClient, deviceName);
        _logger.LogInformation("Filter wheel selected: {Name}", deviceName);
        return FilterWheel;
    }

    public IndiRotator SelectRotator(string deviceName) {
        Rotator = new IndiRotator(_indiClient, deviceName);
        _logger.LogInformation("Rotator selected: {Name}", deviceName);
        return Rotator;
    }

    public IndiFlatDevice SelectFlatDevice(string deviceName) {
        FlatDevice = new IndiFlatDevice(_indiClient, deviceName);
        _logger.LogInformation("Flat device selected: {Name}", deviceName);
        return FlatDevice;
    }

    public IndiDome SelectDome(string deviceName) {
        Dome = new IndiDome(_indiClient, deviceName);
        _logger.LogInformation("Dome selected: {Name}", deviceName);
        return Dome;
    }

    public IndiWeather SelectWeather(string deviceName) {
        Weather = new IndiWeather(_indiClient, deviceName);
        _logger.LogInformation("Weather selected: {Name}", deviceName);
        return Weather;
    }

    public Dictionary<string, object> GetEquipmentStatus() {
        var status = new Dictionary<string, object>();

        status["indi"] = new {
            connected = _indiClient.IsConnected,
            host = _indiClient.Host,
            port = _indiClient.Port,
            deviceCount = _indiClient.Devices.Count
        };

        if (Camera != null) {
            // Sensor dimensions: pixel size is in micrometres, resolution in
            // pixels. width_mm = MaxX * PixelSizeX / 1000.
            var pxX = Camera.PixelSizeX;
            var pxY = Camera.PixelSizeY;
            var sensorWmm = Camera.MaxX > 0 && pxX > 0 ? Camera.MaxX * pxX / 1000.0 : 0;
            var sensorHmm = Camera.MaxY > 0 && pxY > 0 ? Camera.MaxY * pxY / 1000.0 : 0;

            status["camera"] = new {
                name = Camera.DeviceName,
                connected = Camera.IsConnected,
                state = Camera.State.ToString(),
                temperature = Safe(Camera.Temperature),
                coolerOn = Camera.CoolerOn,
                coolerPower = Safe(Camera.CoolerPower),
                binX = Camera.BinX,
                binY = Camera.BinY,
                bitDepth = Camera.BitDepth,
                maxX = Camera.MaxX,
                maxY = Camera.MaxY,
                pixelSizeX = Safe(pxX),
                pixelSizeY = Safe(pxY),
                sensorWidthMm = Safe(sensorWmm),
                sensorHeightMm = Safe(sensorHmm)
            };
        }

        if (Telescope != null) {
            status["telescope"] = new {
                name = Telescope.DeviceName,
                connected = Telescope.IsConnected,
                ra = Safe(Telescope.RightAscension),
                dec = Safe(Telescope.Declination),
                alt = Safe(Telescope.Altitude),
                az = Safe(Telescope.Azimuth),
                tracking = Telescope.IsTracking,
                slewing = Telescope.IsSlewing,
                parked = Telescope.IsParked,
                pierSide = Telescope.SideOfPier.ToString()
            };
        }

        if (Focuser != null) {
            status["focuser"] = new {
                name = Focuser.DeviceName,
                position = Focuser.Position,
                temperature = Safe(Focuser.Temperature),
                maxPosition = Focuser.MaxPosition,
                moving = Focuser.IsMoving
            };
        }

        if (FilterWheel != null) {
            status["filterWheel"] = new {
                name = FilterWheel.DeviceName,
                position = FilterWheel.Position,
                currentFilter = FilterWheel.CurrentFilterName,
                filters = FilterWheel.FilterNames,
                moving = FilterWheel.IsMoving
            };
        }

        if (Rotator != null) {
            status["rotator"] = new {
                name = Rotator.DeviceName,
                connected = Rotator.IsConnected,
                position = Safe(Rotator.Position),
                moving = Rotator.IsMoving,
                reversed = Rotator.IsReversed
            };
        }

        if (FlatDevice != null) {
            status["flatDevice"] = new {
                name = FlatDevice.DeviceName,
                connected = FlatDevice.IsConnected,
                lightOn = FlatDevice.IsLightOn,
                brightness = FlatDevice.Brightness,
                coverOpen = FlatDevice.IsCoverOpen,
                coverMoving = FlatDevice.IsCoverMoving
            };
        }

        if (Dome != null) {
            status["dome"] = new {
                name = Dome.DeviceName,
                connected = Dome.IsConnected,
                azimuth = Safe(Dome.Azimuth),
                moving = Dome.IsMoving,
                parked = Dome.IsParked,
                slaved = Dome.IsSlaved,
                shutter = Dome.ShutterStatus.ToString()
            };
        }

        if (Weather != null) {
            status["weather"] = new {
                name = Weather.DeviceName,
                connected = Weather.IsConnected,
                temperature = Safe(Weather.Temperature),
                humidity = Safe(Weather.Humidity),
                dewPoint = Safe(Weather.DewPoint),
                windSpeed = Safe(Weather.WindSpeed),
                windGust = Safe(Weather.WindGust),
                pressure = Safe(Weather.Pressure),
                cloudCover = Safe(Weather.CloudCover),
                rainRate = Safe(Weather.RainRate),
                skyQuality = Safe(Weather.SkyQuality),
                safe = Weather.IsSafe
            };
        }

        return status;
    }

    static double? Safe(double v) => double.IsNaN(v) || double.IsInfinity(v) ? null : v;

    private void OnDeviceFound(string deviceName) {
        _logger.LogInformation("INDI device discovered: {Name}", deviceName);
    }

    public void Dispose() {
        _indiClient.DeviceFound -= OnDeviceFound;
    }
}

/// <summary>Describes one camera-driver kind exposed by the host.
/// Used by <c>GET /api/camera/drivers</c> so the UI can populate the
/// driver dropdown with the matching availability badges.</summary>
public record CameraDriverInfo(string Id, string Name, bool Available, string Description);

/// <summary>One row in the per-driver camera-discovery dropdown. Id
/// is what the UI passes back to <c>POST /api/camera/select</c>; the
/// rest is display-only.</summary>
public record DiscoveredCamera(string Id, string Model, string Detail);

using NINA.Image.Interfaces;
using NINA.INDI.Client;
using NINA.INDI.Devices;
using NINA.Polaris.Services.Alpaca;

namespace NINA.Polaris.Services;

public class EquipmentManager : IDisposable {
    private readonly IndiClient _indiClient;
    private readonly ILogger<EquipmentManager> _logger;
    private readonly AlpacaDiscoveryCache _alpacaCache;

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

    /// <summary>Currently-selected mount, regardless of backend.
    /// Today only <see cref="IndiTelescope"/> implements it; direct
    /// WiFi / Bluetooth drivers (SynScan UDP, NexStar TCP, LX200 TCP)
    /// plug in here without touching the capture / sequencing code.
    /// See <c>docs/mounts-wifi.md</c> for the open driver work.</summary>
    public ITelescope? Telescope { get; private set; }
    /// <summary>Mount driver kind currently bound to <see cref="Telescope"/>.
    /// Mirrors <c>EquipmentProfile.TelescopeDriver</c>. Null when no
    /// mount is selected.</summary>
    public string? TelescopeDriver { get; private set; }
    public IFocuser? Focuser { get; private set; }
    /// <summary>Driver kind currently bound to <see cref="Focuser"/>.
    /// "indi" (default) or "ascom-com" (Windows-only ASCOM Platform).
    /// Mirrors <c>EquipmentProfile.FocuserDriver</c>.</summary>
    public string? FocuserDriver { get; private set; }
    public IFilterWheel? FilterWheel { get; private set; }
    /// <summary>Driver kind currently bound to <see cref="FilterWheel"/>.
    /// Mirrors <c>EquipmentProfile.FilterWheelDriver</c>.</summary>
    public string? FilterWheelDriver { get; private set; }
    public IndiRotator? Rotator { get; private set; }
    public IndiFlatDevice? FlatDevice { get; private set; }
    public IndiDome? Dome { get; private set; }
    public IndiWeather? Weather { get; private set; }

    public EquipmentManager(IndiClient indiClient, ILogger<EquipmentManager> logger,
                            AlpacaDiscoveryCache alpacaCache) {
        _indiClient = indiClient;
        _logger = logger;
        _alpacaCache = alpacaCache;
        _indiClient.DeviceFound += OnDeviceFound;
    }

    public IEnumerable<string> GetDeviceNames() => _indiClient.GetDeviceNames();

    /// <summary>Legacy entry-point, assumes the INDI driver. Kept
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
            "ascom-com"   => CreateAscomCamera(deviceId),
            "alpaca"      => AlpacaCamera.FromDeviceId(deviceId),
            _ => throw new NotSupportedException(
                $"Camera driver '{driver}' is not implemented yet. " +
                "Use 'indi', 'alpaca', or install the matching vendor SDK."),
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
                "gphoto driver instead, see docs/dslr-linux.md.");
        }
        return new NINA.Camera.CanonEdsdk.CanonEdsdkCamera(deviceId);
    }

    private static ICamera CreateNikonCamera(string deviceId) {
        if (!OperatingSystem.IsWindows()) {
            throw new NotSupportedException(
                "Nikon SDK only runs on Windows. On Linux, use the INDI " +
                "gphoto driver instead, see docs/dslr-linux.md.");
        }
        return new NINA.Camera.NikonSdk.NikonSdkCamera(deviceId);
    }

    /// <summary>ASCOM Camera (ICameraV3) over native COM. Windows-only,
    /// requires the ASCOM Platform installed on the host. Lets Polaris
    /// reach ASCOM hardware without routing through ASCOM Remote or
    /// the Alpaca Omni Simulator.</summary>
    private static ICamera CreateAscomCamera(string progId) {
        if (!OperatingSystem.IsWindows()) {
            throw new NotSupportedException(
                "ASCOM COM drivers only run on Windows. On Linux / macOS, " +
                "use 'indi' or 'alpaca' instead.");
        }
        return new NINA.Ascom.Com.AscomComCamera(progId);
    }

    /// <summary>List of camera driver kinds the host can offer. Always
    /// includes <c>indi</c>; vendor SDK drivers are listed only when
    /// the matching native dependency is present on the current OS.</summary>
    public IReadOnlyList<CameraDriverInfo> GetAvailableCameraDrivers() {
        var alpacaCount = _alpacaCache.ByType("Camera").Count;
        var list = new List<CameraDriverInfo> {
            new("indi", "INDI", Available: true,
                Description: "Standard astronomy cameras via INDI server."),
            new("alpaca", "Alpaca (ASCOM)", Available: alpacaCount > 0,
                Description: alpacaCount > 0
                    ? $"ASCOM-over-HTTP cameras. {alpacaCount} discovered."
                    : "Run Alpaca Discover in RIGS first to populate this list."),
        };
        if (OperatingSystem.IsWindows()) {
            // Direct ASCOM Platform COM-interop. Available iff the
            // ASCOM Platform is installed AND at least one Camera
            // driver is registered. No native dependency to download
            // beyond ASCOM Platform itself.
            var ascomCount = ProbeAscomDriverCount(
                NINA.Ascom.Com.AscomComRegistry.DeviceType.Camera);
            list.Add(new("ascom-com", "ASCOM (COM, direct)",
                Available: ascomCount > 0,
                Description: ascomCount > 0
                    ? $"Direct COM-interop, no ASCOM Remote in the way. {ascomCount} driver(s) registered."
                    : "Install the ASCOM Platform + a camera driver from https://ascom-standards.org/"));
            // Canon EDSDK + Nikon MAID/Imaging SDKs are Windows-only.
            // Probe each so the UI can show a green check when the
            // native DLLs are reachable on the search path or a
            // "download" banner when they aren't.
            list.Add(new("canon-edsdk", "Canon EOS (EDSDK)",
                Available: NINA.Camera.CanonEdsdk.CanonEdsdkRegistry.IsAvailable,
                Description: "Canon DSLR / mirrorless. Requires EDSDK DLLs."));
            list.Add(new("nikon-sdk", "Nikon (MAID SDK)",
                Available: NINA.Camera.NikonSdk.NikonSdkRegistry.IsAvailable,
                Description: "Nikon DSLR / Z mirrorless. Skeleton driver, " +
                    "see docs/dslr-windows-nikon.md to wire up the actual SDK."));
        }
        // Sony Camera Remote SDK ships native binaries for both
        // Windows and Linux, so it shows up on every OS, including
        // Raspberry Pi via the SDK's linux-arm64 build.
        list.Add(new("sony-sdk", "Sony α series",
            Available: NINA.Camera.SonySdk.SonySdkRegistry.IsAvailable,
            Description: "Sony α series. Skeleton driver, see " +
                "docs/dslr-windows-sony.md (two complementary paths: " +
                "Wi-Fi Camera Remote API for older bodies, USB SCRSDK " +
                "v2.x for current bodies)."));
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
        if (driver == "ascom-com" && OperatingSystem.IsWindows()) {
            return EnumerateAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Camera)
                .Select(d => new DiscoveredCamera(d.ProgId, d.Description, d.ProgId))
                .ToList();
        }
        if (driver == "alpaca") {
            // Pulls from the cache populated by /api/alpaca/discover.
            // DeviceId is canonical host:port:devnum so SelectCamera(driver,
            // deviceId) can reconstruct an AlpacaCamera without re-discovering.
            return _alpacaCache.ByType("Camera")
                .Select(d => new DiscoveredCamera(d.DeviceId, d.DeviceName, d.ServerName))
                .ToList();
        }
        return Array.Empty<DiscoveredCamera>();
    }

    /// <summary>Mirror of <see cref="GetDiscoveredCamerasFor"/> for the
    /// mount / telescope dropdown. Same Alpaca cache fed by
    /// /api/alpaca/discover; INDI returns the INDI device list filtered
    /// by interface type would be ideal but the existing endpoints don't
    /// do that yet, so for now INDI/synscan/lx200 paths short-circuit to
    /// empty and the user types the device id manually.</summary>
    public IReadOnlyList<DiscoveredCamera> GetDiscoveredTelescopesFor(string driver) {
        driver = (driver ?? "").Trim().ToLowerInvariant();
        if (driver == "alpaca") {
            return _alpacaCache.ByType("Telescope")
                .Select(d => new DiscoveredCamera(d.DeviceId, d.DeviceName, d.ServerName))
                .ToList();
        }
        if (driver == "ascom-com" && OperatingSystem.IsWindows()) {
            return EnumerateAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Telescope)
                .Select(d => new DiscoveredCamera(d.ProgId, d.Description, d.ProgId))
                .ToList();
        }
        return Array.Empty<DiscoveredCamera>();
    }

    public IReadOnlyList<DiscoveredCamera> GetDiscoveredFocusersFor(string driver) {
        driver = (driver ?? "").Trim().ToLowerInvariant();
        if (driver == "alpaca") {
            return _alpacaCache.ByType("Focuser")
                .Select(d => new DiscoveredCamera(d.DeviceId, d.DeviceName, d.ServerName))
                .ToList();
        }
        if (driver == "ascom-com" && OperatingSystem.IsWindows()) {
            return EnumerateAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.Focuser)
                .Select(d => new DiscoveredCamera(d.ProgId, d.Description, d.ProgId))
                .ToList();
        }
        return Array.Empty<DiscoveredCamera>();
    }

    public IReadOnlyList<DiscoveredCamera> GetDiscoveredFilterWheelsFor(string driver) {
        driver = (driver ?? "").Trim().ToLowerInvariant();
        if (driver == "alpaca") {
            return _alpacaCache.ByType("FilterWheel")
                .Select(d => new DiscoveredCamera(d.DeviceId, d.DeviceName, d.ServerName))
                .ToList();
        }
        if (driver == "ascom-com" && OperatingSystem.IsWindows()) {
            return EnumerateAscomDrivers(
                    NINA.Ascom.Com.AscomComRegistry.DeviceType.FilterWheel)
                .Select(d => new DiscoveredCamera(d.ProgId, d.Description, d.ProgId))
                .ToList();
        }
        return Array.Empty<DiscoveredCamera>();
    }

    /// <summary>Count of registered ASCOM drivers for a given device
    /// type. Used by the driver-catalogue endpoints to decide whether
    /// to advertise the "ascom-com" entry as available. Returns 0 on
    /// non-Windows hosts.</summary>
    private static int ProbeAscomDriverCount(NINA.Ascom.Com.AscomComRegistry.DeviceType type) {
        if (!OperatingSystem.IsWindows()) return 0;
        try { return EnumerateAscomDrivers(type).Count; } catch { return 0; }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static IReadOnlyList<NINA.Ascom.Com.AscomComRegistry.AscomDriver>
        EnumerateAscomDrivers(NINA.Ascom.Com.AscomComRegistry.DeviceType type)
        => NINA.Ascom.Com.AscomComRegistry.Enumerate(type);

    /// <summary>Windows-only inner helper so the platform analyzer
    /// is satisfied, the OS guard in the caller is implicit here via
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

    /// <summary>Legacy entry-point, assumes the INDI driver. Kept
    /// for backwards compatibility with the existing
    /// <c>POST /api/telescope/select/{deviceName}</c> route.</summary>
    public ITelescope SelectTelescope(string deviceName)
        => SelectTelescope("indi", deviceName);

    /// <summary>Select a mount by driver kind + driver-specific
    /// device id. INDI mounts are addressed by INDI device name;
    /// direct WiFi drivers (SynScan UDP, NexStar TCP, LX200 TCP)
    /// take a <c>host:port</c> string. Vendor SDK drivers are
    /// addressed by serial / id.</summary>
    public ITelescope SelectTelescope(string driver, string deviceId) {
        driver = (driver ?? "indi").Trim().ToLowerInvariant();
        Telescope = driver switch {
            "indi" => new IndiTelescope(_indiClient, deviceId),
            "synscan-wifi" => new NINA.Mount.SynScanWifi.SynScanWifiTelescope(deviceId),
            "ascom-com" => CreateAscomTelescope(deviceId),
            "alpaca" => AlpacaTelescope.FromDeviceId(deviceId),
            // NexStar TCP + LX200 TCP still pending, see
            // docs/mounts-wifi.md for the backlog.
            _ => throw new NotSupportedException(
                $"Mount driver '{driver}' is not implemented yet. " +
                "Use 'indi', 'alpaca', 'synscan-wifi', or 'ascom-com'."),
        };
        TelescopeDriver = driver;
        _logger.LogInformation("Telescope selected: driver={Driver}, id={DeviceId}",
            driver, deviceId);
        return Telescope;
    }

    /// <summary>Available mount drivers on this host. Always includes
    /// <c>indi</c>; the WiFi / Alpaca entries are advertised as
    /// "not installed" until their backend lands.</summary>
    public IReadOnlyList<CameraDriverInfo> GetAvailableMountDrivers() {
        // Reusing CameraDriverInfo here, the shape is identical
        // (id / name / available / description) and a separate
        // record would just be ceremony.
        var list = new List<CameraDriverInfo> {
            new("indi", "INDI", Available: true,
                Description: "Any mount the running INDI server exposes, covers most WiFi mounts via indi_skywatcherAltAzMount / indi_celestron_aux / indi_ioptron_v3 / indi_lx200gps."),
            new("alpaca", "Alpaca (ASCOM)",
                Available: _alpacaCache.ByType("Telescope").Count > 0,
                Description: _alpacaCache.ByType("Telescope").Count > 0
                    ? $"ASCOM-over-HTTP mounts. {_alpacaCache.ByType("Telescope").Count} discovered."
                    : "Run Alpaca Discover in RIGS first to populate this list."),
            new("synscan-wifi", "Sky-Watcher SynScan (Wi-Fi UDP)", Available: true,
                Description: "Direct UDP to AZ-GTi / EQ6-R Pro / EQ8-R Pro / AllView / GoTo Dob (port 11880). Likely also drives ZWO AM5N / AM7 in SynScan-compat mode. Device id format: host[:port], defaults to 192.168.4.1:11880 (factory AP)."),
            new("nexstar-wifi", "Celestron NexStar (Wi-Fi TCP)", Available: false,
                Description: "Direct TCP to SkyPortal Wi-Fi accessory / StarSense Explorer Wi-Fi. Driver pending, see docs/mounts-wifi.md."),
            new("lx200-tcp", "Meade / LX200 (TCP)", Available: false,
                Description: "Direct TCP wrapping the LX200 serial protocol. Driver pending, see docs/mounts-wifi.md."),
        };
        if (OperatingSystem.IsWindows()) {
            var n = ProbeAscomDriverCount(NINA.Ascom.Com.AscomComRegistry.DeviceType.Telescope);
            list.Add(new("ascom-com", "ASCOM (COM, direct)",
                Available: n > 0,
                Description: n > 0
                    ? $"Direct COM-interop, no ASCOM Remote in the way. {n} driver(s) registered."
                    : "Install the ASCOM Platform + a telescope driver from https://ascom-standards.org/"));
        }
        return list;
    }

    /// <summary>List of registered ASCOM drivers for a device type.
    /// Used by the per-driver discovery endpoints. Empty on non-
    /// Windows hosts.</summary>
    public IReadOnlyList<DiscoveredCamera> GetAscomDrivers(
            NINA.Ascom.Com.AscomComRegistry.DeviceType type) {
        if (!OperatingSystem.IsWindows()) return Array.Empty<DiscoveredCamera>();
        try {
            return EnumerateAscomDrivers(type)
                .Select(d => new DiscoveredCamera(d.ProgId, d.Description, d.ProgId))
                .ToList();
        } catch (Exception ex) {
            _logger.LogWarning(ex, "ASCOM {Type} discovery failed", type);
            return Array.Empty<DiscoveredCamera>();
        }
    }

    private static ITelescope CreateAscomTelescope(string progId) {
        if (!OperatingSystem.IsWindows()) {
            throw new NotSupportedException(
                "ASCOM COM drivers only run on Windows.");
        }
        return new NINA.Ascom.Com.AscomComTelescope(progId);
    }

    /// <summary>Legacy entry-point, assumes the INDI driver. Kept for
    /// backwards compatibility with the existing
    /// <c>POST /api/focuser/select/{deviceName}</c> route.</summary>
    public IFocuser SelectFocuser(string deviceName)
        => SelectFocuser("indi", deviceName);

    public IFocuser SelectFocuser(string driver, string deviceId) {
        driver = (driver ?? "indi").Trim().ToLowerInvariant();
        Focuser = driver switch {
            "indi" => new IndiFocuser(_indiClient, deviceId, _logger),
            "ascom-com" => CreateAscomFocuser(deviceId),
            "alpaca" => AlpacaFocuser.FromDeviceId(deviceId),
            _ => throw new NotSupportedException(
                $"Focuser driver '{driver}' is not implemented yet. " +
                "Use 'indi', 'alpaca', or 'ascom-com'."),
        };
        FocuserDriver = driver;
        _logger.LogInformation("Focuser selected: driver={Driver}, id={DeviceId}",
            driver, deviceId);
        return Focuser;
    }

    private static IFocuser CreateAscomFocuser(string progId) {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("ASCOM COM drivers only run on Windows.");
        return new NINA.Ascom.Com.AscomComFocuser(progId);
    }

    public IFilterWheel SelectFilterWheel(string deviceName)
        => SelectFilterWheel("indi", deviceName);

    public IFilterWheel SelectFilterWheel(string driver, string deviceId) {
        driver = (driver ?? "indi").Trim().ToLowerInvariant();
        FilterWheel = driver switch {
            "indi" => new IndiFilterWheel(_indiClient, deviceId),
            "ascom-com" => CreateAscomFilterWheel(deviceId),
            "alpaca" => AlpacaFilterWheel.FromDeviceId(deviceId),
            _ => throw new NotSupportedException(
                $"Filter wheel driver '{driver}' is not implemented yet. " +
                "Use 'indi', 'alpaca', or 'ascom-com'."),
        };
        FilterWheelDriver = driver;
        _logger.LogInformation("Filter wheel selected: driver={Driver}, id={DeviceId}",
            driver, deviceId);
        return FilterWheel;
    }

    private static IFilterWheel CreateAscomFilterWheel(string progId) {
        if (!OperatingSystem.IsWindows())
            throw new NotSupportedException("ASCOM COM drivers only run on Windows.");
        return new NINA.Ascom.Com.AscomComFilterWheel(progId);
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
            // Capabilities sub-object gates per-button UI affordances
            // (Park / Find Home / pier-side indicator). Sent every
            // tick so a hot-plug rig switch flips the buttons without
            // a UI reload.
            var caps = Telescope.Capabilities;
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
                pierSide = Telescope.SideOfPier.ToString(),
                capabilities = new {
                    park = caps.SupportsPark,
                    trackingToggle = caps.SupportsTrackingToggle,
                    sync = caps.SupportsSync,
                    pierSide = caps.SupportsPierSide,
                    manualJog = caps.SupportsManualJog,
                    findHome = caps.SupportsFindHome,
                    setSiteLocation = caps.SupportsSetSiteLocation
                }
            };
        }

        if (Focuser != null) {
            status["focuser"] = new {
                name = Focuser.DeviceName,
                connected = Focuser.IsConnected,
                position = Focuser.Position,
                temperature = Safe(Focuser.Temperature),
                maxPosition = Focuser.MaxPosition,
                moving = Focuser.IsMoving
            };
        }

        if (FilterWheel != null) {
            status["filterWheel"] = new {
                name = FilterWheel.DeviceName,
                connected = FilterWheel.IsConnected,
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

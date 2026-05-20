using NINA.INDI.Client;
using NINA.INDI.Devices;

namespace NINA.Headless.Services;

public class EquipmentManager : IDisposable {
    private readonly IndiClient _indiClient;
    private readonly ILogger<EquipmentManager> _logger;

    public IndiCamera? Camera { get; private set; }
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

    public IndiCamera SelectCamera(string deviceName) {
        Camera = new IndiCamera(_indiClient, deviceName);
        _logger.LogInformation("Camera selected: {Name}", deviceName);
        return Camera;
    }

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
            status["camera"] = new {
                name = Camera.DeviceName,
                connected = Camera.IsConnected,
                state = Camera.State.ToString(),
                temperature = Safe(Camera.Temperature),
                coolerOn = Camera.CoolerOn,
                binX = Camera.BinX,
                binY = Camera.BinY
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

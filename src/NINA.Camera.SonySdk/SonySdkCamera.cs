using NINA.Core.Enum;
using NINA.Image.Interfaces;

namespace NINA.Camera.SonySdk;

/// <summary>
/// Skeleton <see cref="ICamera"/> implementation for Sony α-series
/// bodies. Wired into <c>EquipmentManager.SelectCamera</c> and the
/// Camera-card UI exactly like the other vendor drivers, but the
/// SDK calls themselves throw because the Camera Remote SDK
/// binding work is open.
///
/// See <c>docs/dslr-windows-sony.md</c> and
/// <see cref="SonySdkRegistry"/> for the recommended path.
/// </summary>
public class SonySdkCamera : ICamera {
    private readonly string _deviceId;

    public SonySdkCamera(string deviceId) {
        _deviceId = deviceId;
    }

    public string DeviceName => _deviceId;
    public bool IsConnected => false;
    public CameraStates State => CameraStates.NoState;
    public double Temperature => double.NaN;
    public bool CoolerOn => false;
    public double CoolerPower => 0;
    public int BinX => 1;
    public int BinY => 1;
    public int BitDepth => 14;
    public int MaxX => 0;
    public int MaxY => 0;
    public double PixelSizeX => 0;
    public double PixelSizeY => 0;
    public int Gain => SelectedIso;
    public IReadOnlyList<int> IsoOptions { get; } = new[] {
        50, 100, 200, 400, 800, 1600, 3200, 6400, 12800, 25600, 51200, 102400
    };
    public int SelectedIso { get; private set; } = 800;
    public CameraCapabilities Capabilities => CameraCapabilities.Dslr;

    public Task ConnectAsync(CancellationToken ct = default) => Throw();
    public Task DisconnectAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SetBinningAsync(int binX, int binY, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetTemperatureAsync(double t, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetCoolerAsync(bool on, CancellationToken ct = default) => Task.CompletedTask;
    public Task SetIsoAsync(int iso, CancellationToken ct = default) {
        SelectedIso = iso;
        return Task.CompletedTask;
    }
    public Task AbortExposureAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IImageData> CaptureAsync(double exposureSeconds,
            CaptureOptions? opts = null, CancellationToken ct = default)
        => Throw<IImageData>();

    private static Task Throw() => Task.FromException(MakeException());
    private static Task<T> Throw<T>() => Task.FromException<T>(MakeException());

    private static NotImplementedException MakeException()
        => new("Sony Camera Remote SDK integration is a skeleton in this " +
               "build. Implementation pending — see docs/dslr-windows-sony.md.");
}

using System.Runtime.Versioning;

namespace NINA.Camera.NikonSdk;

/// <summary>Connected-Nikon-bodies enumeration. Currently returns
/// an empty list because the driver is a skeleton. See
/// <see cref="NikonSdkRegistry"/> for the open work.</summary>
[SupportedOSPlatform("windows")]
public static class NikonSdkDiscovery {

    public record NikonCameraEntry(string Id, string Model, string PortName);

    public static IReadOnlyList<NikonCameraEntry> Enumerate()
        => Array.Empty<NikonCameraEntry>();
}

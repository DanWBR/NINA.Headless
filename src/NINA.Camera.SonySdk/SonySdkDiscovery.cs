namespace NINA.Camera.SonySdk;

/// <summary>Connected-Sony-bodies enumeration. Returns empty until
/// the SDK binding is implemented — see <see cref="SonySdkRegistry"/>.</summary>
public static class SonySdkDiscovery {

    public record SonyCameraEntry(string Id, string Model, string PortName);

    public static IReadOnlyList<SonyCameraEntry> Enumerate()
        => Array.Empty<SonyCameraEntry>();
}

namespace NINA.Camera.SonySdk;

/// <summary>
/// SDK lifetime + availability probe for Sony α-series mirrorless
/// support. Same role as <c>CanonEdsdkRegistry</c> for Canon.
///
/// <para>
/// Status: <b>skeleton driver</b>. Sony's Camera Remote SDK
/// (SCRSDK) v2.x ships native binaries for both Windows and
/// Linux, which is unusual among vendor SDKs (Canon and Nikon
/// are Windows-only) and makes it the most attractive vendor
/// driver to finish. Bodies covered: α7 III / α7R III onward,
/// α9 II, α1, α7 IV, α7C, ZV-E1.
/// </para>
///
/// <para>
/// To finish this driver: register on
/// <a href="https://developer.sony.com/imaging-products/camera-remote-sdk/">
/// developer.sony.com/imaging-products</a>, download SCRSDK,
/// implement the native bindings under <c>Native/</c> (the SDK
/// ships a C-style API surface that's easier to P/Invoke than
/// Canon's mixed C/C++ EDSDK), populate <see cref="SonySdkDiscovery"/>
/// + <see cref="SonySdkCamera"/>, and flip
/// <see cref="IsAvailable"/> to return true on successful init.
/// </para>
/// </summary>
public static class SonySdkRegistry {

    /// <summary>Currently returns false unconditionally — the
    /// integration is a skeleton. The UI surfaces this as "(not
    /// installed)" with a link to <c>docs/dslr-windows-sony.md</c>.</summary>
    public static bool IsAvailable => false;

    public static void EnsureInitialized() {
        throw new NotImplementedException(
            "Sony Camera Remote SDK integration is not implemented yet. " +
            "See docs/dslr-windows-sony.md for the open work.");
    }
}

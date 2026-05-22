using System.Runtime.Versioning;

namespace NINA.Camera.NikonSdk;

/// <summary>
/// SDK lifetime + availability probe for Nikon DSLR / mirrorless
/// support. Same role as <c>CanonEdsdkRegistry</c> for Canon.
///
/// <para>
/// Status: <b>skeleton driver</b>. The Nikon stack splits in two —
/// the older <b>MAID</b> SDK (covers DSLR bodies through the D6 era;
/// driven by <c>.md3</c> module files) and the newer <b>Nikon
/// Imaging SDK / Type 0006</b> (covers Z-series mirrorless). This
/// project ships the structure + ICamera plumbing + UI integration
/// so a contributor with a Nikon body can wire up the actual native
/// surface without rebuilding the driver layout.
/// </para>
///
/// <para>
/// Recommended implementation path: the MIT-licensed
/// <a href="https://github.com/meklarian/MekNikon">MekNikon</a>
/// project is the most usable reference for a C# wrapper around
/// MAID. It covers Z7 / Z7 II and D500 today and follows the
/// "dynamically load <c>.md3</c> modules then drive cameras via
/// NkMAIDOpenObject / GetData / SetData / Capture" recipe the SDK
/// expects. Either vendor a copy of its native bindings or
/// reference its NuGet (once published) and adapt
/// <see cref="NikonSdkCamera"/> to drive it. See
/// <c>docs/dslr-windows-nikon.md</c> for the full open-work list.
/// </para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class NikonSdkRegistry {

    /// <summary>Currently returns false unconditionally — the
    /// integration is a skeleton. The UI surfaces this as "(not
    /// installed)" with a link to <c>docs/dslr-windows-nikon.md</c>.</summary>
    public static bool IsAvailable => false;

    public static void EnsureInitialized() {
        throw new NotImplementedException(
            "Nikon SDK integration is not implemented yet. See " +
            "docs/dslr-windows-nikon.md for the open work.");
    }
}

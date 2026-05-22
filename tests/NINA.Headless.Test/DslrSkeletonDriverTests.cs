using NUnit.Framework;
using NINA.Image.Interfaces;

namespace NINA.Headless.Test;

/// <summary>
/// Sanity tests for the skeleton DSLR drivers (Nikon MAID + Sony
/// Camera Remote SDK). They exist primarily to pin the contract
/// shape the upcoming real bindings have to honour — ICamera
/// implementation present, DSLR capabilities, ISO option lists
/// non-empty, registry probes return false until the actual SDK
/// integration lands.
/// </summary>
[TestFixture]
public class DslrSkeletonDriverTests {

    // --- Nikon ----------------------------------------------------

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NikonSkeleton_IsAvailable_ReturnsFalse() {
        Assert.That(NINA.Camera.NikonSdk.NikonSdkRegistry.IsAvailable, Is.False,
            "Skeleton driver must report unavailable until the actual SDK binding lands");
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NikonSkeleton_Enumerate_ReturnsEmpty() {
        Assert.That(NINA.Camera.NikonSdk.NikonSdkDiscovery.Enumerate(), Is.Empty);
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void NikonSkeleton_Camera_HasDslrCapabilities() {
        var cam = new NINA.Camera.NikonSdk.NikonSdkCamera("test-id");
        Assert.That(cam.Capabilities, Is.EqualTo(CameraCapabilities.Dslr));
        Assert.That(cam.IsoOptions, Is.Not.Empty,
            "DSLR drivers must expose an ISO option list");
        Assert.That(cam.IsConnected, Is.False);
    }

    [Test]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task NikonSkeleton_Capture_ThrowsNotImplemented() {
        var cam = new NINA.Camera.NikonSdk.NikonSdkCamera("test-id");
        Assert.ThrowsAsync<NotImplementedException>(async () =>
            await cam.CaptureAsync(1.0));
        await Task.CompletedTask;
    }

    // --- Sony -----------------------------------------------------

    [Test]
    public void SonySkeleton_IsAvailable_ReturnsFalse() {
        Assert.That(NINA.Camera.SonySdk.SonySdkRegistry.IsAvailable, Is.False);
    }

    [Test]
    public void SonySkeleton_Enumerate_ReturnsEmpty() {
        Assert.That(NINA.Camera.SonySdk.SonySdkDiscovery.Enumerate(), Is.Empty);
    }

    [Test]
    public void SonySkeleton_Camera_HasDslrCapabilities() {
        var cam = new NINA.Camera.SonySdk.SonySdkCamera("test-id");
        Assert.That(cam.Capabilities, Is.EqualTo(CameraCapabilities.Dslr));
        Assert.That(cam.IsoOptions, Is.Not.Empty);
        Assert.That(cam.IsConnected, Is.False);
    }

    [Test]
    public async Task SonySkeleton_Capture_ThrowsNotImplemented() {
        var cam = new NINA.Camera.SonySdk.SonySdkCamera("test-id");
        Assert.ThrowsAsync<NotImplementedException>(async () =>
            await cam.CaptureAsync(1.0));
        await Task.CompletedTask;
    }
}

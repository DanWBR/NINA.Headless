using Microsoft.Extensions.Logging.Abstractions;
using NINA.Polaris.Services.Simulator;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the pure-function helpers in AscomSimulatorBackend. The
/// IO-heavy paths (DetectInstallAsync via filesystem + PATH lookup,
/// LaunchAsync spawning AlpacaOmniSimulator.exe, IsRunningAsync hitting
/// the management API) are smoke-tested manually — they only pass on
/// a Windows host with the Omni Sim installed. The Alpaca management
/// JSON parser is the part most likely to break under refactoring.
/// </summary>
[TestFixture]
public class AscomSimulatorBackendTests {

    // --- ParseConfiguredDevices ---

    [Test]
    public void ParseConfiguredDevices_OmniSimRealResponse_ExtractsTags() {
        // Shape captured from a real Alpaca Omni Simulator install
        // (slightly trimmed). The DeviceType strings are exactly what
        // Omni Sim sends — our map handles the case-insensitive
        // mapping to our canonical lowercase tags.
        var json = """
        {
          "Value": [
            { "DeviceName": "Alpaca Camera Sim", "DeviceType": "Camera", "DeviceNumber": 0 },
            { "DeviceName": "Alpaca Telescope Sim", "DeviceType": "Telescope", "DeviceNumber": 0 },
            { "DeviceName": "Alpaca Focuser Sim", "DeviceType": "Focuser", "DeviceNumber": 0 },
            { "DeviceName": "Alpaca FilterWheel Sim", "DeviceType": "FilterWheel", "DeviceNumber": 0 },
            { "DeviceName": "Alpaca Dome Sim", "DeviceType": "Dome", "DeviceNumber": 0 },
            { "DeviceName": "Alpaca Observing Conditions Sim", "DeviceType": "ObservingConditions", "DeviceNumber": 0 }
          ]
        }
        """;
        var tags = AscomSimulatorBackend.ParseConfiguredDevices(json);
        Assert.That(tags, Is.EquivalentTo(new[] {
            "ccd", "telescope", "focus", "wheel", "dome", "weather"
        }));
    }

    [Test]
    public void ParseConfiguredDevices_UnknownDeviceType_SilentlyDropped() {
        // If a future Omni Sim version adds something we don't have
        // a tag for (e.g. "SafetyMonitor"), we drop it rather than
        // surface a confusing string in the UI checkbox grid.
        var json = """
        {"Value":[
          {"DeviceType":"Camera"},
          {"DeviceType":"SafetyMonitor"},
          {"DeviceType":"Switch"}
        ]}
        """;
        var tags = AscomSimulatorBackend.ParseConfiguredDevices(json);
        Assert.That(tags, Is.EqualTo(new[] { "ccd" }));
    }

    [Test]
    public void ParseConfiguredDevices_EmptyArray_ReturnsEmpty() {
        Assert.That(AscomSimulatorBackend.ParseConfiguredDevices("""{"Value":[]}"""),
            Is.Empty);
    }

    [Test]
    public void ParseConfiguredDevices_MissingValueProperty_ReturnsEmpty() {
        // Defensive against an Omni Sim version that changes the
        // envelope; don't crash, just report nothing.
        Assert.That(AscomSimulatorBackend.ParseConfiguredDevices("""{"Other":[]}"""),
            Is.Empty);
    }

    [Test]
    public void ParseConfiguredDevices_MalformedJson_ReturnsEmpty() {
        Assert.That(AscomSimulatorBackend.ParseConfiguredDevices("not json"),
            Is.Empty);
        Assert.That(AscomSimulatorBackend.ParseConfiguredDevices(""),
            Is.Empty);
    }

    [Test]
    public void ParseConfiguredDevices_DedupsRepeatedTypes() {
        // Two Cameras (DeviceNumber 0 and 1) should yield one "ccd"
        // tag, not two — we report per-category, not per-instance.
        var json = """
        {"Value":[
          {"DeviceType":"Camera","DeviceNumber":0},
          {"DeviceType":"Camera","DeviceNumber":1}
        ]}
        """;
        Assert.That(AscomSimulatorBackend.ParseConfiguredDevices(json),
            Is.EqualTo(new[] { "ccd" }));
    }

    // --- IsSupported gate ---

    [Test]
    public void IsSupported_TracksRuntimePlatform() {
        var backend = new AscomSimulatorBackend(NullLogger<AscomSimulatorBackend>.Instance);
        Assert.That(backend.IsSupported, Is.EqualTo(OperatingSystem.IsWindows()));
    }

    [Test]
    public async Task DetectInstallAsync_OnUnsupportedOs_ReturnsCleanError() {
        if (OperatingSystem.IsWindows()) {
            Assert.Ignore("This test pins the non-Windows fallback path.");
        }
        var backend = new AscomSimulatorBackend(NullLogger<AscomSimulatorBackend>.Instance);
        var install = await backend.DetectInstallAsync();
        Assert.That(install.Installed, Is.False);
        Assert.That(install.Error, Does.Contain("Windows"));
    }

    [Test]
    public async Task LaunchAsync_OnUnsupportedOs_ReturnsFalse() {
        if (OperatingSystem.IsWindows()) {
            Assert.Ignore("Windows integration path is manual-verify only — needs the actual Omni Sim binary.");
        }
        var backend = new AscomSimulatorBackend(NullLogger<AscomSimulatorBackend>.Instance);
        var ok = await backend.LaunchAsync(new SimulatorLaunchRequest(["ccd"], 32323));
        Assert.That(ok, Is.False);
    }

    // --- AddDevice / RemoveDevice no-op semantics ---

    [Test]
    public async Task AddDeviceAsync_OnWindows_ReturnsTrueAsNoop() {
        // Omni Sim exposes everything at once — runtime add/remove
        // doesn't translate. Backend returns true (idempotent
        // success) so SimulatorService doesn't surface a misleading
        // failure when the UI thinks it added something. The UI
        // hides the toggle when kind === 'ascom' anyway.
        if (!OperatingSystem.IsWindows()) {
            Assert.Ignore("This test pins the Windows no-op return; non-Windows path returns false.");
        }
        var backend = new AscomSimulatorBackend(NullLogger<AscomSimulatorBackend>.Instance);
        Assert.That(await backend.AddDeviceAsync("ccd"), Is.True);
        Assert.That(await backend.RemoveDeviceAsync("ccd"), Is.True);
    }

    [Test]
    public async Task AddRemove_OnUnsupportedOs_ReturnsFalse() {
        if (OperatingSystem.IsWindows()) Assert.Ignore("Non-Windows path.");
        var backend = new AscomSimulatorBackend(NullLogger<AscomSimulatorBackend>.Instance);
        Assert.That(await backend.AddDeviceAsync("ccd"), Is.False);
        Assert.That(await backend.RemoveDeviceAsync("ccd"), Is.False);
    }

    // --- WindowsCandidatePaths ---

    [Test]
    public void WindowsCandidatePaths_FirstEntryIsBareExeForPathLookup() {
        // The first entry is the bare exe name; the resolver uses
        // `where` to search PATH. If a user customised the install
        // location but added it to PATH, we still find it.
        Assert.That(AscomSimulatorBackend.WindowsCandidatePaths[0],
            Is.EqualTo("AlpacaOmniSimulator.exe"));
    }

    [Test]
    public void WindowsCandidatePaths_IncludesStandardInstallDirs() {
        var paths = string.Join("\n", AscomSimulatorBackend.WindowsCandidatePaths);
        Assert.That(paths, Does.Contain(@"Program Files"));
        Assert.That(paths, Does.Contain(@"OmniSimulators"));
    }
}

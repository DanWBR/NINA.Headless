using NINA.Polaris.Services;
using NUnit.Framework;

namespace NINA.Polaris.Test;

/// <summary>
/// Tests for the pure-string helpers in HostInfo. The IO-heavy
/// DetectLinux/DetectWindows are not unit-tested directly — they hit
/// real /proc and WMI which differ per host; the classifier +
/// label-builder + placeholder-detector logic is what's worth
/// pinning down.
/// </summary>
[TestFixture]
public class HostInfoTests {

    [TestCase("Raspberry Pi 5 Model B Rev 1.0", "raspberry-pi")]
    [TestCase("Raspberry Pi 4 Model B Rev 1.4", "raspberry-pi")]
    [TestCase("Raspberry Pi 3 Model B Plus Rev 1.3", "raspberry-pi")]
    [TestCase("NVIDIA Jetson Nano Developer Kit", "jetson")]
    [TestCase("Radxa Rock Pi 4B", "rockpi")]
    [TestCase("ROCK Pi 5B (RK3588)", "rockpi")]
    [TestCase("Hardkernel Odroid-N2Plus", "odroid")]
    [TestCase("Intel NUC11PAHi7", "mini-pc")]
    [TestCase("VMware Virtual Platform", "vm")]
    [TestCase("Innotek GmbH VirtualBox", "vm")]
    [TestCase("QEMU Standard PC", "vm")]
    [TestCase("Dell Inc. PowerEdge R720", "linux")]   // no rule matches → generic linux
    public void ClassifyLinuxModel_RecognisesCommonHardware(string model, string expected) {
        Assert.That(HostInfo.ClassifyLinuxModel(model), Is.EqualTo(expected));
    }

    [TestCase("Intel NUC11PAHi7", "mini-pc")]
    [TestCase("VMware Virtual Platform", "vm")]
    [TestCase("Microsoft Hyper-V Virtual Machine", "vm")]
    [TestCase("Dell Latitude 7420", "windows")]
    [TestCase("ASUS PRIME X670-P", "windows")]
    public void ClassifyWindowsModel_RecognisesCommonHardware(string model, string expected) {
        Assert.That(HostInfo.ClassifyWindowsModel(model), Is.EqualTo(expected));
    }

    [Test]
    public void BuildShortLabel_StripsManufacturerNoise() {
        // Dell + similar OEMs love trailing "Inc.", "Corporation",
        // "Computer" — drops them to keep the bar readable.
        Assert.That(HostInfo.BuildShortLabel("linux", "Dell Inc. PowerEdge R720", null, "x64"),
            Is.EqualTo("Dell PowerEdge R720"));
        Assert.That(HostInfo.BuildShortLabel("linux", "Hewlett-Packard Corporation EliteBook 840", null, "x64"),
            Is.EqualTo("Hewlett-Packard EliteBook 840"));
    }

    [Test]
    public void BuildShortLabel_StripsRevSuffix() {
        // Silicon revisions ("Rev 1.0", "Rev 1.4") are noise to a
        // user reading the status bar.
        Assert.That(HostInfo.BuildShortLabel("raspberry-pi", "Raspberry Pi 5 Model B Rev 1.0", null, "arm64"),
            Is.EqualTo("Raspberry Pi 5 Model B"));
    }

    [Test]
    public void BuildShortLabel_TruncatesAtMaxLength() {
        var long_ = new string('X', 80);
        var s = HostInfo.BuildShortLabel("linux", long_, null, "x64");
        Assert.That(s.Length, Is.LessThanOrEqualTo(56));
        Assert.That(s, Does.EndWith("..."));
    }

    [Test]
    public void BuildShortLabel_UnknownModelWithCpu_FallsBackToCpu() {
        // When the OEM left a placeholder we want at least the CPU
        // to identify the box — better than "Host (x64)".
        Assert.That(HostInfo.BuildShortLabel("windows", "Unknown", "AMD Ryzen 9 7950X", "x64"),
            Is.EqualTo("AMD Ryzen 9 7950X"));
    }

    [Test]
    public void BuildShortLabel_UnknownModelNoCpu_GivesArchFallback() {
        Assert.That(HostInfo.BuildShortLabel("generic", "Unknown", null, "arm64"),
            Is.EqualTo("Host (arm64)"));
        Assert.That(HostInfo.BuildShortLabel("generic", "", null, "x64"),
            Is.EqualTo("Host (x64)"));
    }

    // --- Placeholder detection: the whole point of this round of fixes.
    [TestCase("System Product Name", true)]
    [TestCase("SYSTEM PRODUCT NAME", true)]      // case-insensitive
    [TestCase("All Series", true)]
    [TestCase("To Be Filled By O.E.M.", true)]
    [TestCase("To be filled by OEM", true)]
    [TestCase("Default string", true)]
    [TestCase("Default String", true)]
    [TestCase("Not Specified", true)]
    [TestCase("None", true)]
    [TestCase("Unknown", true)]
    [TestCase("N/A", true)]
    [TestCase("", true)]
    [TestCase(null, true)]
    [TestCase("   ", true)]
    [TestCase("Raspberry Pi 5 Model B Rev 1.0", false)]
    [TestCase("Dell Latitude 7420", false)]
    [TestCase("ASUS PRIME X670-P", false)]
    [TestCase("MSI MAG B650 TOMAHAWK WIFI", false)]
    [TestCase("VMware Virtual Platform", false)]
    [TestCase("Intel NUC11PAHi7", false)]
    public void IsPlaceholderModel_DistinguishesRealVsPlaceholder(string? input, bool expected) {
        Assert.That(HostInfo.IsPlaceholderModel(input), Is.EqualTo(expected));
    }

    // --- Manufacturer + model combination.
    [TestCase("ASUS", "ROG STRIX Z690-E GAMING", "ASUS ROG STRIX Z690-E GAMING")]
    [TestCase("Dell Inc.", "Latitude 7420", "Dell Inc. Latitude 7420")]
    [TestCase("ASUS", "ASUS Prime X670-P", "ASUS Prime X670-P")]   // dedupes
    [TestCase("", "Custom", "Custom")]
    [TestCase("Brand", "", "Brand")]
    [TestCase(null, "Just Model", "Just Model")]
    [TestCase("Just Brand", null, "Just Brand")]
    public void CombineMfgAndModel_WorksAsExpected(string? mfg, string? model, string expected) {
        Assert.That(HostInfo.CombineMfgAndModel(mfg, model), Is.EqualTo(expected));
    }

    [Test]
    public void CombineMfgAndModel_BothEmpty_ReturnsNull() {
        Assert.That(HostInfo.CombineMfgAndModel(null, null), Is.Null);
        Assert.That(HostInfo.CombineMfgAndModel("", ""), Is.Null);
    }

    // --- CPU brand normalisation.
    [TestCase("Intel(R) Core(TM) i7-12700K CPU @ 3.60GHz", "Intel Core i7-12700K")]
    [TestCase("Intel(R) Xeon(R) Gold 6248R CPU @ 3.00GHz", "Intel Xeon Gold 6248R")]
    [TestCase("AMD Ryzen 9 7950X 16-Core Processor", "AMD Ryzen 9 7950X")]
    [TestCase("AMD Ryzen 5 5600X 6-Core Processor", "AMD Ryzen 5 5600X")]
    [TestCase("Apple M2 Pro", "Apple M2 Pro")]
    [TestCase("ARM Cortex-A76 r4p1", "ARM Cortex-A76 r4p1")]
    public void NormaliseCpuName_StripsVendorMarketingNoise(string raw, string expected) {
        Assert.That(HostInfo.NormaliseCpuName(raw), Is.EqualTo(expected));
    }

    [Test]
    public void NormaliseCpuName_NullOrEmpty_ReturnsNull() {
        Assert.That(HostInfo.NormaliseCpuName(null), Is.Null);
        Assert.That(HostInfo.NormaliseCpuName(""), Is.Null);
        Assert.That(HostInfo.NormaliseCpuName("   "), Is.Null);
    }

    [Test]
    public void Current_ReturnsCachedInstance_AcrossCalls() {
        // HostInfo.Current is a Lazy<T>; subsequent calls must not
        // re-run the (relatively expensive) detection path.
        var a = HostInfo.Current;
        var b = HostInfo.Current;
        Assert.That(b, Is.SameAs(a));
    }

    [Test]
    public void Current_HasNonEmptyFields() {
        // Best-effort detection should always populate every field
        // with at least a fallback — the UI binds to ShortLabel
        // and expects a string. Cpu is optional (null on systems
        // that don't expose /proc/cpuinfo or WMI).
        var info = HostInfo.Current;
        Assert.That(info.Kind, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Model, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Os, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Architecture, Is.Not.Null.And.Not.Empty);
        Assert.That(info.Cores, Is.GreaterThan(0));
        Assert.That(info.ShortLabel, Is.Not.Null.And.Not.Empty);
        // Cpu may legitimately be null on hosts where the underlying
        // syscall failed (locked-down container, exotic kernel) —
        // don't assert non-null here.
    }
}

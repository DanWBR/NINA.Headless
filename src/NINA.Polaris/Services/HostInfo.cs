using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace NINA.Polaris.Services;

/// <summary>
/// Read-once host identification — what kind of machine is Polaris
/// running on. Shown in the activity bar so the user (and remote
/// support) can see at a glance "Raspberry Pi 5 / Linux arm64" vs
/// "Intel NUC11 / Windows 11 x64".
///
/// All detection happens at process start. Hardware doesn't change
/// at runtime; caching the snapshot keeps the WS payload cheap.
///
/// The detection is **best effort**: missing files, locked-down
/// containers, or exotic distros fall through to a generic OS
/// description rather than throwing. Fields are always populated
/// with at least a non-empty fallback so the UI never has to
/// render an empty string.
/// </summary>
public static class HostInfo {
    private static readonly Lazy<HostDeviceInfo> _cached = new(Detect, isThreadSafe: true);

    /// <summary>Detected once on first access, then cached for the
    /// process lifetime. Subsequent reads are free.</summary>
    public static HostDeviceInfo Current => _cached.Value;

    private static HostDeviceInfo Detect() {
        var os = RuntimeInformation.OSDescription;
        var arch = RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var kind = "generic";
        var model = "Unknown";
        string? cpu = null;

        try {
            if (OperatingSystem.IsLinux()) {
                (kind, model) = DetectLinux();
                cpu = DetectLinuxCpu();
            } else if (OperatingSystem.IsWindows()) {
                (kind, model) = DetectWindows();
                cpu = NormaliseCpuName(QueryWmi("Win32_Processor", "Name"));
            } else if (OperatingSystem.IsMacOS()) {
                (kind, model) = ("mac", "Apple Mac");
            }
        } catch {
            // Detection is best-effort; don't let a missing file or
            // a locked-down container break the activity bar.
        }

        return new HostDeviceInfo(
            Kind: kind,
            Model: model,
            Os: os,
            Architecture: arch,
            Cores: Math.Max(1, Environment.ProcessorCount),
            ShortLabel: BuildShortLabel(kind, model, cpu, arch),
            Cpu: cpu);
    }

    /// <summary>Linux detection priority:
    /// 1. <c>/proc/device-tree/model</c> — single source of truth on
    ///    Raspberry Pi + most ARM SBCs (NVIDIA Jetson, Rock Pi, ...).
    /// 2. <c>/sys/class/dmi/id/{sys_vendor,product_name}</c> — x86
    ///    standard, populated on basically every modern PC/server/laptop.
    /// 3. <c>/sys/class/dmi/id/{board_vendor,board_name}</c> —
    ///    motherboard fallback when product_name is an OEM placeholder
    ///    ("System Product Name", "All Series", "Default string", ...).
    /// 4. <c>/proc/cpuinfo</c> "Model" line — older RPi fallback.
    /// 5. CPU model + "Custom build" as last resort.</summary>
    internal static (string Kind, string Model) DetectLinux() {
        // 1. device-tree (RPi, Jetson, Rock Pi, Odroid, ...)
        const string dtPath = "/proc/device-tree/model";
        if (File.Exists(dtPath)) {
            // device-tree files end with a stray null byte
            var dt = File.ReadAllText(dtPath).TrimEnd('\0', ' ', '\n', '\r');
            if (!string.IsNullOrWhiteSpace(dt)) {
                return (ClassifyLinuxModel(dt), dt);
            }
        }

        // 2. DMI sys (x86 standard)
        var vendor = ReadFirstLine("/sys/class/dmi/id/sys_vendor");
        var product = ReadFirstLine("/sys/class/dmi/id/product_name");
        var combined = CombineMfgAndModel(vendor, product);
        if (!IsPlaceholderModel(combined)) {
            return (ClassifyLinuxModel(combined!), combined!);
        }

        // 3. DMI board (motherboard) — fills in for DIY builds where
        //    sys_vendor/product_name are OEM placeholders
        var boardVendor = ReadFirstLine("/sys/class/dmi/id/board_vendor");
        var boardName = ReadFirstLine("/sys/class/dmi/id/board_name");
        var boardCombined = CombineMfgAndModel(boardVendor, boardName);
        if (!IsPlaceholderModel(boardCombined)) {
            return (ClassifyLinuxModel(boardCombined!), boardCombined!);
        }

        // 4. /proc/cpuinfo "Model" line — old RPi 1/2/3 firmware
        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("Model", StringComparison.OrdinalIgnoreCase)) {
                    var idx = line.IndexOf(':');
                    if (idx > 0) {
                        var v = line[(idx + 1)..].Trim();
                        if (!string.IsNullOrWhiteSpace(v)) {
                            return (ClassifyLinuxModel(v), v);
                        }
                    }
                }
            }
        } catch { /* /proc not readable, skip */ }

        // 5. CPU-only fallback for headless servers + DIY builds
        //    with totally empty DMI
        var cpu = DetectLinuxCpu();
        if (!string.IsNullOrWhiteSpace(cpu)) {
            return ("linux", $"Custom build · {cpu}");
        }
        return ("linux", $"Linux {RuntimeInformation.OSArchitecture}");
    }

    /// <summary>Windows detection: WMI is the canonical source but
    /// the `System.Management` namespace is Windows-only + adds a
    /// nontrivial dep. <c>ManagementObjectSearcher</c> via
    /// reflection-style access is overkill; instead we shell out to
    /// PowerShell <c>Get-CimInstance Win32_ComputerSystem</c> which
    /// ships in every Windows since 2012. The call runs once at
    /// startup, ~150 ms per query (we do 2-4 queries).
    ///
    /// Fallback chain for DIY builds where the OEM left
    /// Win32_ComputerSystem.Model as a placeholder
    /// ("System Product Name", "All Series", "To Be Filled By O.E.M.",
    /// etc.):
    /// 1. Win32_ComputerSystem.{Manufacturer,Model} — works for laptops
    ///    + branded desktops + servers + every VM
    /// 2. Win32_BaseBoard.{Manufacturer,Product} — motherboard. For
    ///    a DIY PC this often reads "ASUS PRIME X670-P" / "MSI MAG B650"
    ///    which is what the user actually identifies the build by
    /// 3. CPU model + "Custom build" — last resort so the chip is
    ///    never just "ASUS System Product Name"
    /// </summary>
    internal static (string Kind, string Model) DetectWindows() {
        var sysCombined = CombineMfgAndModel(
            QueryWmi("Win32_ComputerSystem", "Manufacturer"),
            QueryWmi("Win32_ComputerSystem", "Model"));

        if (!IsPlaceholderModel(sysCombined)) {
            return (ClassifyWindowsModel(sysCombined!), sysCombined!);
        }

        // Fallback 1: motherboard
        var boardCombined = CombineMfgAndModel(
            QueryWmi("Win32_BaseBoard", "Manufacturer"),
            QueryWmi("Win32_BaseBoard", "Product"));
        if (!IsPlaceholderModel(boardCombined)) {
            return (ClassifyWindowsModel(boardCombined!), boardCombined!);
        }

        // Fallback 2: CPU model
        var cpu = NormaliseCpuName(QueryWmi("Win32_Processor", "Name"));
        if (!string.IsNullOrWhiteSpace(cpu)) {
            return ("windows", $"Custom build · {cpu}");
        }
        return ("windows", "Windows PC");
    }

    /// <summary>Map a verbose model string to a short device kind so
    /// the UI can pick an icon (raspberry / mini-pc / pc / server /
    /// generic). String matching only — no syscalls.</summary>
    internal static string ClassifyLinuxModel(string model) {
        var m = model.ToLowerInvariant();
        if (m.Contains("raspberry pi")) return "raspberry-pi";
        if (m.Contains("jetson")) return "jetson";
        if (m.Contains("rock pi") || m.Contains("rockpi") || m.Contains("rk35")) return "rockpi";
        if (m.Contains("odroid")) return "odroid";
        if (m.Contains("nuc")) return "mini-pc";
        if (m.Contains("nano")) return "mini-pc";
        if (m.Contains("vmware") || m.Contains("virtualbox") || m.Contains("kvm") || m.Contains("qemu")) return "vm";
        return "linux";
    }

    internal static string ClassifyWindowsModel(string model) {
        var m = model.ToLowerInvariant();
        if (m.Contains("nuc")) return "mini-pc";
        if (m.Contains("virtual") || m.Contains("vmware") || m.Contains("hyper-v")) return "vm";
        return "windows";
    }

    /// <summary>True when the given string is empty, whitespace, or
    /// one of the well-known OEM placeholders that motherboard makers
    /// stamp when the system builder didn't customise SMBIOS strings.
    /// Driving the fallback to motherboard / CPU info in that case.</summary>
    internal static bool IsPlaceholderModel(string? s) {
        if (string.IsNullOrWhiteSpace(s)) return true;
        var lower = s.ToLowerInvariant();
        // The full set of OEM placeholders SMBIOS tools complain about.
        // See for example dmidecode's bad-strings list.
        string[] placeholders = [
            "system product name",
            "system manufacturer",
            "system version",
            "system serial number",
            "all series",
            "to be filled by o.e.m.",
            "to be filled by oem",
            "default string",
            "not specified",
            "not available",
            "not applicable",
            "no enclosure",
            "oem",
            "none",
            "unknown",
            "n/a",
        ];
        // Exact match OR the placeholder is the only content (manufacturer
        // sometimes appends garbage like "0x01" to a placeholder).
        foreach (var p in placeholders) {
            if (lower == p) return true;
            if (lower.Contains(p) && lower.Length < p.Length + 20) return true;
        }
        return false;
    }

    /// <summary>Compact one-liner the activity bar shows. Drops the
    /// "Computer/Corporation/Inc." noise some manufacturers stamp and
    /// the trailing "Rev 1.2" silicon revisions that no human cares
    /// about in a status bar.</summary>
    internal static string BuildShortLabel(string kind, string model, string? cpu, string arch) {
        if (string.IsNullOrWhiteSpace(model) || model == "Unknown") {
            return !string.IsNullOrWhiteSpace(cpu) ? cpu : $"Host ({arch})";
        }
        var s = model;
        s = Regex.Replace(s, @"\s+Rev\s+\d+(\.\d+)*", "", RegexOptions.IgnoreCase);
        // Use (?=\s|$) instead of \b because `\b` doesn't trigger
        // between two non-word chars (the trailing "." in "Inc." +
        // following " ") and would otherwise leave a dangling period.
        s = Regex.Replace(s, @"\s+(Inc\.?|Corporation|Computer|Computers|LLC|Ltd\.?|Co\.?\s*Ltd\.?)(?=\s|$)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        // Truncate at 56 chars (was 48) — CPU-fallback strings like
        // "Custom build · Intel Core i7-12700K" need a bit more room.
        return s.Length > 56 ? s[..53] + "..." : s;
    }

    /// <summary>Combine a manufacturer + model into a friendly label,
    /// avoiding "ASUS ASUS Prime X670-P" duplication when the model
    /// already includes the brand. Returns null if both empty.</summary>
    internal static string? CombineMfgAndModel(string? mfg, string? model) {
        var m = model?.Trim();
        var v = mfg?.Trim();
        if (string.IsNullOrEmpty(m)) return string.IsNullOrEmpty(v) ? null : v;
        if (string.IsNullOrEmpty(v)) return m;
        return m.Contains(v, StringComparison.OrdinalIgnoreCase) ? m : $"{v} {m}";
    }

    /// <summary>Strip the marketing-y noise out of a CPU brand string.
    /// Intel reports "Intel(R) Core(TM) i7-12700K CPU @ 3.60GHz",
    /// AMD reports "AMD Ryzen 9 7950X 16-Core Processor" — neither
    /// adds value past the model number when shown in a status bar.</summary>
    internal static string? NormaliseCpuName(string? raw) {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Trim();
        s = Regex.Replace(s, @"\(R\)|\(TM\)|\(r\)|\(tm\)", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+CPU\s*@.*$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+\d+-Core\s+Processor", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s+Processor$", "", RegexOptions.IgnoreCase);
        s = Regex.Replace(s, @"\s{2,}", " ").Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    /// <summary>Read the first "model name" line out of /proc/cpuinfo.
    /// Returns the normalised brand string (or null on x86 systems
    /// where /proc isn't readable, or ARM systems where cpuinfo uses
    /// different field names).</summary>
    internal static string? DetectLinuxCpu() {
        try {
            foreach (var line in File.ReadLines("/proc/cpuinfo")) {
                if (line.StartsWith("model name", StringComparison.OrdinalIgnoreCase)) {
                    var idx = line.IndexOf(':');
                    if (idx > 0) return NormaliseCpuName(line[(idx + 1)..]);
                }
            }
        } catch { /* /proc not readable */ }
        return null;
    }

    private static string? ReadFirstLine(string path) {
        try {
            if (!File.Exists(path)) return null;
            var s = File.ReadAllText(path).Trim();
            return string.IsNullOrWhiteSpace(s) ? null : s;
        } catch { return null; }
    }

    private static string? QueryWmi(string @class, string property) {
        try {
            var psi = new System.Diagnostics.ProcessStartInfo {
                FileName = "powershell",
                Arguments = $"-NoProfile -NonInteractive -Command \"(Get-CimInstance {@class}).{property}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            using var proc = System.Diagnostics.Process.Start(psi);
            if (proc == null) return null;
            if (!proc.WaitForExit(5000)) {
                try { proc.Kill(); } catch { }
                return null;
            }
            var output = proc.StandardOutput.ReadToEnd().Trim();
            return string.IsNullOrWhiteSpace(output) ? null : output;
        } catch { return null; }
    }
}

/// <summary>
/// Immutable snapshot of the host's identity. Constructed once at
/// startup via <see cref="HostInfo.Current"/> and serialised into the
/// status WebSocket payload alongside the live metrics so the UI can
/// show a label like "Raspberry Pi 5 Model B Rev 1.0" next to CPU%.
/// </summary>
/// <param name="Kind">Coarse classification for icon selection:
/// raspberry-pi / jetson / rockpi / odroid / mini-pc / vm / windows
/// / linux / mac / generic.</param>
/// <param name="Model">Full model string as reported by hardware.
/// "Raspberry Pi 5 Model B Rev 1.0", "Intel(R) NUC11PAHi7", etc.
/// On DIY builds where the OEM left placeholders in SMBIOS, falls
/// back to the motherboard model and finally to
/// "Custom build · {CpuModel}".</param>
/// <param name="Os">Free-form OS description from
/// <see cref="RuntimeInformation.OSDescription"/>.</param>
/// <param name="Architecture">x64 / arm64 / x86 / arm.</param>
/// <param name="Cores">Logical processor count.</param>
/// <param name="ShortLabel">Trimmed label suitable for the activity
/// bar — manufacturer noise and silicon-revision suffixes removed,
/// capped at 56 chars.</param>
/// <param name="Cpu">Normalised CPU brand string ("Intel Core i7-12700K",
/// "AMD Ryzen 9 7950X", "ARM Cortex-A76") with the vendor noise
/// stripped. Null if not detectable. Useful in the tooltip even when
/// the device chip shows a fancy mini-PC name.</param>
public sealed record HostDeviceInfo(
    string Kind,
    string Model,
    string Os,
    string Architecture,
    int Cores,
    string ShortLabel,
    string? Cpu);

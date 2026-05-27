using System.Diagnostics;
using Microsoft.Extensions.Diagnostics.ResourceMonitoring;
using Microsoft.Extensions.Hosting;

// IResourceMonitor is marked obsolete in 10.x in favour of
// OpenTelemetry observable instruments. We knowingly use the
// imperative API because it returns a synchronous snapshot the
// activity bar consumes once per second; switching to the OTEL
// path would require an extra subscription + aggregation layer
// for no UX gain. When the API is actually removed (no earlier
// than .NET 12 per the announcement) we revisit.
#pragma warning disable EXTOBS0001

namespace NINA.Polaris.Services;

/// <summary>
/// Background sampler for host-level CPU + memory metrics. Powers the
/// activity bar at the bottom of the UI. Samples every 2 seconds
/// (the minimum window <see cref="IResourceMonitor.GetUtilization"/>
/// needs for a meaningful CPU%); the status WebSocket broadcasts the
/// most recent snapshot at its own 1 Hz cadence (so a snapshot may
/// reach the client twice, harmless, the UI just renders the same
/// value).
///
/// Two CPU numbers are exposed:
///   - <c>CpuPercent</c> = system-wide, all processes combined,
///     normalised to 100% regardless of core count.
///   - <c>ProcessCpuPercent</c> = the Polaris process alone, also
///     normalised to 100% (so the user sees "12%" instead of "192%
///     because Polaris is using two cores fully").
///
/// Memory:
///   - <c>MemoryUsedMB</c> / <c>MemoryTotalMB</c> = system-wide
///     (derived from <see cref="IResourceMonitor"/> percentage and
///     <see cref="GC.GetGCMemoryInfo"/>'s OS-allocated ceiling).
///   - <c>ProcessMemoryMB</c> = Polaris's WorkingSet64.
/// </summary>
public class HostMetricsService : BackgroundService {
    private readonly IResourceMonitor _monitor;
    private readonly ILogger<HostMetricsService> _logger;

    /// <summary>Most recent successful sample. Initialised to zeros.</summary>
    public HostMetricsSnapshot Latest { get; private set; } = new();

    /// <summary>Host hardware identification, detected once at
    /// startup, broadcast verbatim in every snapshot so the UI can
    /// label the activity bar.</summary>
    public HostDeviceInfo Device { get; } = HostInfo.Current;

    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    public HostMetricsService(IResourceMonitor monitor, ILogger<HostMetricsService> logger) {
        _monitor = monitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        var process = Process.GetCurrentProcess();
        var lastCpuTime = process.TotalProcessorTime;
        var lastSampleTime = DateTime.UtcNow;
        var coreCount = Math.Max(1, Environment.ProcessorCount);

        // First sample skipped, TotalProcessorTime delta needs a
        // reference window, so we wait one interval before the first
        // valid emit.
        await Task.Delay(SampleInterval, stoppingToken);

        // Seed Latest immediately with the device info so the first
        // status broadcast (which may happen before the first sample
        // completes) already carries the host label.
        Latest = Latest with { Device = Device };

        while (!stoppingToken.IsCancellationRequested) {
            try {
                Latest = Sample(process, ref lastCpuTime, ref lastSampleTime, coreCount) with { Device = Device };
            } catch (Exception ex) {
                // Defensive: cgroups not mounted, ResourceMonitor
                // edge cases, or a transient PerformanceCounter
                // hiccup on Windows. Keep the last good Latest so
                // the UI doesn't flash zeros.
                _logger.LogDebug(ex, "HostMetrics sample failed; keeping last good snapshot");
            }
            try {
                await Task.Delay(SampleInterval, stoppingToken);
            } catch (OperationCanceledException) {
                break;
            }
        }
    }

    /// <summary>
    /// Public for unit tests, pulls one snapshot off the monitor
    /// and the current process. Updates the in/out cpu trackers in
    /// place so the caller can call repeatedly.
    /// </summary>
    public HostMetricsSnapshot Sample(Process process,
                                       ref TimeSpan lastCpuTime,
                                       ref DateTime lastSampleTime,
                                       int coreCount) {
        var util = _monitor.GetUtilization(SampleInterval);

        long totalBytes;
        long usedBytes;
        double usedPercent;

        // Prefer /proc/meminfo on Linux. The default IResourceMonitor
        // path counts buff/cache as used memory, which makes a healthy
        // Pi with lots of file cache show 100% RAM and look alarming.
        // /proc/meminfo's MemAvailable subtracts reclaimable pages,
        // matching what `free -h` reports under "available" and what
        // actually matters for memory pressure.
        if (OperatingSystem.IsLinux() && TryReadProcMeminfo(out var procTotal, out var procAvailable)) {
            totalBytes = procTotal;
            usedBytes = Math.Max(0, procTotal - procAvailable);
            usedPercent = procTotal > 0
                ? Math.Min(100.0, 100.0 * usedBytes / procTotal)
                : 0;
        } else {
            // Fallback (Windows, edge cases): GC info gives the OS
            // memory ceiling, IResourceMonitor gives the percentage.
            var gcInfo = GC.GetGCMemoryInfo();
            totalBytes = gcInfo.TotalAvailableMemoryBytes;
            usedBytes = (long)(totalBytes * util.MemoryUsedPercentage / 100.0);
            usedPercent = util.MemoryUsedPercentage;
        }

        var now = DateTime.UtcNow;
        var elapsedMs = (now - lastSampleTime).TotalMilliseconds * coreCount;
        var cpuDeltaMs = (process.TotalProcessorTime - lastCpuTime).TotalMilliseconds;
        var processCpu = elapsedMs > 0
            ? Math.Max(0, Math.Min(100.0, 100.0 * cpuDeltaMs / elapsedMs))
            : 0.0;
        lastCpuTime = process.TotalProcessorTime;
        lastSampleTime = now;

        // Refresh the process snapshot so WorkingSet64 reflects the
        // most recent value instead of the value at Process.GetCurrentProcess().
        process.Refresh();

        return new HostMetricsSnapshot {
            CpuPercent = Math.Round(util.CpuUsedPercentage, 1),
            MemoryPercent = Math.Round(usedPercent, 1),
            MemoryUsedMB = usedBytes / (1024 * 1024),
            MemoryTotalMB = totalBytes / (1024 * 1024),
            ProcessCpuPercent = Math.Round(processCpu, 1),
            ProcessMemoryMB = process.WorkingSet64 / (1024 * 1024),
            SampledAt = now
        };
    }

    /// <summary>
    /// Parse the two lines we care about out of /proc/meminfo. Returns
    /// false on any IO error or unparseable line so the caller can fall
    /// back to the cross-platform path.
    /// </summary>
    internal static bool TryReadProcMeminfo(out long totalBytes, out long availableBytes) {
        totalBytes = 0;
        availableBytes = 0;
        try {
            foreach (var line in File.ReadLines("/proc/meminfo")) {
                if (totalBytes == 0 && line.StartsWith("MemTotal:", StringComparison.Ordinal)) {
                    totalBytes = ParseKibLine(line);
                } else if (availableBytes == 0 && line.StartsWith("MemAvailable:", StringComparison.Ordinal)) {
                    availableBytes = ParseKibLine(line);
                }
                if (totalBytes > 0 && availableBytes > 0) break;
            }
        } catch {
            return false;
        }
        return totalBytes > 0 && availableBytes > 0;
    }

    private static long ParseKibLine(string line) {
        // Format: "MemTotal:        4015896 kB"
        var parts = line.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && long.TryParse(parts[1], out var kb)) {
            return kb * 1024L;
        }
        return 0;
    }
}

/// <summary>
/// Immutable snapshot of host metrics. Serialised verbatim into the
/// status WebSocket payload. All numbers rounded to 1 decimal so
/// the UI doesn't display jittery sub-percent values.
/// </summary>
public sealed record HostMetricsSnapshot {
    public double CpuPercent { get; init; }
    public double MemoryPercent { get; init; }
    public long MemoryUsedMB { get; init; }
    public long MemoryTotalMB { get; init; }
    public double ProcessCpuPercent { get; init; }
    public long ProcessMemoryMB { get; init; }
    public DateTime SampledAt { get; init; }

    /// <summary>Host hardware identification, same instance is shared
    /// across every snapshot (detection is one-shot at startup). Null
    /// before the first <see cref="HostMetricsService.ExecuteAsync"/>
    /// tick runs.</summary>
    public HostDeviceInfo? Device { get; init; }
}

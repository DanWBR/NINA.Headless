using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.ResourceMonitoring;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using System.Collections.Generic;
using System.Diagnostics;

#pragma warning disable EXTOBS0001

namespace NINA.Polaris.Test;

/// <summary>
/// Pins the HostMetricsService snapshot shape + the public Sample()
/// method's behaviour around process CPU computation. The full
/// BackgroundService loop is not exercised here, it needs a live
/// IResourceMonitor implementation which only resolves via the real
/// DI graph. Tests use a hand-rolled stub that returns canned
/// utilisation numbers.
/// </summary>
[TestFixture]
public class HostMetricsServiceTests {

    private sealed class StubResourceMonitor : IResourceMonitor {
        public double Cpu = 12.3;
        public double Mem = 45.6;
        private const ulong MaxMemBytes = 1_000_000_000;
        public ResourceUtilization GetUtilization(TimeSpan window) {
            // ResourceUtilization.MemoryUsedPercentage is COMPUTED
            // inside the type as 100 * memoryUsedInBytes /
            // maximumMemoryInBytes. To stub a specific percentage
            // we have to back-solve the bytes value.
            var bytes = (ulong)(Mem / 100.0 * MaxMemBytes);
            return new ResourceUtilization(
                cpuUsedPercentage: Cpu,
                memoryUsedInBytes: bytes,
                systemResources: new SystemResources(
                    guaranteedCpuUnits: 1.0,
                    maximumCpuUnits: 1.0,
                    guaranteedMemoryInBytes: MaxMemBytes,
                    maximumMemoryInBytes: MaxMemBytes));
        }
    }

    private static ProfileService MakeProfileService() {
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        return new ProfileService(cfg, NullLogger<ProfileService>.Instance);
    }

    [Test]
    public void Snapshot_Defaults_AreZero() {
        // Before the first sample, Latest holds the zero record.
        // The UI handles that as "no data yet", important the
        // service doesn't expose null or throw.
        var stub = new StubResourceMonitor();
        var svc = new HostMetricsService(stub, MakeProfileService(), NullLogger<HostMetricsService>.Instance);
        Assert.That(svc.Latest, Is.Not.Null);
        Assert.That(svc.Latest.CpuPercent, Is.EqualTo(0));
        Assert.That(svc.Latest.MemoryUsedMB, Is.EqualTo(0));
        Assert.That(svc.Latest.ProcessCpuPercent, Is.EqualTo(0));
    }

    [Test]
    public void Sample_PopulatesAllFields() {
        // Drive Sample directly with the stub. We can't predict
        // ProcessCpuPercent precisely (depends on host scheduling),
        // but every other field has a deterministic source.
        var stub = new StubResourceMonitor { Cpu = 25.5, Mem = 60.0 };
        var svc = new HostMetricsService(stub, MakeProfileService(), NullLogger<HostMetricsService>.Instance);

        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastTime = DateTime.UtcNow.AddSeconds(-1);   // small but positive window
        var snap = svc.Sample(proc, ref lastCpu, ref lastTime,
            coreCount: Environment.ProcessorCount);

        Assert.That(snap.CpuPercent, Is.EqualTo(25.5));
        Assert.That(snap.MemoryPercent, Is.EqualTo(60.0));
        Assert.That(snap.MemoryTotalMB, Is.GreaterThan(0));
        Assert.That(snap.MemoryUsedMB, Is.GreaterThan(0));
        Assert.That(snap.ProcessMemoryMB, Is.GreaterThan(0),
            "Polaris process must have non-zero working set");
        Assert.That(snap.ProcessCpuPercent, Is.InRange(0.0, 100.0),
            "Process CPU must be clamped to [0, 100]");
        Assert.That(snap.SampledAt, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-5)),
            "Snapshot timestamp should be recent");
    }

    [Test]
    public void Sample_RoundedToOneDecimal() {
        // UI doesn't need sub-percent resolution and jittery numbers
        // (38.213% → 38.198% → 38.241%) look broken. Round at the
        // source.
        var stub = new StubResourceMonitor { Cpu = 38.21385, Mem = 12.94912 };
        var svc = new HostMetricsService(stub, MakeProfileService(), NullLogger<HostMetricsService>.Instance);

        var proc = Process.GetCurrentProcess();
        var lastCpu = proc.TotalProcessorTime;
        var lastTime = DateTime.UtcNow.AddSeconds(-1);
        var snap = svc.Sample(proc, ref lastCpu, ref lastTime, coreCount: 4);

        Assert.That(snap.CpuPercent, Is.EqualTo(38.2));
        Assert.That(snap.MemoryPercent, Is.EqualTo(12.9));
    }

    [Test]
    public void Sample_AdvancesCpuTracker() {
        // The two ref parameters must be updated so the next call
        // computes ProcessCpuPercent from the right window. If they
        // don't advance, every subsequent sample would compare
        // against the original baseline and the % drifts upward.
        var stub = new StubResourceMonitor();
        var svc = new HostMetricsService(stub, MakeProfileService(), NullLogger<HostMetricsService>.Instance);

        var proc = Process.GetCurrentProcess();
        var lastCpu = TimeSpan.FromSeconds(10);   // fake baseline
        var lastTime = DateTime.UtcNow.AddSeconds(-2);

        svc.Sample(proc, ref lastCpu, ref lastTime, coreCount: 4);

        Assert.That(lastCpu, Is.Not.EqualTo(TimeSpan.FromSeconds(10)),
            "lastCpu should be advanced to the current process CPU time");
        Assert.That(lastTime, Is.GreaterThan(DateTime.UtcNow.AddSeconds(-1)),
            "lastTime should be advanced to ~now");
    }
}

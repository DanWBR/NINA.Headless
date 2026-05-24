using Makaretu.Dns;

namespace NINA.Polaris.Services;

/// <summary>
/// Announces this N.I.N.A. Polaris instance on the local network via mDNS/Avahi
/// so that clients can find it at <c>nina.local:5000</c> (or whatever hostname
/// the OS reports) without needing to know its IP address.
///
/// Registered service type: <c>_nina._tcp.local</c> on the configured Kestrel
/// port (defaults to 5000). Instance name defaults to the machine hostname
/// but can be overridden via <c>Mdns:InstanceName</c> in appsettings.
/// </summary>
public class MdnsService : IHostedService, IDisposable {
    private readonly ILogger<MdnsService> _logger;
    private readonly IConfiguration _config;
    private ServiceDiscovery? _discovery;
    private MulticastService? _mdns;

    public MdnsService(ILogger<MdnsService> logger, IConfiguration config) {
        _logger = logger;
        _config = config;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        if (!_config.GetValue("Mdns:Enabled", true)) {
            _logger.LogInformation("mDNS disabled by configuration");
            return Task.CompletedTask;
        }

        try {
            var port = _config.GetValue("Mdns:Port", 5000);
            var hostname = Environment.MachineName;
            // Default instance name = "polaris" so the discoverable
            // URL is the friendly http://polaris.local:<port> on any
            // LAN that supports mDNS / Bonjour. Override via
            // Mdns:InstanceName when running multiple Polaris
            // instances on the same network (then second instance
            // collides and Bonjour appends a numeric suffix).
            var instanceName = _config.GetValue("Mdns:InstanceName", "polaris")!;

            _mdns = new MulticastService();
            _discovery = new ServiceDiscovery(_mdns);

            var profile = new ServiceProfile(instanceName, "_nina._tcp", (ushort)port);
            profile.AddProperty("version", "1.0");
            profile.AddProperty("path", "/");
            profile.AddProperty("hostname", hostname);

            _discovery.Advertise(profile);
            _mdns.Start();

            _logger.LogInformation(
                "mDNS advertising as {Instance}._nina._tcp.local on port {Port} (hostname: {Hostname})",
                instanceName, port, hostname);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "mDNS announcer failed to start — continuing without LAN discovery");
            _mdns = null;
            _discovery = null;
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) {
        try {
            _mdns?.Stop();
            _discovery?.Dispose();
        } catch (Exception ex) {
            _logger.LogDebug(ex, "mDNS shutdown error (ignored)");
        }
        _mdns = null;
        _discovery = null;
        return Task.CompletedTask;
    }

    public void Dispose() {
        _discovery?.Dispose();
    }
}

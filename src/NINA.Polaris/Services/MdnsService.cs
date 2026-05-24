using System.Net.Sockets;
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
            // Default instance name = "polaris-app" — discoverable as
            // http://polaris-app.local:<port> on any LAN with mDNS /
            // Bonjour. The bare "polaris" was avoided because a lot
            // of users already have a Pi or other gadget claiming
            // polaris.local; the -app suffix keeps the URL friendly
            // while preventing collisions. Override via
            // Mdns:InstanceName in appsettings for a custom name.
            var instanceName = _config.GetValue("Mdns:InstanceName", "polaris-app")!;

            _mdns = new MulticastService();
            _discovery = new ServiceDiscovery(_mdns);

            // Both ServiceProfile constructors (3-arg AND 4-arg) build
            // HostName as `{instance}.{servicePrefix}.local` — so for
            // service "_nina._tcp" we got `polaris-app.nina.local` in
            // the wild, confirmed in the user's startup log. Override
            // HostName explicitly to the URL we WANT the browser to
            // resolve, then patch the SRV record the constructor
            // added (it still points at the old HostName) and add A /
            // AAAA records that map the new HostName to our local IPs.
            var profile = new ServiceProfile(instanceName, "_nina._tcp", (ushort)port);
            var desiredHost = new DomainName($"{instanceName}.local");
            profile.HostName = desiredHost;
            foreach (var srv in profile.Resources.OfType<SRVRecord>()) {
                srv.Target = desiredHost;
            }

            var addresses = MulticastService.GetIPAddresses()
                .Where(addr =>
                    (addr.AddressFamily == AddressFamily.InterNetwork
                     || addr.AddressFamily == AddressFamily.InterNetworkV6)
                    && !System.Net.IPAddress.IsLoopback(addr))
                .ToList();
            foreach (var ip in addresses) {
                if (ip.AddressFamily == AddressFamily.InterNetwork) {
                    profile.Resources.Add(new ARecord {
                        Name = desiredHost, Address = ip
                    });
                } else {
                    profile.Resources.Add(new AAAARecord {
                        Name = desiredHost, Address = ip
                    });
                }
            }

            profile.AddProperty("version", "1.0");
            profile.AddProperty("path", "/");
            profile.AddProperty("hostname", hostname);

            _discovery.Advertise(profile);
            _mdns.Start();

            _logger.LogInformation(
                "mDNS advertising as {Instance}._nina._tcp.local at "
                + "{HostName}:{Port} (machine: {Hostname}, {AddrCount} IPs)",
                instanceName, profile.HostName, port, hostname, addresses.Count);
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

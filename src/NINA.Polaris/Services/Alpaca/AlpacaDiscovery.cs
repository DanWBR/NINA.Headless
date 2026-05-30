using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services.Alpaca;

/// <summary>
/// UDP-broadcast discovery for Alpaca servers on the local subnet.
/// Per the ASCOM Alpaca discovery protocol:
///   client sends "alpacadiscovery1" to UDP port 32227 (broadcast)
///   each server replies with JSON: {"AlpacaPort": 11111}
///
/// We also fetch each respondent's <c>/management/v1/configureddevices</c>
/// so the caller gets a flat list of (host, port, device-type, device-number,
/// device-name, unique-id) tuples ready to display.
/// </summary>
public class AlpacaDiscovery {
    private readonly ILogger<AlpacaDiscovery> _logger;
    private const int DiscoveryPort = 32227;
    private const string DiscoveryMessage = "alpacadiscovery1";

    public AlpacaDiscovery(ILogger<AlpacaDiscovery> logger) {
        _logger = logger;
    }

    public async Task<List<AlpacaServer>> DiscoverServersAsync(TimeSpan? timeout = null) {
        var to = timeout ?? TimeSpan.FromSeconds(3);
        var found = new Dictionary<string, AlpacaServer>(); // key: "host:port"

        using var udp = new UdpClient();
        udp.EnableBroadcast = true;
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));

        var bytes = Encoding.ASCII.GetBytes(DiscoveryMessage);

        // Send the discovery probe on EVERY usable target, not just
        // 255.255.255.255. The limited broadcast goes out the primary
        // interface only, which on Windows means:
        //   - it reaches LAN-bound Alpaca servers on the same subnet
        //   - it does NOT reach loopback, so an ASCOM Remote Server
        //     running on the SAME machine (very common dev setup) is
        //     invisible to discovery.
        // Fix: enumerate operational IPv4 interfaces + send the
        // directed broadcast to each one's subnet, plus an explicit
        // unicast to 127.0.0.1 so the local Alpaca server gets it
        // even when no real NIC is up (laptop in airplane mode, etc).
        var targets = BuildBroadcastTargets();
        int sent = 0;
        foreach (var ep in targets) {
            try {
                await udp.SendAsync(bytes, ep);
                sent++;
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Alpaca discovery probe to {Ep} failed", ep);
            }
        }
        if (sent == 0) {
            _logger.LogWarning("Alpaca discovery: every broadcast target failed (firewall?)");
            return found.Values.ToList();
        }
        _logger.LogDebug("Alpaca discovery probe sent to {N} target(s)", sent);

        using var cts = new CancellationTokenSource(to);
        try {
            while (!cts.IsCancellationRequested) {
                var result = await udp.ReceiveAsync(cts.Token);
                try {
                    var json = Encoding.ASCII.GetString(result.Buffer);
                    using var doc = JsonDocument.Parse(json);
                    if (doc.RootElement.TryGetProperty("AlpacaPort", out var portEl)) {
                        var host = result.RemoteEndPoint.Address.ToString();
                        var port = portEl.GetInt32();
                        var key = $"{host}:{port}";
                        if (!found.ContainsKey(key)) {
                            found[key] = new AlpacaServer { Host = host, Port = port };
                        }
                    }
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "Ignoring malformed Alpaca discovery reply");
                }
            }
        } catch (OperationCanceledException) { /* expected */ }

        // Enrich with /management/v1/configureddevices for each found server
        foreach (var server in found.Values) {
            try {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
                var url = $"http://{server.Host}:{server.Port}/management/v1/configureddevices";
                var resp = await http.GetFromJsonAsync<AlpacaResponse<List<AlpacaConfiguredDevice>>>(url);
                if (resp?.Value != null) {
                    server.Devices = resp.Value;
                }
                // Also fetch description
                var descUrl = $"http://{server.Host}:{server.Port}/management/v1/description";
                var desc = await http.GetFromJsonAsync<AlpacaResponse<AlpacaServerDescription>>(descUrl);
                if (desc?.Value != null) {
                    server.ServerName = desc.Value.ServerName;
                    server.Manufacturer = desc.Value.Manufacturer;
                    server.ManufacturerVersion = desc.Value.ManufacturerVersion;
                }
            } catch (Exception ex) {
                _logger.LogDebug(ex, "Failed to query {Host}:{Port} for devices", server.Host, server.Port);
            }
        }

        _logger.LogInformation("Alpaca discovery found {N} server(s)", found.Count);
        return found.Values.ToList();
    }

    /// <summary>Enumerate every IPv4 endpoint we should send the
    /// discovery probe to. Always includes:
    ///   - 255.255.255.255 (limited broadcast — main LAN sweep)
    ///   - 127.0.0.1 unicast (local-machine Alpaca server, since
    ///     limited broadcast on Windows doesn't deliver to loopback)
    /// Plus one directed broadcast per up + IPv4 interface
    ///   (e.g. 192.168.1.255 for a 192.168.1.0/24 NIC). This catches
    ///   Alpaca servers on the same /24 even when the host has
    ///   multiple NICs and the primary one isn't the right path.
    /// Loopback as a sender doesn't help — we already cover it with
    /// the explicit 127.0.0.1 entry.</summary>
    private static List<IPEndPoint> BuildBroadcastTargets() {
        var targets = new List<IPEndPoint> {
            new(IPAddress.Broadcast, DiscoveryPort),
            new(IPAddress.Loopback, DiscoveryPort)
        };
        try {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces()) {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                var props = nic.GetIPProperties();
                foreach (var ua in props.UnicastAddresses) {
                    if (ua.Address.AddressFamily != AddressFamily.InterNetwork) continue;
                    if (ua.IPv4Mask == null) continue;
                    // Compute directed broadcast: (addr | ~mask).
                    var addrBytes = ua.Address.GetAddressBytes();
                    var maskBytes = ua.IPv4Mask.GetAddressBytes();
                    var bcast = new byte[4];
                    for (int i = 0; i < 4; i++) bcast[i] = (byte)(addrBytes[i] | ~maskBytes[i]);
                    var bcastIp = new IPAddress(bcast);
                    // Skip duplicates of 255.255.255.255 (a /0 mask).
                    if (bcastIp.Equals(IPAddress.Broadcast)) continue;
                    targets.Add(new IPEndPoint(bcastIp, DiscoveryPort));
                }
            }
        } catch {
            // Iface enumeration can fail on locked-down systems; we
            // still have the 2 baseline targets above.
        }
        return targets;
    }
}

public class AlpacaServer {
    public string Host { get; set; } = "";
    public int Port { get; set; }
    public string? ServerName { get; set; }
    public string? Manufacturer { get; set; }
    public string? ManufacturerVersion { get; set; }
    public List<AlpacaConfiguredDevice> Devices { get; set; } = new();
}

public class AlpacaConfiguredDevice {
    [JsonPropertyName("DeviceName")]
    public string DeviceName { get; set; } = "";

    [JsonPropertyName("DeviceType")]
    public string DeviceType { get; set; } = "";

    [JsonPropertyName("DeviceNumber")]
    public int DeviceNumber { get; set; }

    [JsonPropertyName("UniqueID")]
    public string UniqueID { get; set; } = "";
}

public class AlpacaServerDescription {
    [JsonPropertyName("ServerName")]
    public string ServerName { get; set; } = "";

    [JsonPropertyName("Manufacturer")]
    public string Manufacturer { get; set; } = "";

    [JsonPropertyName("ManufacturerVersion")]
    public string ManufacturerVersion { get; set; } = "";

    [JsonPropertyName("Location")]
    public string Location { get; set; } = "";
}

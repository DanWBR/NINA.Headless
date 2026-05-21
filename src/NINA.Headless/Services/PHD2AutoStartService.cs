namespace NINA.Headless.Services;

/// <summary>
/// Hosted service that — when <c>PHD2:AutoStart=true</c> — launches PHD2 and
/// connects the relay client to it as soon as the Headless app starts. Runs
/// the launch in the background so it never blocks app startup.
///
/// Behaviour:
///   - If PHD2 is already running on the configured host/port, just connects.
///   - If PHD2 isn't installed (no executable path resolved), logs a warning
///     and exits quietly so the rest of the app keeps working.
///   - Retries the connect a few times after launch in case PHD2's event
///     server takes a while to come up.
/// </summary>
public class PHD2AutoStartService : IHostedService {
    private readonly IConfiguration _config;
    private readonly PHD2ProcessManager _pm;
    private readonly PHD2Client _client;
    private readonly ProfileService _profiles;
    private readonly ILogger<PHD2AutoStartService> _logger;
    private Task? _runner;
    private CancellationTokenSource? _cts;

    public PHD2AutoStartService(IConfiguration config, PHD2ProcessManager pm, PHD2Client client,
        ProfileService profiles, ILogger<PHD2AutoStartService> logger) {
        _config = config;
        _pm = pm;
        _client = client;
        _profiles = profiles;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken) {
        // Profile (UI-controlled) wins over appsettings.json. Either source
        // can enable auto-start; the UI toggle is the primary path for
        // ordinary users, the env-var / config path exists for headless
        // installs that don't want to load the UI to flip a switch.
        var enabled = _profiles.Active.PHD2AutoStart
                   || _config.GetValue("PHD2:AutoStart", false);
        if (!enabled) {
            _logger.LogDebug("PHD2 auto-start disabled (toggle in Guider settings or set PHD2:AutoStart=true)");
            return Task.CompletedTask;
        }
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _runner = Task.Run(() => RunAsync(_cts.Token));
        return Task.CompletedTask;
    }

    private async Task RunAsync(CancellationToken ct) {
        try {
            // Stagger startup a touch so we don't compete with the rest of the
            // hosted services for CPU/disk during cold start
            await Task.Delay(TimeSpan.FromSeconds(2), ct);

            var alreadyRunning = await _pm.IsRunningAsync();
            if (alreadyRunning) {
                _logger.LogInformation("PHD2 already running on {Host}:{Port} — connecting", _pm.DefaultHost, _pm.DefaultPort);
            } else {
                if (!_pm.ExecutableConfigured) {
                    _logger.LogWarning("PHD2:AutoStart=true but no PHD2 executable found. Looked for: {Paths}",
                        string.Join(", ", PHD2ProcessManager.EnumerateCandidatePaths()));
                    return;
                }
                _logger.LogInformation("Auto-starting PHD2 from {Path}", _pm.ExecutablePath);
                var ok = await _pm.LaunchAsync(ct);
                if (!ok) {
                    _logger.LogWarning("PHD2 auto-launch did not produce a listening event server");
                    return;
                }
            }

            // Connect the JSON-RPC client. Retry a handful of times — PHD2's
            // event server sometimes accepts TCP before it's ready to RPC.
            for (var attempt = 1; attempt <= 5 && !ct.IsCancellationRequested; attempt++) {
                try {
                    if (_client.IsConnected) {
                        _logger.LogDebug("PHD2 client already connected");
                        return;
                    }
                    await _client.ConnectAsync(_pm.DefaultHost, _pm.DefaultPort);
                    _logger.LogInformation("PHD2 auto-start complete (connected, state={State})", _client.AppState);
                    return;
                } catch (Exception ex) {
                    _logger.LogDebug(ex, "PHD2 connect attempt {N}/5 failed", attempt);
                    await Task.Delay(TimeSpan.FromSeconds(2), ct);
                }
            }
            _logger.LogWarning("PHD2 launched but JSON-RPC client could not connect after 5 attempts");
        } catch (OperationCanceledException) { /* shutdown */ }
          catch (Exception ex) {
            _logger.LogError(ex, "PHD2 auto-start crashed");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken) {
        _cts?.Cancel();
        if (_runner != null) {
            try { await _runner.WaitAsync(TimeSpan.FromSeconds(2), cancellationToken); } catch { }
        }
    }
}

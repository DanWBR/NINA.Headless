using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket;

public static class StatusStreamHandler {
    private static readonly TimeSpan StatusInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);
    private static readonly JsonSerializerOptions JsonOpts = new() {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        var equip = context.RequestServices.GetRequiredService<EquipmentManager>();
        var cameraStream = context.RequestServices.GetRequiredService<CameraStreamService>();
        var videoRecording = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Planetary.VideoRecordingService>();
        var videoStacker = context.RequestServices.GetRequiredService<NINA.Polaris.Services.Planetary.PlanetaryStackerService>();
        var slewPreview = context.RequestServices.GetRequiredService<SlewPreviewService>();
        var liveStackTriggers = context.RequestServices.GetRequiredService<LiveStackTriggersService>();
        var refocusSuggest = context.RequestServices.GetRequiredService<RefocusSuggestionService>();
        var flatWizard = context.RequestServices.GetRequiredService<FlatWizardService>();
        var liveStack = context.RequestServices.GetRequiredService<LiveStackingService>();
        var sequence = context.RequestServices.GetRequiredService<SequenceEngine>();
        var phd2 = context.RequestServices.GetRequiredService<PHD2Client>();
        var profileSync = context.RequestServices.GetRequiredService<PHD2ProfileSyncService>();
        var phd2Calibration = context.RequestServices.GetRequiredService<PHD2CalibrationOrchestrator>();
        var phd2Gui = context.RequestServices.GetRequiredService<Phd2GuiSessionService>();
        var phd2Vnc = context.RequestServices.GetRequiredService<Phd2VncSessionService>();
        var autoFocus = context.RequestServices.GetRequiredService<AutoFocusService>();
        var meridianFlip = context.RequestServices.GetRequiredService<MeridianFlipService>();
        var profile = context.RequestServices.GetRequiredService<ProfileService>();
        var hostMetrics = context.RequestServices.GetRequiredService<HostMetricsService>();
        var clockSync = context.RequestServices.GetRequiredService<ClockSyncService>();
        var siril = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.External.SirilService>();
        var graxpert = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.External.GraXpertService>();
        var simulator = context.RequestServices
            .GetRequiredService<NINA.Polaris.Services.Simulator.SimulatorService>();
        var network = context.RequestServices.GetRequiredService<NetworkManagerService>();
        var notifications = context.RequestServices.GetRequiredService<NotificationService>();
        var polarAlign = context.RequestServices.GetRequiredService<PolarAlignmentService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = PingInterval
        });

        using var cts = new CancellationTokenSource();

        try {
            await SendJsonAsync(ws, new { type = "connected", stream = "status" }, cts.Token);
        } catch {
            return;
        }

        var sendTask = Task.Run(async () => {
            while (!cts.Token.IsCancellationRequested && ws.State == WebSocketState.Open) {
                try {
                    var seqStatus = sequence.GetStatus();

                    // Compact summaries of PH2X-3/4/6 services, surface
                    // as sub-objects on the guider block so UI can read
                    // sync/calibrate/embed status without polling endpoints.
                    var profileSyncPayload = new {
                        phase = profileSync.CurrentStatus.Phase,
                        rigId = profileSync.CurrentStatus.RigId,
                        rigName = profileSync.CurrentStatus.RigName,
                        profileId = profileSync.CurrentStatus.ProfileId,
                        profileMissing = profileSync.CurrentStatus.ProfileMissing,
                        error = profileSync.CurrentStatus.Error,
                        at = profileSync.CurrentStatus.At
                    };
                    var calibrateJobPayload = phd2Calibration.CurrentJob == null ? null : new {
                        id = phd2Calibration.CurrentJob.Id,
                        phase = phd2Calibration.CurrentJob.State.ToString(),
                        stepMs = phd2Calibration.CurrentJob.CalibrationStepMs,
                        pixelScale = phd2Calibration.CurrentJob.PixelScale,
                        error = phd2Calibration.CurrentJob.Error,
                        warnings = phd2Calibration.CurrentJob.Warnings,
                        done = phd2Calibration.CurrentJob.State == CalibrationPhase.Ok
                            || phd2Calibration.CurrentJob.State == CalibrationPhase.Fail
                    };
                    var guiSessionPayload = new {
                        supportedOs = phd2Gui.IsSupportedOs,
                        supportedArch = phd2Gui.IsSupportedArch,
                        unsupportedReason = phd2Gui.UnsupportedReason,
                        xpraInstalled = phd2Gui.XpraInstalled,
                        xpraVersion = phd2Gui.XpraVersion,
                        running = phd2Gui.SessionRunning,
                        port = phd2Gui.BindPort,
                        lastError = phd2Gui.LastError
                    };
                    // PH2VNC-4: Windows-side embed status. Mirrors
                    // guiSession's shape so the UI tab can switch
                    // backends by OS without divergent state code.
                    var vncSessionPayload = new {
                        supportedOs = phd2Vnc.IsSupportedOs,
                        unsupportedReason = phd2Vnc.UnsupportedReason,
                        tightVncInstalled = phd2Vnc.TightVncInstalled,
                        tightVncVersion = phd2Vnc.TightVncVersion,
                        serviceInstalled = phd2Vnc.ServiceInstalled,
                        serviceRunning = phd2Vnc.ServiceRunning,
                        listening = phd2Vnc.Listening,
                        port = phd2Vnc.Port,
                        lastError = phd2Vnc.LastError
                    };

                    // Compact guider payload: last 60 samples for inline chart
                    object? guiderPayload = null;
                    if (phd2.IsConnected) {
                        var steps = phd2.SnapshotSteps();
                        var tail = steps.Skip(Math.Max(0, steps.Count - 60));
                        guiderPayload = new {
                            connected = true,
                            host = phd2.Host,
                            port = phd2.Port,
                            appState = phd2.AppState,
                            guiding = phd2.IsGuiding,
                            calibrating = phd2.IsCalibrating,
                            paused = phd2.IsPaused,
                            looping = phd2.IsLooping,
                            settling = phd2.IsSettling,
                            pixelScale = phd2.PixelScale,
                            rmsRA = phd2.RmsRA,
                            rmsDec = phd2.RmsDec,
                            rmsTotal = phd2.RmsTotal,
                            peakRA = phd2.PeakRA,
                            peakDec = phd2.PeakDec,
                            stepCount = steps.Count,
                            lastAlert = phd2.LastAlert,
                            lastSettleStatus = phd2.LastSettleStatus,
                            recentSteps = tail.Select(s => new {
                                t = ((DateTimeOffset)s.Timestamp).ToUnixTimeMilliseconds(),
                                ra = s.RaArcsec,
                                dec = s.DecArcsec
                            }),
                            profileSync = profileSyncPayload,
                            calibrateJob = calibrateJobPayload,
                            guiSession = guiSessionPayload,
                            vncSession = vncSessionPayload
                        };
                    } else {
                        guiderPayload = new {
                            connected = false, appState = "Stopped",
                            profileSync = profileSyncPayload,
                            calibrateJob = calibrateJobPayload,
                            guiSession = guiSessionPayload,
                            vncSession = vncSessionPayload
                        };
                    }

                    // Meridian flip live status (LST + time-to-meridian for the current mount RA)
                    double? lstHours = null, hourAngleHours = null, timeToMeridianHours = null;
                    if (equip.Telescope != null && equip.Telescope.IsConnected) {
                        var raHours = equip.Telescope.RightAscension;
                        if (!double.IsNaN(raHours)) {
                            lstHours = MeridianFlipService.ComputeLstHours(DateTime.UtcNow, profile.Active.Longitude);
                            var ha = lstHours.Value - raHours;
                            while (ha > 12) ha -= 24;
                            while (ha < -12) ha += 24;
                            hourAngleHours = ha;
                            timeToMeridianHours = MeridianFlipService.HoursUntilMeridian(
                                raHours, DateTime.UtcNow, profile.Active.Longitude);
                        }
                    }

                    var meridianPayload = new {
                        state = meridianFlip.State.ToString().ToLowerInvariant(),
                        settings = meridianFlip.Settings,
                        flipsCompleted = meridianFlip.FlipsCompleted,
                        lastFlipAt = meridianFlip.LastFlipAt,
                        lastFlipError = meridianFlip.LastFlipError,
                        lstHours,
                        hourAngleHours,
                        timeToMeridianHours,
                        timeToMeridianMinutes = timeToMeridianHours * 60
                    };

                    var autoFocusPayload = new {
                        state = autoFocus.State.ToString().ToLowerInvariant(),
                        currentSampleIndex = autoFocus.Progress.CurrentSampleIndex,
                        steps = autoFocus.Progress.Steps,
                        lastHfr = autoFocus.Progress.LastHfr,
                        lastStarCount = autoFocus.Progress.LastStarCount,
                        points = autoFocus.Progress.Points,
                        bestPosition = autoFocus.LastResult?.BestPosition,
                        bestHfr = autoFocus.LastResult?.BestPredictedHfr,
                        success = autoFocus.LastResult?.Success
                    };

                    // Compact summaries for the activity bar. Full job
                    // detail (lights paths, results, etc) lives on the
                    // per-tool endpoints, only the surface needed for
                    // chips makes it into the broadcast.
                    var sirilJobsPayload = siril.ActiveJobs.Select(j => new {
                        j.JobId, j.ScriptName, j.TargetName, j.Stage, j.PercentDone
                    }).ToList();
                    var graXpertJobsPayload = graxpert.ActiveJobs.Select(j => new {
                        j.JobId,
                        operation = j.Operation.ToString(),
                        j.Done, j.Total, j.Failed
                    }).ToList();

                    var status = new {
                        type = "status",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        equipment = equip.GetEquipmentStatus(),
                        // Stack status + triggers (LSTR-4). Triggers sub-object
                        // carries last-action timestamps + reference RA/Dec +
                        // executing flag so the UI banner + status lines can
                        // render without a separate poll.
                        liveStack = new {
                            isRunning = liveStack.GetStatus().IsRunning,
                            frameCount = liveStack.GetStatus().FrameCount,
                            width = liveStack.GetStatus().Width,
                            height = liveStack.GetStatus().Height,
                            referenceStarCount = liveStack.GetStatus().ReferenceStarCount,
                            lastFrameHfr = liveStack.LastFrameMedianHfr,
                            lastFrameStarCount = liveStack.LastFrameStarCount,
                            // CLST-1/CLST-4: "full" (server-side accumulator) or
                            // "metricsonly" (client owns the accumulator via WASM).
                            // The client only routes raw frames through its WASM
                            // stacker when this is "metricsonly", otherwise the
                            // raw frames the server relays ARE the accumulated
                            // stack and re-stacking would compound.
                            mode = liveStack.GetStatus().Mode,
                            // Per-frame-to-disk toggle + count of frames
                            // actually written this session. Drives the
                            // LIVE tab checkbox state + the "(N saved)"
                            // counter rendered next to it.
                            saveFramesToDisk = liveStack.SaveFramesToDisk,
                            framesSavedToDisk = liveStack.FramesSavedToDisk,
                            // Continuous-stack + duration cap. UI uses
                            // these to render the elapsed counter, the
                            // "stack complete" badge once the cap fires,
                            // and the max-duration input value.
                            maxDurationSeconds = liveStack.MaxDurationSeconds,
                            startedAt = liveStack.StartedAt,
                            elapsedSeconds = liveStack.ElapsedSeconds,
                            durationCapReached = liveStack.DurationCapReached,
                            triggers = liveStackTriggers.CurrentStatus,
                            // REFSUG-1: trend-based advisory. Always
                            // emitted so the UI can decide whether to
                            // render the chip / callout without polling.
                            refocusSuggestion = refocusSuggest.CurrentStatus
                        },
                        guider = guiderPayload,
                        autoFocus = autoFocusPayload,
                        meridianFlip = meridianPayload,
                        sequence = new {
                            state = seqStatus.State,
                            currentItemIndex = seqStatus.CurrentItemIndex,
                            currentFrameInItem = seqStatus.CurrentFrameInItem,
                            totalFrames = seqStatus.TotalFrames,
                            totalFramesCompleted = seqStatus.TotalFramesCompleted,
                            elapsedSeconds = seqStatus.ElapsedSeconds,
                            estimatedRemainingSeconds = seqStatus.EstimatedRemainingSeconds,
                            lastError = seqStatus.LastError,
                            items = seqStatus.Items,
                            dithersIssued = seqStatus.DithersIssued,
                            framesSinceDither = seqStatus.FramesSinceDither,
                            dither = seqStatus.Dither
                        },
                        // Camera video-stream lifecycle (PREVIEW tab Stream button).
                        // Always present so the UI button can read mode/fps even
                        // while idle.
                        cameraStream = new {
                            running = cameraStream.IsRunning,
                            mode = cameraStream.Mode,
                            exposure = cameraStream.ExposureSeconds,
                            gain = cameraStream.Gain,
                            frames = cameraStream.FrameCount,
                            fps = cameraStream.Fps,
                            lastError = cameraStream.LastError,
                            supportsNative = equip.Camera?.Capabilities.SupportsVideoStream ?? false
                        },
                        // Planetary recording lifecycle (VIDEO tab Capture).
                        videoRecording = new {
                            recording = videoRecording.IsRecording,
                            path = videoRecording.OutputPath,
                            frames = videoRecording.FrameCount,
                            bytes = videoRecording.BytesWritten,
                            durationSec = videoRecording.Duration.TotalSeconds,
                            droppedFrames = videoRecording.DroppedFrames,
                            lastError = videoRecording.LastError
                        },
                        // Planetary stack job (VIDEO tab Process). Null when idle.
                        videoStack = videoStacker.CurrentJob == null ? null : new {
                            id = videoStacker.CurrentJob.Id,
                            phase = videoStacker.CurrentJob.Phase.ToString(),
                            totalFrames = videoStacker.CurrentJob.TotalFrames,
                            framesAnalyzed = videoStacker.CurrentJob.FramesAnalyzed,
                            framesPicked = videoStacker.CurrentJob.FramesPicked,
                            framesAligned = videoStacker.CurrentJob.FramesAligned,
                            framesStacked = videoStacker.CurrentJob.FramesStacked,
                            outputPath = videoStacker.CurrentJob.OutputPath,
                            error = videoStacker.CurrentJob.Error,
                            done = videoStacker.CurrentJob.Phase
                                is NINA.Polaris.Services.Planetary.StackPhase.Ok
                                or NINA.Polaris.Services.Planetary.StackPhase.Fail
                        },
                        // FW-1: Flat Wizard state (AUTORUN → Flat Wizard
                        // sub-tab). Always emitted so the UI shutter +
                        // progress section can render "idle" without
                        // a separate poll. progress is null when the
                        // wizard never ran; populated by the service
                        // while running and after the last filter
                        // completes (FilterResults accumulates).
                        flatWizard = new {
                            state = flatWizard.State.ToString().ToLowerInvariant(),
                            lastError = flatWizard.LastError,
                            progress = flatWizard.State == FlatWizardState.Idle
                                       && flatWizard.Progress.TotalFilters == 0
                                ? null
                                : (object)new {
                                    startedAt = flatWizard.Progress.StartedAt,
                                    totalFilters = flatWizard.Progress.TotalFilters,
                                    currentFilterIndex = flatWizard.Progress.CurrentFilterIndex,
                                    currentFilter = flatWizard.Progress.CurrentFilter,
                                    phase = flatWizard.Progress.Phase,
                                    searchAttempt = flatWizard.Progress.SearchAttempt,
                                    currentExposure = flatWizard.Progress.CurrentExposure,
                                    lastMedian = flatWizard.Progress.LastMedian,
                                    totalFramesPerFilter = flatWizard.Progress.TotalFramesPerFilter,
                                    framesCaptured = flatWizard.Progress.FramesCaptured,
                                    filterResults = flatWizard.Progress.FilterResults
                                }
                        },
                        // Auto-slew-preview state (SKY tab inset card).
                        slewPreview = new {
                            enabled = slewPreview.Enabled,
                            active = slewPreview.IsPreviewActive,
                            slewing = slewPreview.LastDecision_Slewing,
                            captureIdle = slewPreview.LastDecision_CaptureIdle,
                            lastCheckedAt = slewPreview.LastCheckedAt,
                            lastError = slewPreview.LastError
                        },
                        // New blocks powering the bottom activity bar.
                        host = hostMetrics.Latest,
                        sirilJobs = sirilJobsPayload,
                        graXpertJobs = graXpertJobsPayload,
                        // CLOCK-1: serverUtcNow lets the client compute
                        // wall-clock skew against its own Date.now()
                        // every tick. When |skew| > 30s the activity
                        // bar shows a "Clock N off" chip and the
                        // Settings card surfaces a Sync button.
                        server = new {
                            utcNow = DateTime.UtcNow.ToString("o"),
                            clockSyncSupported = clockSync.IsSupported
                        },
                        // Server-pushed toasts (auto-connect outcomes,
                        // simulator events, etc.). Client de-dups by id
                        //, see toast pump in app.js.
                        notifications = notifications.Snapshot(),
                        // SIM-4: built-in equipment simulator status
                        // (which backend is active, is it installed,
                        // is the stack running, which devices). UI
                        // shows a green/amber chip + the Settings
                        // panel binds to these fields.
                        simulator = simulator.GetStatus(),
                        // WIFI-3: host WiFi state (mode + ssid + ip +
                        // signal). 501-class platforms (Windows /
                        // macOS / no nmcli / no wifi iface) still send
                        // the block, the supportedOs/nmcliInstalled/
                        // hasWifi flags + unsupportedReason tell the
                        // UI which banner to show.
                        network = new {
                            supportedOs       = network.IsSupportedOs,
                            nmcliInstalled    = network.NmcliInstalled,
                            hasWifi           = network.HasWifiInterface,
                            wifiInterface     = network.WifiInterface,
                            mode              = network.CurrentMode.ToString().ToLowerInvariant(),
                            ssid              = network.CurrentSsid,
                            ip                = network.CurrentIp,
                            signal            = network.SignalStrength,
                            hotspotSsid       = network.HotspotSsid,
                            lastError         = network.LastError,
                            unsupportedReason = network.UnsupportedReason,
                            lastRefreshAt     = network.LastRefreshAt
                        },
                        // PA-4: TPPA orchestrator state. CurrentJob is
                        // null until the user clicks Start; serialise a
                        // null-shaped object so the front-end can bind
                        // without null checks.
                        polarAlignment = polarAlign.CurrentJob == null ? null : new {
                            jobId = polarAlign.CurrentJob.Id,
                            phase = polarAlign.CurrentJob.Phase.ToString(),
                            mode = polarAlign.CurrentJob.Mode,
                            isActive = polarAlign.CurrentJob.IsActive,
                            points = polarAlign.CurrentJob.Points,
                            azErrorArcsec = polarAlign.CurrentJob.AzErrorArcsec,
                            altErrorArcsec = polarAlign.CurrentJob.AltErrorArcsec,
                            totalErrorArcsec = polarAlign.CurrentJob.TotalErrorArcsec,
                            lastError = polarAlign.CurrentJob.LastError,
                            startedAt = polarAlign.CurrentJob.StartedAt,
                            completedAt = polarAlign.CurrentJob.CompletedAt
                        }
                    };

                    await SendJsonAsync(ws, status, cts.Token);
                    await Task.Delay(StatusInterval, cts.Token);
                } catch (OperationCanceledException) {
                    break;
                } catch (WebSocketException) {
                    break;
                } catch (Exception ex) {
                    logger.LogWarning(ex, "Status stream send error");
                    break;
                }
            }
        }, cts.Token);

        try {
            var buffer = new byte[256];
            while (ws.State == WebSocketState.Open) {
                using var recvCts = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
                recvCts.CancelAfter(PingInterval * 3);

                var result = await ws.ReceiveAsync(buffer, recvCts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;
            }
        } catch (OperationCanceledException) {
            logger.LogDebug("Status WebSocket receive timed out (client likely disconnected)");
        } catch (WebSocketException) {
            // Client disconnected abruptly
        }

        cts.Cancel();
        try { await sendTask; } catch { }

        await CloseGracefully(ws);
    }

    private static async Task SendJsonAsync(System.Net.WebSockets.WebSocket ws, object data, CancellationToken ct) {
        var json = JsonSerializer.Serialize(data, JsonOpts);
        using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        sendCts.CancelAfter(SendTimeout);
        await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, sendCts.Token);
    }

    private static async Task CloseGracefully(System.Net.WebSockets.WebSocket ws) {
        if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived) {
            try {
                using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "Closing", closeCts.Token);
            } catch { }
        }
    }
}

using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NINA.Headless.Services;

namespace NINA.Headless.WebSocket;

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
        var videoRecording = context.RequestServices.GetRequiredService<NINA.Headless.Services.Planetary.VideoRecordingService>();
        var videoStacker = context.RequestServices.GetRequiredService<NINA.Headless.Services.Planetary.PlanetaryStackerService>();
        var slewPreview = context.RequestServices.GetRequiredService<SlewPreviewService>();
        var liveStackTriggers = context.RequestServices.GetRequiredService<LiveStackTriggersService>();
        var liveStack = context.RequestServices.GetRequiredService<LiveStackingService>();
        var sequence = context.RequestServices.GetRequiredService<SequenceEngine>();
        var phd2 = context.RequestServices.GetRequiredService<PHD2Client>();
        var profileSync = context.RequestServices.GetRequiredService<PHD2ProfileSyncService>();
        var phd2Calibration = context.RequestServices.GetRequiredService<PHD2CalibrationOrchestrator>();
        var phd2Gui = context.RequestServices.GetRequiredService<Phd2GuiSessionService>();
        var autoFocus = context.RequestServices.GetRequiredService<AutoFocusService>();
        var meridianFlip = context.RequestServices.GetRequiredService<MeridianFlipService>();
        var profile = context.RequestServices.GetRequiredService<ProfileService>();
        var hostMetrics = context.RequestServices.GetRequiredService<HostMetricsService>();
        var siril = context.RequestServices
            .GetRequiredService<NINA.Headless.Services.External.SirilService>();
        var graxpert = context.RequestServices
            .GetRequiredService<NINA.Headless.Services.External.GraXpertService>();
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

                    // Compact summaries of PH2X-3/4/6 services — surface
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
                        xpraInstalled = phd2Gui.XpraInstalled,
                        xpraVersion = phd2Gui.XpraVersion,
                        running = phd2Gui.SessionRunning,
                        port = phd2Gui.BindPort,
                        lastError = phd2Gui.LastError
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
                            guiSession = guiSessionPayload
                        };
                    } else {
                        guiderPayload = new {
                            connected = false, appState = "Stopped",
                            profileSync = profileSyncPayload,
                            calibrateJob = calibrateJobPayload,
                            guiSession = guiSessionPayload
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
                    // per-tool endpoints — only the surface needed for
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
                            triggers = liveStackTriggers.CurrentStatus
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
                                is NINA.Headless.Services.Planetary.StackPhase.Ok
                                or NINA.Headless.Services.Planetary.StackPhase.Fail
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
                        graXpertJobs = graXpertJobsPayload
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

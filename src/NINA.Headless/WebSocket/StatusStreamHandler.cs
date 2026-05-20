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
        var liveStack = context.RequestServices.GetRequiredService<LiveStackingService>();
        var sequence = context.RequestServices.GetRequiredService<SequenceEngine>();
        var phd2 = context.RequestServices.GetRequiredService<PHD2Client>();
        var autoFocus = context.RequestServices.GetRequiredService<AutoFocusService>();
        var meridianFlip = context.RequestServices.GetRequiredService<MeridianFlipService>();
        var profile = context.RequestServices.GetRequiredService<ProfileService>();
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
                            })
                        };
                    } else {
                        guiderPayload = new { connected = false, appState = "Stopped" };
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

                    var status = new {
                        type = "status",
                        timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                        equipment = equip.GetEquipmentStatus(),
                        liveStack = liveStack.GetStatus(),
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

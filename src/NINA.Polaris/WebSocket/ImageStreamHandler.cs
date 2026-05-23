using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using NINA.Polaris.Services;

namespace NINA.Polaris.WebSocket;

public static class ImageStreamHandler {
    private static readonly TimeSpan PingInterval = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(5);

    public static async Task Handle(HttpContext context) {
        if (!context.WebSockets.IsWebSocketRequest) {
            context.Response.StatusCode = 400;
            return;
        }

        var relay = context.RequestServices.GetRequiredService<ImageRelayService>();
        var logger = context.RequestServices.GetRequiredService<ILogger<Program>>();

        using var ws = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext {
            KeepAliveInterval = PingInterval
        });

        var clientId = Guid.NewGuid().ToString("N");
        relay.RegisterClient(clientId, ws);

        try {
            // Send welcome message with supported modes
            using var welcomeCts = new CancellationTokenSource(SendTimeout);
            var welcome = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
                type = "connected",
                stream = "image",
                clientId,
                modes = new[] { "jpeg", "raw" },
                defaultMode = "jpeg"
            }));
            await ws.SendAsync(welcome, WebSocketMessageType.Text, true, welcomeCts.Token);

            var buffer = new byte[1024];
            while (ws.State == WebSocketState.Open) {
                try {
                    using var recvCts = new CancellationTokenSource(PingInterval * 3);
                    var result = await ws.ReceiveAsync(buffer, recvCts.Token);

                    if (result.MessageType == WebSocketMessageType.Close)
                        break;

                    // Handle text messages for mode switching
                    if (result.MessageType == WebSocketMessageType.Text && result.Count > 0) {
                        var text = Encoding.UTF8.GetString(buffer, 0, result.Count);
                        HandleClientMessage(relay, clientId, text, logger);
                    }
                } catch (OperationCanceledException) {
                    logger.LogDebug("Image stream client {Id} timed out", clientId);
                    break;
                } catch (WebSocketException) {
                    break;
                }
            }
        } finally {
            relay.UnregisterClient(clientId);
            await CloseGracefully(ws);
        }
    }

    private static void HandleClientMessage(ImageRelayService relay, string clientId, string text, ILogger logger) {
        try {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;

            if (root.TryGetProperty("mode", out var modeProp)) {
                var mode = modeProp.GetString()?.ToLowerInvariant() switch {
                    "raw" => ImageRelayService.StreamMode.Raw,
                    _ => ImageRelayService.StreamMode.Jpeg
                };
                relay.SetClientMode(clientId, mode);
                logger.LogInformation("Client {Id} requested {Mode} mode", clientId, mode);
                return;
            }

            // Discriminated messages — capability handshake + per-
            // frame metrics from the client-side WASM stacker. CLST-5.
            if (root.TryGetProperty("type", out var typeProp)) {
                var type = typeProp.GetString();
                switch (type) {
                    case "client-capability":
                        var hasWasm = root.TryGetProperty("wasm", out var w) && w.ValueKind == JsonValueKind.True;
                        string? ver = null;
                        if (root.TryGetProperty("wasmVersion", out var verProp) && verProp.ValueKind == JsonValueKind.String) {
                            ver = verProp.GetString();
                        }
                        relay.SetClientCapability(clientId, hasWasm, ver);
                        break;

                    case "client-stack-progress":
                        // Logged at debug for now — the trigger
                        // orchestrator still uses the server-side
                        // StarDetector's metrics (which run in
                        // MetricsOnly mode too) so we don't need to
                        // wire these through. Useful for diagnosing
                        // server-vs-client output divergence.
                        if (root.TryGetProperty("frameCount", out var fc)
                            && root.TryGetProperty("hfr", out var hfr)
                            && root.TryGetProperty("starCount", out var sc)) {
                            logger.LogDebug("Client {Id} integrated frame {N}: HFR={Hfr:F2} stars={Stars}",
                                clientId, fc.GetInt32(), hfr.GetDouble(), sc.GetInt32());
                        }
                        break;
                }
            }
        } catch (JsonException) {
            // Ignore malformed messages
        }
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

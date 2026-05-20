using NINA.Headless.Endpoints;
using NINA.Headless.Services;
using NINA.Headless.WebSocket;
using NINA.INDI.Client;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.ConfigureKestrel(options =>
{
    options.ListenAnyIP(5000);
});

// Services
builder.Services.AddSingleton<ImageRelayService>();
builder.Services.AddSingleton<LiveStackingService>();
builder.Services.AddSingleton<EquipmentManager>();
builder.Services.AddSingleton<SequenceEngine>();
builder.Services.AddSingleton<SkyCatalogService>();
builder.Services.AddSingleton<PlateSolveService>();
builder.Services.AddSingleton<SlewCenterService>();
builder.Services.AddSingleton<ProfileService>();
builder.Services.AddSingleton<PHD2Client>();
builder.Services.AddSingleton(sp =>
{
    var config = sp.GetRequiredService<IConfiguration>();
    var host = config.GetValue("Indi:Host", "localhost")!;
    var port = config.GetValue("Indi:Port", 7624);
    return new IndiClient(host, port);
});

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.UseWebSockets();

// Equipment endpoints
app.MapEquipmentEndpoints();
app.MapCameraEndpoints();
app.MapTelescopeEndpoints();
app.MapFocuserEndpoints();
app.MapFilterWheelEndpoints();
app.MapRotatorEndpoints();
app.MapFlatDeviceEndpoints();
app.MapDomeEndpoints();
app.MapWeatherEndpoints();
app.MapGuiderEndpoints();
app.MapSequenceEndpoints();
app.MapSkyEndpoints();
app.MapSystemEndpoints();
app.MapImageEndpoints();

// Live stacking + INDI
app.MapLiveStackEndpoints();
app.MapIndiEndpoints();

// WebSocket streams
app.Map("/ws/image-stream", ImageStreamHandler.Handle);
app.Map("/ws/status", StatusStreamHandler.Handle);

app.Run();

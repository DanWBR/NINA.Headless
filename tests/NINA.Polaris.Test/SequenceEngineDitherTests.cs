using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services;
using NINA.INDI.Client;

namespace NINA.Polaris.Test;

[TestFixture]
public class SequenceEngineDitherTests {

    private SequenceEngine MakeEngine() {
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance);
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var liveStack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var autoFocus = new AutoFocusService(equip, relay, NullLogger<AutoFocusService>.Instance);
        var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var plateSolve = new PlateSolveService(emptyConfig, NullLogger<PlateSolveService>.Instance);
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        var slewCenter = new SlewCenterService(equip, plateSolve, profile, NullLogger<SlewCenterService>.Instance);
        var meridianFlip = new MeridianFlipService(equip, phd2, slewCenter, autoFocus, profile,
            NullLogger<MeridianFlipService>.Instance);
        var imageWriter = new ImageWriterService(equip, profile, NullLogger<ImageWriterService>.Instance);
        var graXpert = new NINA.Polaris.Services.External.GraXpertService(emptyConfig, profile,
            NullLogger<NINA.Polaris.Services.External.GraXpertService>.Instance);
        var flatWizard = new FlatWizardService(equip, imageWriter, profile,
            NullLogger<FlatWizardService>.Instance, emptyConfig);
        return new SequenceEngine(equip, relay, liveStack, phd2, meridianFlip, imageWriter,
            graXpert, flatWizard, NullLogger<SequenceEngine>.Instance);
    }

    [Test]
    public void DitherSettings_Defaults_AreSensible() {
        var s = new DitherSettings();
        Assert.That(s.Enabled, Is.False, "dither should default to OFF");
        Assert.That(s.Pixels, Is.EqualTo(5.0));
        Assert.That(s.EveryNFrames, Is.EqualTo(1));
        Assert.That(s.RaOnly, Is.False);
        Assert.That(s.SettlePixels, Is.EqualTo(1.5));
        Assert.That(s.SettleTime, Is.EqualTo(10));
        Assert.That(s.SettleTimeout, Is.EqualTo(40));
    }

    [Test]
    public void Engine_NewlyConstructed_HasDefaultDisabledDither() {
        var engine = MakeEngine();
        Assert.That(engine.Dither, Is.Not.Null);
        Assert.That(engine.Dither.Enabled, Is.False);
        Assert.That(engine.DithersIssued, Is.EqualTo(0));
    }

    [Test]
    public void Engine_DitherSetterReplacesConfig() {
        var engine = MakeEngine();
        engine.Dither = new DitherSettings {
            Enabled = true,
            Pixels = 8.0,
            EveryNFrames = 3,
            RaOnly = true,
            SettlePixels = 0.8,
            SettleTime = 5,
            SettleTimeout = 60
        };

        Assert.That(engine.Dither.Enabled, Is.True);
        Assert.That(engine.Dither.Pixels, Is.EqualTo(8.0));
        Assert.That(engine.Dither.EveryNFrames, Is.EqualTo(3));
        Assert.That(engine.Dither.RaOnly, Is.True);
    }

    [Test]
    public void GetStatus_IncludesDitherConfigAndCounters() {
        var engine = MakeEngine();
        engine.Dither = new DitherSettings { Enabled = true, EveryNFrames = 2 };

        var status = engine.GetStatus();

        Assert.That(status.Dither, Is.Not.Null);
        Assert.That(status.Dither!.Enabled, Is.True);
        Assert.That(status.Dither.EveryNFrames, Is.EqualTo(2));
        Assert.That(status.DithersIssued, Is.EqualTo(0));
        Assert.That(status.FramesSinceDither, Is.EqualTo(0));
    }

    // --- ImageType / autorun frame classification ---

    [Test]
    public void SequenceItem_DefaultImageType_IsLight() {
        // Catches the regression where an autorun item builds with no
        // explicit type, must default to LIGHT, not empty or null,
        // because ImageWriterService.BuildSubDir routes by uppercase
        // string match.
        var item = new SequenceItem();
        Assert.That(item.ImageType, Is.EqualTo("LIGHT"));
    }

    [Test]
    public void EndActions_Defaults_AreAllFalse() {
        // The cleanup is opt-in. A freshly-constructed engine must not
        // park the mount or warm the camera on its own.
        var engine = MakeEngine();
        Assert.That(engine.EndActions, Is.Not.Null);
        Assert.That(engine.EndActions.ParkMount, Is.False);
        Assert.That(engine.EndActions.StopTracking, Is.False);
        Assert.That(engine.EndActions.WarmCamera, Is.False);
        Assert.That(engine.EndActions.DisconnectGuider, Is.False);
        Assert.That(engine.EndActions.RunOnStop, Is.False);
        Assert.That(engine.EndActions.AutoGraXpert, Is.False,
            "Per-frame auto-GraXpert hook must default off");
    }

    [Test]
    public void EndActions_AutoGraXpertSurvivesRoundTrip() {
        // Critical for the Autorun UI: the checkbox round-trips
        // through GetStatus + the /end-actions endpoint and must
        // persist its state across requests.
        var engine = MakeEngine();
        engine.EndActions = new SequenceEndActions {
            AutoGraXpert = true
        };
        Assert.That(engine.EndActions.AutoGraXpert, Is.True);
        var status = engine.GetStatus();
        Assert.That(status.EndActions, Is.Not.Null);
        Assert.That(status.EndActions!.AutoGraXpert, Is.True);
    }

    [Test]
    public void EndActions_SetterReplacesConfig() {
        var engine = MakeEngine();
        engine.EndActions = new SequenceEndActions {
            ParkMount = true, StopTracking = true,
            WarmCamera = true, DisconnectGuider = true, RunOnStop = true
        };
        Assert.That(engine.EndActions.ParkMount, Is.True);
        Assert.That(engine.EndActions.WarmCamera, Is.True);
        Assert.That(engine.EndActions.RunOnStop, Is.True);
    }

    [Test]
    public void GetStatus_IncludesEndActions() {
        // Surfaced via WS so the UI can show the active end-action set
        // without an extra GET round-trip while a run is in flight.
        var engine = MakeEngine();
        engine.EndActions = new SequenceEndActions { ParkMount = true, WarmCamera = true };
        var status = engine.GetStatus();
        Assert.That(status.EndActions, Is.Not.Null);
        Assert.That(status.EndActions!.ParkMount, Is.True);
        Assert.That(status.EndActions.WarmCamera, Is.True);
        Assert.That(status.EndActions.StopTracking, Is.False);
    }

    [Test]
    public void LoadSequence_PreservesItemImageType() {
        // Calibration items routinely flow through LoadSequence; the
        // engine must keep their type intact end-to-end.
        var engine = MakeEngine();
        engine.LoadSequence(new List<SequenceItem> {
            new() { Name = "Darks 300s", Exposure = 300, Count = 20, ImageType = "DARK" },
            new() { Name = "Bias",       Exposure = 0,   Count = 50, ImageType = "BIAS" }
        });
        Assert.That(engine.Items[0].ImageType, Is.EqualTo("DARK"));
        Assert.That(engine.Items[1].ImageType, Is.EqualTo("BIAS"));
    }

    [Test]
    public void LoadSequence_DoesNotResetDitherConfig() {
        var engine = MakeEngine();
        engine.Dither = new DitherSettings { Enabled = true, Pixels = 7.5, EveryNFrames = 4 };

        engine.LoadSequence(new List<SequenceItem> {
            new() { Name = "Test", Exposure = 1.0, Count = 1 }
        });

        Assert.That(engine.Dither.Enabled, Is.True);
        Assert.That(engine.Dither.Pixels, Is.EqualTo(7.5));
        Assert.That(engine.Dither.EveryNFrames, Is.EqualTo(4));
    }
}

using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;
using NINA.INDI.Client;

namespace NINA.Headless.Test;

[TestFixture]
public class SequenceEngineDitherTests {

    private SequenceEngine MakeEngine() {
        var indi = new IndiClient("localhost", 7624);
        var equip = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance);
        var relay = new ImageRelayService(NullLogger<ImageRelayService>.Instance);
        var liveStack = new LiveStackingService(relay, NullLogger<LiveStackingService>.Instance);
        var phd2 = new PHD2Client(NullLogger<PHD2Client>.Instance);
        var autoFocus = new AutoFocusService(equip, NullLogger<AutoFocusService>.Instance);
        var emptyConfig = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var plateSolve = new PlateSolveService(emptyConfig, NullLogger<PlateSolveService>.Instance);
        var slewCenter = new SlewCenterService(equip, plateSolve, NullLogger<SlewCenterService>.Instance);
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        var meridianFlip = new MeridianFlipService(equip, phd2, slewCenter, autoFocus, profile,
            NullLogger<MeridianFlipService>.Instance);
        return new SequenceEngine(equip, relay, liveStack, phd2, meridianFlip,
            NullLogger<SequenceEngine>.Instance);
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

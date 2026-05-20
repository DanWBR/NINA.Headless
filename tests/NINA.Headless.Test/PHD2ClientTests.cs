using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

[TestFixture]
public class PHD2ClientTests {
    private PHD2Client _sut = null!;

    [SetUp]
    public void SetUp() {
        _sut = new PHD2Client(NullLogger<PHD2Client>.Instance);
    }

    [TearDown]
    public void TearDown() {
        _sut.Dispose();
    }

    private void Feed(string json) {
        using var doc = JsonDocument.Parse(json);
        _sut.HandleMessage(doc.RootElement);
    }

    // --- AppState event ---

    [Test]
    public void HandleMessage_AppStateEvent_UpdatesState() {
        Feed("""{"Event":"AppState","Timestamp":1234.5,"Host":"PHD2","Inst":1,"State":"Guiding"}""");

        Assert.That(_sut.AppState, Is.EqualTo("Guiding"));
        Assert.That(_sut.IsGuiding, Is.True);
    }

    [TestCase("Stopped", false, false, false, false)]
    [TestCase("Looping", false, false, false, true)]
    [TestCase("Calibrating", false, true, false, false)]
    [TestCase("Guiding", true, false, false, false)]
    [TestCase("Paused", false, false, true, false)]
    public void HandleMessage_AppStateEvent_ClassifiesCorrectly(
        string state, bool guiding, bool calibrating, bool paused, bool looping) {

        Feed($$"""{"Event":"AppState","State":"{{state}}"}""");

        Assert.That(_sut.AppState, Is.EqualTo(state));
        Assert.That(_sut.IsGuiding, Is.EqualTo(guiding));
        Assert.That(_sut.IsCalibrating, Is.EqualTo(calibrating));
        Assert.That(_sut.IsPaused, Is.EqualTo(paused));
        Assert.That(_sut.IsLooping, Is.EqualTo(looping));
    }

    [Test]
    public void AppStateChanged_FiresOnEvent() {
        string? captured = null;
        _sut.AppStateChanged += s => captured = s;

        Feed("""{"Event":"AppState","State":"Guiding"}""");

        Assert.That(captured, Is.EqualTo("Guiding"));
    }

    // --- GuideStep event ---

    [Test]
    public void HandleMessage_GuideStep_AppendsToBufferAndUpdatesRms() {
        Feed("""{"Event":"GuideStep","RADistanceRaw":0.5,"DECDistanceRaw":-0.3,"SNR":18.2}""");

        Assert.That(_sut.RecentSteps, Has.Count.EqualTo(1));
        var step = _sut.RecentSteps[0];
        Assert.That(step.RaPixels, Is.EqualTo(0.5));
        Assert.That(step.DecPixels, Is.EqualTo(-0.3));
        Assert.That(step.SNR, Is.EqualTo(18.2));
        Assert.That(_sut.RmsTotal, Is.GreaterThan(0));
    }

    [Test]
    public void HandleMessage_GuideStep_RmsCalculation() {
        // Without pixel scale, arcsec == pixels
        Feed("""{"Event":"GuideStep","RADistanceRaw":1.0,"DECDistanceRaw":0.0}""");
        Feed("""{"Event":"GuideStep","RADistanceRaw":-1.0,"DECDistanceRaw":0.0}""");
        Feed("""{"Event":"GuideStep","RADistanceRaw":1.0,"DECDistanceRaw":0.0}""");
        Feed("""{"Event":"GuideStep","RADistanceRaw":-1.0,"DECDistanceRaw":0.0}""");

        // RMS RA: sqrt((1+1+1+1)/4) = 1
        Assert.That(_sut.RmsRA, Is.EqualTo(1.0).Within(0.001));
        Assert.That(_sut.RmsDec, Is.EqualTo(0.0).Within(0.001));
        Assert.That(_sut.PeakRA, Is.EqualTo(1.0).Within(0.001));
    }

    [Test]
    public void GuideStepReceived_FiresOnEvent() {
        GuideStep? captured = null;
        _sut.GuideStepReceived += s => captured = s;

        Feed("""{"Event":"GuideStep","RADistanceRaw":0.42,"DECDistanceRaw":0.0}""");

        Assert.That(captured, Is.Not.Null);
        Assert.That(captured!.RaPixels, Is.EqualTo(0.42));
    }

    [Test]
    public void HandleMessage_BufferCapsAtMaxSteps() {
        for (int i = 0; i < PHD2Client.MaxSteps + 50; i++) {
            Feed("""{"Event":"GuideStep","RADistanceRaw":0.1,"DECDistanceRaw":0.0}""");
        }

        Assert.That(_sut.RecentSteps.Count, Is.EqualTo(PHD2Client.MaxSteps));
    }

    [Test]
    public void ClearStepHistory_ResetsBufferAndMetrics() {
        Feed("""{"Event":"GuideStep","RADistanceRaw":1.5,"DECDistanceRaw":1.5}""");
        Assert.That(_sut.RecentSteps, Is.Not.Empty);

        _sut.ClearStepHistory();

        Assert.That(_sut.RecentSteps, Is.Empty);
        Assert.That(_sut.RmsRA, Is.EqualTo(0));
        Assert.That(_sut.RmsDec, Is.EqualTo(0));
        Assert.That(_sut.RmsTotal, Is.EqualTo(0));
    }

    // --- Settling / SettleDone ---

    [Test]
    public void HandleMessage_Settling_SetsFlag() {
        Feed("""{"Event":"Settling","Distance":2.1,"Time":1.0,"SettleTime":10}""");

        Assert.That(_sut.IsSettling, Is.True);
        Assert.That(_sut.LastSettleStatus, Is.EqualTo("settling"));
    }

    [Test]
    public void HandleMessage_SettleDoneOk_ClearsFlagAndFiresEvent() {
        SettleResult? result = null;
        _sut.Settled += r => result = r;

        Feed("""{"Event":"Settling"}""");
        Feed("""{"Event":"SettleDone","Status":0,"TotalFrames":5,"DroppedFrames":0}""");

        Assert.That(_sut.IsSettling, Is.False);
        Assert.That(_sut.LastSettleStatus, Is.EqualTo("done"));
        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Status, Is.EqualTo(0));
        Assert.That(result.TotalFrames, Is.EqualTo(5));
    }

    [Test]
    public void HandleMessage_SettleDoneFailed_CapturesError() {
        SettleResult? result = null;
        _sut.Settled += r => result = r;

        Feed("""{"Event":"SettleDone","Status":1,"Error":"settle timeout"}""");

        Assert.That(_sut.LastSettleStatus, Is.EqualTo("failed"));
        Assert.That(result!.Error, Is.EqualTo("settle timeout"));
    }

    // --- Alert ---

    [Test]
    public void HandleMessage_Alert_CapturesMessageAndFires() {
        string? captured = null;
        _sut.Alert += m => captured = m;

        Feed("""{"Event":"Alert","Msg":"Star lost","Type":"error"}""");

        Assert.That(_sut.LastAlert, Is.EqualTo("Star lost"));
        Assert.That(_sut.LastAlertAt, Is.Not.Null);
        Assert.That(captured, Is.EqualTo("Star lost"));
    }

    // --- Snapshot semantics ---

    [Test]
    public void SnapshotSteps_ReturnsIndependentCopy() {
        Feed("""{"Event":"GuideStep","RADistanceRaw":0.1,"DECDistanceRaw":0.2}""");
        var snap = _sut.SnapshotSteps();
        Feed("""{"Event":"GuideStep","RADistanceRaw":0.3,"DECDistanceRaw":0.4}""");

        Assert.That(snap, Has.Count.EqualTo(1)); // unchanged by later events
    }

    // --- Ignored / unknown messages don't crash ---

    [Test]
    public void HandleMessage_UnknownEvent_DoesNotThrow() {
        Assert.DoesNotThrow(() => Feed("""{"Event":"Foo","Bar":42}"""));
        Assert.DoesNotThrow(() => Feed("""{"Event":"GuidingStopped"}"""));
        Assert.DoesNotThrow(() => Feed("""{"Event":"Version","PHDVersion":"2.6.11"}"""));
    }

    [Test]
    public void HandleMessage_MessageWithoutEventOrId_Ignored() {
        Assert.DoesNotThrow(() => Feed("""{"Random":"thing"}"""));
        Assert.That(_sut.AppState, Is.EqualTo("Stopped"));
    }

    // --- IsConnected default ---

    [Test]
    public void IsConnected_BeforeConnect_IsFalse() {
        Assert.That(_sut.IsConnected, Is.False);
    }

    [Test]
    public void DefaultHostAndPort_AreLocalhost4400() {
        Assert.That(_sut.Host, Is.EqualTo("localhost"));
        Assert.That(_sut.Port, Is.EqualTo(4400));
    }
}

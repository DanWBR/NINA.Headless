using NUnit.Framework;
using NINA.Polaris.Services.Logging;

namespace NINA.Polaris.Test.Services.Logging;

[TestFixture]
public class LogServiceTests {

    private static LogEntry MakeEntry(string msg = "test", string level = "info", string source = "server") =>
        new(Id: 0, At: DateTime.UtcNow, Level: level, Source: source, Message: msg);

    [Test]
    public void Append_AssignsMonotonicIds_StartingAtOne() {
        var svc = new LogService();
        var a = svc.Append(MakeEntry("first"));
        var b = svc.Append(MakeEntry("second"));
        var c = svc.Append(MakeEntry("third"));
        Assert.That(a.Id, Is.EqualTo(1));
        Assert.That(b.Id, Is.EqualTo(2));
        Assert.That(c.Id, Is.EqualTo(3));
        Assert.That(svc.CurrentId, Is.EqualTo(3));
    }

    [Test]
    public void SnapshotSince_ReturnsOnlyNewerEntries() {
        var svc = new LogService();
        var a = svc.Append(MakeEntry("a"));
        var b = svc.Append(MakeEntry("b"));
        var c = svc.Append(MakeEntry("c"));
        var snap = svc.SnapshotSince(a.Id, max: 500);
        // Strictly greater than cursor => entries b, c.
        Assert.That(snap.Entries.Count, Is.EqualTo(2));
        Assert.That(snap.Entries[0].Id, Is.EqualTo(b.Id));
        Assert.That(snap.Entries[1].Id, Is.EqualTo(c.Id));
        Assert.That(snap.Cursor, Is.EqualTo(c.Id));
        Assert.That(snap.Truncated, Is.False);
    }

    [Test]
    public void SnapshotSince_CapsAtMax() {
        var svc = new LogService();
        for (int i = 0; i < 10; i++) svc.Append(MakeEntry($"m{i}"));
        var snap = svc.SnapshotSince(0, max: 3);
        Assert.That(snap.Entries.Count, Is.EqualTo(3));
        Assert.That(snap.Entries[0].Id, Is.EqualTo(1));
        Assert.That(snap.Entries[2].Id, Is.EqualTo(3));
        Assert.That(snap.Cursor, Is.EqualTo(3));
    }

    [Test]
    public void Append_DropsHead_WhenOverMaxKept() {
        var svc = new LogService();
        // Append 1 over the cap so we know exactly one entry was evicted.
        for (int i = 0; i < LogService.MaxKept + 1; i++) svc.Append(MakeEntry($"m{i}"));
        Assert.That(svc.Snapshot().Count, Is.EqualTo(LogService.MaxKept));
        // Lowest Id still in the buffer = 2 (Id 1 was the head).
        Assert.That(svc.Snapshot()[0].Id, Is.EqualTo(2));
    }

    [Test]
    public void SnapshotSince_FlagsTruncated_WhenOlderThanOldestRetained() {
        var svc = new LogService();
        for (int i = 0; i < LogService.MaxKept + 5; i++) svc.Append(MakeEntry($"m{i}"));
        // Asking from Id 1 -- the first 5 Ids were evicted.
        var snap = svc.SnapshotSince(1, max: 100);
        Assert.That(snap.Truncated, Is.True);
    }

    [Test]
    public void Append_IsThreadSafe_NoIdCollisions() {
        var svc = new LogService();
        // 8 threads × 1000 appends. Cap is 5000 so the buffer ends
        // partially evicted, but the assigned Ids must be unique +
        // contiguous 1..8000.
        const int threads = 8, perThread = 1000;
        Parallel.For(0, threads, _ => {
            for (int i = 0; i < perThread; i++) svc.Append(MakeEntry("m"));
        });
        Assert.That(svc.CurrentId, Is.EqualTo(threads * perThread));
        // Whatever is left in the buffer must have unique Ids in [1, 8000].
        var ids = svc.Snapshot().Select(e => e.Id).ToList();
        Assert.That(ids.Count, Is.EqualTo(LogService.MaxKept));
        Assert.That(ids.Distinct().Count(), Is.EqualTo(LogService.MaxKept));
        Assert.That(ids.Min(), Is.GreaterThan(0));
        Assert.That(ids.Max(), Is.LessThanOrEqualTo(threads * perThread));
    }

    // -------- Sensitivity filter --------

    [Test]
    public void Append_RedactsPasswordInMessage() {
        var svc = new LogService();
        var e = svc.Append(MakeEntry("login attempt password=hunter2 from ip"));
        Assert.That(e.Message, Does.Contain("password=***"));
        Assert.That(e.Message, Does.Not.Contain("hunter2"));
    }

    [Test]
    public void Append_RedactsTokenInMessage() {
        var svc = new LogService();
        var e = svc.Append(MakeEntry("forwarded token=abc123def456 ok"));
        Assert.That(e.Message, Does.Contain("token=***"));
        Assert.That(e.Message, Does.Not.Contain("abc123def456"));
    }

    [Test]
    public void Append_RedactsBearerHeader() {
        var svc = new LogService();
        var e = svc.Append(MakeEntry("Authorization: Bearer ey.123.abc rejected"));
        Assert.That(e.Message, Does.Contain("Authorization: ***"));
        Assert.That(e.Message, Does.Not.Contain("ey.123.abc"));
    }

    [Test]
    public void Append_RedactsSessionCookie() {
        var svc = new LogService();
        var e = svc.Append(MakeEntry("Cookie: polaris_session=secret; other=v"));
        Assert.That(e.Message, Does.Contain("polaris_session=***"));
        Assert.That(e.Message, Does.Not.Contain("secret"));
    }

    [Test]
    public void Append_StripsQueryStringFromAuthPaths() {
        var svc = new LogService();
        var raw = new LogEntry(0, DateTime.UtcNow, "info", "http", "GET /api/auth/login",
            Path: "/api/auth/login?token=leaked");
        var e = svc.Append(raw);
        Assert.That(e.Path, Is.EqualTo("/api/auth/login"));
        Assert.That(e.Path, Does.Not.Contain("leaked"));
    }

    [Test]
    public void Clear_EmptiesBuffer_KeepsCurrentId() {
        var svc = new LogService();
        svc.Append(MakeEntry("a"));
        svc.Append(MakeEntry("b"));
        var beforeId = svc.CurrentId;
        svc.Clear();
        Assert.That(svc.Snapshot().Count, Is.EqualTo(0));
        // Append after clear keeps the monotonic Id sequence going.
        var c = svc.Append(MakeEntry("c"));
        Assert.That(c.Id, Is.EqualTo(beforeId + 1));
    }

    [Test]
    public async Task ExportJsonlAsync_WritesOneEntryPerLine() {
        var svc = new LogService();
        for (int i = 0; i < 3; i++) svc.Append(MakeEntry($"m{i}"));
        using var ms = new MemoryStream();
        await svc.ExportJsonlAsync(ms);
        ms.Position = 0;
        var text = new StreamReader(ms).ReadToEnd();
        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.That(lines.Length, Is.EqualTo(3));
        // Each line is a valid JSON object with an id field.
        foreach (var line in lines) {
            Assert.That(line.TrimStart(), Does.StartWith("{"));
            Assert.That(line, Does.Contain("\"id\""));
        }
    }

    [Test]
    public void Appended_EventFires_OnEachAppend() {
        var svc = new LogService();
        var received = new List<LogEntry>();
        svc.Appended += received.Add;
        svc.Append(MakeEntry("a"));
        svc.Append(MakeEntry("b"));
        Assert.That(received.Count, Is.EqualTo(2));
        Assert.That(received[0].Message, Is.EqualTo("a"));
        Assert.That(received[1].Id, Is.EqualTo(2));
    }

    [Test]
    public void Appended_SubscriberException_DoesNotPropagate() {
        var svc = new LogService();
        svc.Appended += _ => throw new InvalidOperationException("boom");
        // Must not throw.
        Assert.DoesNotThrow(() => svc.Append(MakeEntry("ok")));
        Assert.That(svc.Snapshot().Count, Is.EqualTo(1));
    }
}

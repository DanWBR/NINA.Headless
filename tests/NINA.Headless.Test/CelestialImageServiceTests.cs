using NUnit.Framework;
using NINA.Headless.Services;

namespace NINA.Headless.Test;

/// <summary>
/// Pure-function tests for the slug normaliser used by the on-disk cache.
/// We don't exercise the HTTP path here — that would mean mocking
/// HttpMessageHandler for a thin shim, which is more ceremony than signal.
/// What does need coverage is the slug logic: it has to be consistent
/// across casings and special characters, otherwise two requests for the
/// same object would miss the cache and hit NASA / Wikipedia twice.
/// </summary>
[TestFixture]
public class CelestialImageServiceTests {

    [TestCase("M31",               ExpectedResult = "m31")]
    [TestCase("m31",               ExpectedResult = "m31")]
    [TestCase("M 31",              ExpectedResult = "m31")]
    [TestCase("NGC 7000",          ExpectedResult = "ngc7000")]
    [TestCase("NGC7000",           ExpectedResult = "ngc7000")]
    [TestCase("Sh2-279",           ExpectedResult = "sh2279")]
    [TestCase("22P/Kopff",         ExpectedResult = "22pkopff")]
    [TestCase("Moon",              ExpectedResult = "moon")]
    [TestCase("Andromeda Galaxy",  ExpectedResult = "andromedagalaxy")]
    [TestCase("M44 (Praesepe)",    ExpectedResult = "m44praesepe")]
    public string Slugify_NormalisesCommonNames(string input) =>
        CelestialImageService.Slugify(input);

    [Test]
    public void Slugify_EmptyOrPunctuationOnly_GivesUnknown() {
        Assert.That(CelestialImageService.Slugify(""),       Is.EqualTo("unknown"));
        Assert.That(CelestialImageService.Slugify("---"),    Is.EqualTo("unknown"));
        Assert.That(CelestialImageService.Slugify("(...) "), Is.EqualTo("unknown"));
    }

    [Test]
    public void CelestialImage_NotAvailable_PreservesError() {
        var img = CelestialImage.NotAvailable("offline");
        Assert.That(img.Available,    Is.False);
        Assert.That(img.Error,        Is.EqualTo("offline"));
        Assert.That(img.ThumbnailUrl, Is.Null);
    }
}

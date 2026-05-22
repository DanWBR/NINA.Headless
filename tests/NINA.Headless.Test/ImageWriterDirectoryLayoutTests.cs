using NUnit.Framework;
using NINA.Headless.Services;
using NINA.Image.ImageData;
using NINA.Image.Interfaces;

namespace NINA.Headless.Test;

/// <summary>
/// Verifies the standard folder layout that ImageWriterService.BuildSubDir
/// produces for each IMAGETYP. This is the contract STUDIO relies on for
/// auto-matching calibration frames to lights (darks by exposure+gain,
/// flats by filter+gain) — so it has to stay stable across refactors.
/// </summary>
[TestFixture]
public class ImageWriterDirectoryLayoutTests {

    private static IImageData Frame(string filter, double exp, int gain, string target,
                                     string imageType, DateTime creation) {
        var props = new ImageProperties { Width = 100, Height = 100, BitDepth = 16 };
        var meta = new ImageMetaData {
            CreationTime = creation,
            Exposure  = new ImageMetaData.ExposureInfo  { ExposureTime = exp, Filter = filter, ImageType = imageType },
            Camera    = new ImageMetaData.CameraInfo    { Gain = gain },
            Target    = new ImageMetaData.TargetInfo    { Name = target }
        };
        return new BaseImageData(new ushort[100 * 100], props, meta);
    }

    private static UserProfile EmptyProfile() => new();

    private static DateTime Session(int y, int mo, int d) => new(y, mo, d);

    [Test]
    public void Light_GoesUnder_rig_lights_target_filter_session() {
        var img = Frame("Ha", 300, 100, "M31", "LIGHT",
            new DateTime(2026, 5, 21, 22, 30, 0, DateTimeKind.Local));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "Backyard 130mm APO", Session(2026, 5, 21));
        Assert.That(sub, Is.EqualTo(Path.Combine("Backyard_130mm_APO",
            "lights", "M31", "Ha", "2026-05-21")));
    }

    [Test]
    public void Light_WithSpacesInTargetName_NormalisedToUnderscores() {
        var img = Frame("L", 60, 0, "Veil Nebula", "LIGHT",
            new DateTime(2026, 5, 21, 22, 0, 0, DateTimeKind.Local));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "Default", Session(2026, 5, 21));
        Assert.That(sub, Does.Contain("Veil_Nebula"));
    }

    [Test]
    public void Dark_GoesUnder_rig_calibration_dark_exposure_gain() {
        var img = Frame("", 300, 100, "", "DARK", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("DARK", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "dark", "300s_g100")));
    }

    [Test]
    public void Bias_GoesUnder_rig_calibration_bias_gain() {
        var img = Frame("", 0, 100, "", "BIAS", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("BIAS", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "bias", "g100")));
    }

    [Test]
    public void Flat_GoesUnder_rig_calibration_flat_filterGain() {
        var img = Frame("Ha", 0.5, 100, "", "FLAT", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("FLAT", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "flat", "Ha_g100")));
    }

    [Test]
    public void DarkFlat_GetsItsOwnBucket() {
        var img = Frame("", 0.5, 100, "", "DARKFLAT", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("DARKFLAT", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "darkflat", "0.5s_g100")));
    }

    [Test]
    public void UnknownTarget_FallsBackTo_Unknown() {
        var img = Frame("L", 60, 0, "", "LIGHT", new DateTime(2026, 5, 21));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "MyRig", Session(2026, 5, 21));
        Assert.That(sub, Does.Contain("Unknown"));
    }

    [Test]
    public void FractionalExposure_FormattedWithoutTrailingZeros() {
        var img = Frame("L", 0.25, 100, "", "DARK", DateTime.Now);
        var sub = ImageWriterService.BuildSubDir("DARK", img, EmptyProfile(),
            "MyRig", DateTime.Today);
        Assert.That(sub, Is.EqualTo(Path.Combine("MyRig", "calibration", "dark", "0.25s_g100")));
    }

    [Test]
    public void EmptyRigName_FallsBackTo_Default() {
        var img = Frame("L", 60, 0, "M42", "LIGHT", new DateTime(2026, 5, 21));
        var sub = ImageWriterService.BuildSubDir("LIGHT", img, EmptyProfile(),
            "", Session(2026, 5, 21));
        Assert.That(sub.StartsWith("Default"), Is.True,
            "Empty rig should default to 'Default'");
    }

    // --- SessionDateForLocal: noon-to-noon convention ---

    [Test]
    public void SessionDate_PastNoon_IsCurrentDate() {
        // 22:30 local on May 21 → still May 21 (the evening just started)
        var local = new DateTime(2026, 5, 21, 22, 30, 0, DateTimeKind.Local);
        Assert.That(ImageWriterService.SessionDateForLocal(local),
            Is.EqualTo(new DateTime(2026, 5, 21)));
    }

    [Test]
    public void SessionDate_BeforeNoon_IsPreviousDate() {
        // 03:00 local on May 22 → belongs to the May 21 night
        var local = new DateTime(2026, 5, 22, 3, 0, 0, DateTimeKind.Local);
        Assert.That(ImageWriterService.SessionDateForLocal(local),
            Is.EqualTo(new DateTime(2026, 5, 21)));
    }

    [Test]
    public void SessionDate_AtExactlyNoon_RollsForward() {
        // 12:00 is the start of the new session in our convention
        var local = new DateTime(2026, 5, 22, 12, 0, 0, DateTimeKind.Local);
        Assert.That(ImageWriterService.SessionDateForLocal(local),
            Is.EqualTo(new DateTime(2026, 5, 22)));
    }
}

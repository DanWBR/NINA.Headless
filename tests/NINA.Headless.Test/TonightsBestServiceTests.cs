using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Headless.Services;
using NINA.INDI.Client;

namespace NINA.Headless.Test;

/// <summary>
/// Smoke tests for the Tonight's Best ranking service. These exercise
/// the full Compute() path end-to-end against the real catalog and
/// AstronomyEngine (no network calls), so they catch broad regressions
/// like "service can't even start", "catalog can't be enumerated",
/// "AstronomyEngine throws on a particular planet". The deeper
/// astronomical correctness is the responsibility of AstronomyEngine
/// itself, which has its own test suite.
/// </summary>
[TestFixture]
public class TonightsBestServiceTests {

    private TonightsBestService MakeService(double lat, double lng) {
        var emptyConfig = new ConfigurationBuilder().Build();
        var profile = new ProfileService(emptyConfig, NullLogger<ProfileService>.Instance);
        profile.Active.Latitude  = lat;
        profile.Active.Longitude = lng;

        var catalog  = new SkyCatalogService();
        var altitude = new AltitudeService(profile);
        var indi     = new IndiClient("localhost", 7624);
        var equip    = new EquipmentManager(indi, NullLogger<EquipmentManager>.Instance);
        return new TonightsBestService(catalog, altitude, equip, profile,
            NullLogger<TonightsBestService>.Instance);
    }

    [Test]
    public void Compute_AtModerateLatitude_ReturnsRankedList() {
        // -5° lat (northeast Brazil). Plenty of southern sky DSOs are
        // always above 30° somewhere in the night.
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 30);

        Assert.That(result.Items, Is.Not.Empty, "Should find at least some visible objects");
        Assert.That(result.Items.Count, Is.LessThanOrEqualTo(30));

        // Scores monotonic decreasing.
        for (var i = 1; i < result.Items.Count; i++) {
            Assert.That(result.Items[i].Score, Is.LessThanOrEqualTo(result.Items[i - 1].Score),
                "Items must be sorted by score descending");
        }

        // The Moon should be in there (always above-horizon-somewhere).
        Assert.That(result.Items.Any(i => i.Category == "Moon"),
            "Moon should always be a candidate");
    }

    [Test]
    public void Compute_HasNightWindow_NonZeroDuration() {
        var sut = MakeService(lat: 0, lng: 0);
        var result = sut.Compute(limit: 5);
        Assert.That(result.NightEndUtc, Is.GreaterThan(result.NightStartUtc));
    }

    [Test]
    public void Compute_LimitClampedToList() {
        var sut = MakeService(lat: -5, lng: -37);
        var result = sut.Compute(limit: 3);
        Assert.That(result.Items.Count, Is.LessThanOrEqualTo(3));
    }

    [Test]
    public void Compute_IncludesPlanetCategoryWhenVisible() {
        // At least one planet is virtually always above the horizon for
        // some part of any given night anywhere on Earth.
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 50);
        Assert.That(result.Items.Any(i => i.Category == "Planet"),
            "At least one planet should be visible tonight from a moderate latitude");
    }

    [Test]
    public void Compute_DsoEntriesHaveScoresInPlausibleRange() {
        var sut = MakeService(lat: -5.18, lng: -37.36);
        var result = sut.Compute(limit: 30);
        foreach (var item in result.Items.Where(i => i.Category == "Dso")) {
            Assert.That(item.Score, Is.GreaterThan(0).And.LessThan(200),
                $"DSO {item.Name} has implausible score {item.Score}");
            Assert.That(item.PeakAltDeg, Is.GreaterThanOrEqualTo(30),
                $"DSO {item.Name} below the 30° filter threshold");
        }
    }
}

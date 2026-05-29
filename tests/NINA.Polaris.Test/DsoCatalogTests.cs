using System.Linq;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using NINA.Polaris.Services.Sky;

namespace NINA.Polaris.Test;

/// <summary>
/// CAT-2 tests for <see cref="DsoCatalog"/>. They run against the
/// real bundled <c>wwwroot/catalogs/dso/dso.db</c> built once by
/// <c>scripts/build-dso-catalog.py</c> + committed to the repo, so
/// the assertions pin the data shape (presence of NGC 7331, Arp 273,
/// HCG 92, IC 5146, etc) in addition to the query mechanics.
/// </summary>
[TestFixture]
public class DsoCatalogTests {

    private DsoCatalog _catalog = null!;

    [OneTimeSetUp]
    public void SetUp() {
        _catalog = new DsoCatalog(new TestEnv(), NullLogger<DsoCatalog>.Instance);
        if (!_catalog.IsAvailable) {
            Assert.Ignore(
                $"dso.db not present at {_catalog.DbPath}; run " +
                "`python scripts/build-dso-catalog.py` to populate it. " +
                "Skipping DsoCatalog assertions.");
        }
    }

    [Test]
    public void IsAvailable_WhenDbPresent_IsTrue() {
        Assert.That(_catalog.IsAvailable, Is.True);
    }

    [Test]
    public void ObjectCount_IsAtLeastTenThousand() {
        Assert.That(_catalog.ObjectCount, Is.GreaterThan(10_000),
            "Bundled DB should hold OpenNGC + Vizier + Caldwell rows " +
            "(currently ~14.5k).");
    }

    [Test]
    public async Task GetByNameAsync_KnownObjects_AllResolve() {
        foreach (var name in new[] { "NGC 7331", "IC 5146", "M31", "C14",
                                      "Arp 273", "Sh2 279", "HCG 92" }) {
            var obj = await _catalog.GetByNameAsync(name);
            Assert.That(obj, Is.Not.Null, $"{name} should be in the DB");
            // M / C entries resolve through aliases to their canonical
            // NGC/IC primary name (e.g. M31 -> NGC 224), so accept a
            // match against either the primary Name or the alias list.
            var matches = string.Equals(obj!.Name, name,
                    System.StringComparison.OrdinalIgnoreCase)
                || (obj.Aliases != null && obj.Aliases.Any(a =>
                    string.Equals(a, name, System.StringComparison.OrdinalIgnoreCase)));
            Assert.That(matches, Is.True,
                $"Resolved row '{obj.Name}' should match '{name}' on Name or Aliases");
            Assert.That(obj.RaHours, Is.InRange(0, 24));
            Assert.That(obj.DecDeg, Is.InRange(-90, 90));
            Assert.That(obj.Type, Is.Not.Empty);
        }
    }

    [Test]
    public async Task GetByNameAsync_Miss_ReturnsNull() {
        var obj = await _catalog.GetByNameAsync("NGC 99999");
        Assert.That(obj, Is.Null);
    }

    [Test]
    public async Task SearchAsync_PrefixMatch_ReturnsResults() {
        var results = await _catalog.SearchAsync("NGC 733", limit: 20);
        Assert.That(results, Is.Not.Empty,
            "NGC 7331 / 7339 / etc all start with NGC 733");
        // NGC 7331 is the brightest hit (mag 9.4) — should rank first.
        Assert.That(results[0].Name, Does.StartWith("NGC 733"));
    }

    [Test]
    public async Task FilterAsync_ByCatalog_ReturnsThatCatalogOnly() {
        var results = await _catalog.FilterAsync(
            new DsoCatalog.DsoFilter(Catalog: "Arp", Limit: 500));
        Assert.That(results, Is.Not.Empty);
        Assert.That(results.All(r => r.Catalog == "Arp"), Is.True,
            "Catalog filter must be respected");
    }

    [Test]
    public async Task FilterAsync_ByTypeAndMagnitude_NarrowsResults() {
        var bright = await _catalog.FilterAsync(
            new DsoCatalog.DsoFilter(Type: "Galaxy", MaxMagnitude: 9.0,
                                     Limit: 50));
        Assert.That(bright, Is.Not.Empty);
        Assert.That(bright.All(r => r.Type == "Galaxy"
                                    && (r.Magnitude ?? double.MaxValue) <= 9.0),
            Is.True);
    }

    [Test]
    public async Task GetCatalogsAsync_IncludesAllSources() {
        var cats = await _catalog.GetCatalogsAsync();
        // Subset (full set may grow); at minimum what build-dso-catalog.py produces.
        foreach (var expected in new[] { "NGC", "IC", "M", "C", "Arp",
                                          "Sh2", "HCG", "AGC" }) {
            Assert.That(cats, Contains.Item(expected),
                $"Catalog '{expected}' should be present");
        }
    }

    [Test]
    public async Task GetTypesAsync_IncludesCommonDsoTypes() {
        var types = await _catalog.GetTypesAsync();
        Assert.That(types, Is.Not.Empty);
        // Spot-check a few that any non-trivial catalog will surface.
        Assert.That(types.Any(t => t.Contains("Galaxy",
            StringComparison.OrdinalIgnoreCase)), Is.True);
        Assert.That(types.Any(t => t.Contains("Nebula",
            StringComparison.OrdinalIgnoreCase)), Is.True);
    }

    [Test]
    public async Task QueryRegionAsync_NearNGC7331_FindsHCG92() {
        // NGC 7331 ≈ RA 22h 37m, Dec +34.4°. HCG 92 (Stephan's Quintet)
        // sits ~30 arcmin to the south of it. A 2° cone search should
        // pick up both NGC 7331 itself + the HCG group nearby.
        var hits = await _catalog.QueryRegionAsync(
            raHours: 22.617, decDeg: 34.4, radiusDeg: 2.0,
            magLimit: 14.0, limit: 100);
        Assert.That(hits, Is.Not.Empty);
        var names = hits.Select(h => h.Name).ToList();
        Assert.That(names.Any(n => n == "NGC 7331"), Is.True,
            "NGC 7331 should be inside its own 2° cone");
        Assert.That(names.Any(n => n == "HCG 92"), Is.True,
            "Stephan's Quintet (HCG 92) is ~30' away from NGC 7331");
    }

    [Test]
    public async Task LoadAllAsync_WithMagCap_FiltersDimRows() {
        var bright = await _catalog.LoadAllAsync(magCap: 8.0);
        // Should be enough Messier + a handful of NGC/IC, but not the
        // dim Abell clusters or faint NGC galaxies.
        Assert.That(bright.Count, Is.GreaterThan(50));
        Assert.That(bright.All(o => (o.Magnitude ?? double.MaxValue) <= 8.0),
            Is.True);
    }

    /// <summary>Mirrors the pattern from CometEphemerisServiceTests:
    /// resolve the real wwwroot under src/NINA.Polaris/ so the bundled
    /// dso.db is found at test time.</summary>
    private class TestEnv : IWebHostEnvironment {
        public string WebRootPath { get; set; } = LocateWwwroot();
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";

        private static string LocateWwwroot() {
            var dir = AppContext.BaseDirectory;
            for (var i = 0; i < 8; i++) {
                var candidate = Path.Combine(dir, "src", "NINA.Polaris", "wwwroot");
                if (Directory.Exists(candidate)) return candidate;
                dir = Path.GetDirectoryName(dir) ?? "";
                if (string.IsNullOrEmpty(dir)) break;
            }
            return "wwwroot";
        }
    }
}

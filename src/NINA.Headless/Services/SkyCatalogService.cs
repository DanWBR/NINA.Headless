namespace NINA.Headless.Services;

public class SkyCatalogService {
    private readonly List<CatalogObject> _catalog;

    public SkyCatalogService() {
        _catalog = BuildCatalog();
    }

    public List<CatalogObject> Search(string query, int maxResults = 20) {
        if (string.IsNullOrWhiteSpace(query)) return [];

        var q = query.Trim();

        var exact = _catalog.FirstOrDefault(o =>
            o.Name.Equals(q, StringComparison.OrdinalIgnoreCase) ||
            o.Aliases.Any(a => a.Equals(q, StringComparison.OrdinalIgnoreCase)));

        if (exact != null)
            return [exact];

        var normalized = q.Replace(" ", "").ToUpperInvariant();

        return _catalog
            .Where(o => Matches(o, normalized, q))
            .OrderBy(o => MatchScore(o, normalized))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>
    /// Filtered query for the Sky Atlas. All parameters are optional; null /
    /// empty values are ignored. Results sorted by magnitude (brightest first).
    /// </summary>
    public List<CatalogObject> Filter(CatalogFilter filter, int maxResults = 50) {
        IEnumerable<CatalogObject> q = _catalog;

        if (!string.IsNullOrWhiteSpace(filter.Query)) {
            var qn = filter.Query.Replace(" ", "").ToUpperInvariant();
            q = q.Where(o => Matches(o, qn, filter.Query));
        }
        if (!string.IsNullOrWhiteSpace(filter.Type)) {
            q = q.Where(o => o.Type.Equals(filter.Type, StringComparison.OrdinalIgnoreCase));
        }
        if (filter.MinMagnitude.HasValue) {
            q = q.Where(o => o.Magnitude >= filter.MinMagnitude.Value);
        }
        if (filter.MaxMagnitude.HasValue) {
            q = q.Where(o => o.Magnitude <= filter.MaxMagnitude.Value);
        }
        if (filter.MinDec.HasValue) {
            q = q.Where(o => o.Dec >= filter.MinDec.Value);
        }
        if (filter.MaxDec.HasValue) {
            q = q.Where(o => o.Dec <= filter.MaxDec.Value);
        }

        return q.OrderBy(o => o.Magnitude).Take(maxResults).ToList();
    }

    /// <summary>Distinct object types present in the catalog (for filter dropdowns).</summary>
    public List<string> GetObjectTypes() =>
        _catalog.Select(o => o.Type).Distinct().OrderBy(t => t).ToList();

    public CatalogObject? GetByName(string name) {
        return _catalog.FirstOrDefault(o =>
            o.Name.Equals(name, StringComparison.OrdinalIgnoreCase) ||
            o.Aliases.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>
    /// All catalog objects — used by services that need to walk the full
    /// list (e.g. TonightsBestService ranking visible DSOs).
    /// </summary>
    public IReadOnlyList<CatalogObject> AllObjects => _catalog;

    private static bool Matches(CatalogObject obj, string normalized, string original) {
        var nameNorm = obj.Name.Replace(" ", "").ToUpperInvariant();
        if (nameNorm.Contains(normalized)) return true;

        foreach (var alias in obj.Aliases) {
            if (alias.Replace(" ", "").ToUpperInvariant().Contains(normalized))
                return true;
        }

        if (obj.CommonName != null &&
            obj.CommonName.Contains(original, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static int MatchScore(CatalogObject obj, string normalized) {
        var nameNorm = obj.Name.Replace(" ", "").ToUpperInvariant();
        if (nameNorm == normalized) return 0;
        if (nameNorm.StartsWith(normalized)) return 1;
        if (nameNorm.Contains(normalized)) return 2;
        return 3;
    }

    private static List<CatalogObject> BuildCatalog() {
        var list = new List<CatalogObject>(300);

        // Messier objects (complete catalog)
        AddMessier(list);
        // Caldwell objects (popular selection)
        AddCaldwell(list);

        return list;
    }

    private static void AddMessier(List<CatalogObject> list) {
        // RA in decimal hours, Dec in decimal degrees (J2000)
        Add(list, "M 1", 5.5753, 22.0145, 8.4, "Supernova Remnant", "Crab Nebula", ["NGC 1952"]);
        Add(list, "M 2", 21.5578, -0.8233, 6.5, "Globular Cluster", null, ["NGC 7089"]);
        Add(list, "M 3", 13.7033, 28.3772, 6.2, "Globular Cluster", null, ["NGC 5272"]);
        Add(list, "M 4", 16.3931, -26.5258, 5.6, "Globular Cluster", null, ["NGC 6121"]);
        Add(list, "M 5", 15.3089, 2.0811, 5.7, "Globular Cluster", null, ["NGC 5904"]);
        Add(list, "M 6", 17.6714, -32.2158, 4.2, "Open Cluster", "Butterfly Cluster", ["NGC 6405"]);
        Add(list, "M 7", 17.8978, -34.7928, 3.3, "Open Cluster", "Ptolemy Cluster", ["NGC 6475"]);
        Add(list, "M 8", 18.0633, -24.3833, 6.0, "Emission Nebula", "Lagoon Nebula", ["NGC 6523"]);
        Add(list, "M 9", 17.3200, -18.5158, 7.7, "Globular Cluster", null, ["NGC 6333"]);
        Add(list, "M 10", 16.9528, -4.1003, 6.6, "Globular Cluster", null, ["NGC 6254"]);
        Add(list, "M 11", 18.8514, -6.2667, 6.3, "Open Cluster", "Wild Duck Cluster", ["NGC 6705"]);
        Add(list, "M 12", 16.7867, -1.9486, 6.7, "Globular Cluster", null, ["NGC 6218"]);
        Add(list, "M 13", 16.6947, 36.4614, 5.8, "Globular Cluster", "Great Globular Cluster", ["NGC 6205"]);
        Add(list, "M 14", 17.6267, -3.2458, 7.6, "Globular Cluster", null, ["NGC 6402"]);
        Add(list, "M 15", 21.4997, 12.1669, 6.2, "Globular Cluster", null, ["NGC 7078"]);
        Add(list, "M 16", 18.3133, -13.7867, 6.0, "Emission Nebula", "Eagle Nebula", ["NGC 6611"]);
        Add(list, "M 17", 18.3467, -16.1833, 6.0, "Emission Nebula", "Omega Nebula", ["NGC 6618"]);
        Add(list, "M 18", 18.3308, -17.1333, 7.5, "Open Cluster", null, ["NGC 6613"]);
        Add(list, "M 19", 17.0450, -26.2675, 6.8, "Globular Cluster", null, ["NGC 6273"]);
        Add(list, "M 20", 18.0433, -23.0283, 6.3, "Emission Nebula", "Trifid Nebula", ["NGC 6514"]);
        Add(list, "M 21", 18.0750, -22.5000, 6.5, "Open Cluster", null, ["NGC 6531"]);
        Add(list, "M 22", 18.6064, -23.9047, 5.1, "Globular Cluster", null, ["NGC 6656"]);
        Add(list, "M 23", 17.9475, -19.0167, 6.9, "Open Cluster", null, ["NGC 6494"]);
        Add(list, "M 24", 18.2833, -18.4833, 4.6, "Star Cloud", "Sagittarius Star Cloud", []);
        Add(list, "M 25", 18.5283, -19.2333, 6.5, "Open Cluster", null, ["IC 4725"]);
        Add(list, "M 26", 18.7525, -9.3833, 8.0, "Open Cluster", null, ["NGC 6694"]);
        Add(list, "M 27", 19.9933, 22.7211, 7.5, "Planetary Nebula", "Dumbbell Nebula", ["NGC 6853"]);
        Add(list, "M 28", 18.4078, -24.8697, 6.8, "Globular Cluster", null, ["NGC 6626"]);
        Add(list, "M 29", 20.3975, 38.5167, 7.1, "Open Cluster", null, ["NGC 6913"]);
        Add(list, "M 30", 21.6733, -23.1797, 7.2, "Globular Cluster", null, ["NGC 7099"]);
        Add(list, "M 31", 0.7122, 41.2689, 3.4, "Spiral Galaxy", "Andromeda Galaxy", ["NGC 224"]);
        Add(list, "M 32", 0.7111, 40.8653, 8.1, "Elliptical Galaxy", null, ["NGC 221"]);
        Add(list, "M 33", 1.5647, 30.6600, 5.7, "Spiral Galaxy", "Triangulum Galaxy", ["NGC 598"]);
        Add(list, "M 34", 2.7008, 42.7833, 5.5, "Open Cluster", null, ["NGC 1039"]);
        Add(list, "M 35", 6.1483, 24.3333, 5.3, "Open Cluster", null, ["NGC 2168"]);
        Add(list, "M 36", 5.6017, 34.1333, 6.3, "Open Cluster", null, ["NGC 1960"]);
        Add(list, "M 37", 5.8725, 32.5500, 6.2, "Open Cluster", null, ["NGC 2099"]);
        Add(list, "M 38", 5.4783, 35.8333, 7.4, "Open Cluster", null, ["NGC 1912"]);
        Add(list, "M 39", 21.5333, 48.4333, 4.6, "Open Cluster", null, ["NGC 7092"]);
        Add(list, "M 40", 12.3700, 58.0833, 8.4, "Double Star", "Winnecke 4", []);
        Add(list, "M 41", 6.7675, -20.7333, 4.5, "Open Cluster", null, ["NGC 2287"]);
        Add(list, "M 42", 5.5908, -5.3911, 4.0, "Emission Nebula", "Orion Nebula", ["NGC 1976"]);
        Add(list, "M 43", 5.5917, -5.2667, 9.0, "Emission Nebula", "De Mairan's Nebula", ["NGC 1982"]);
        Add(list, "M 44", 8.6733, 19.6667, 3.7, "Open Cluster", "Beehive Cluster", ["NGC 2632"]);
        Add(list, "M 45", 3.7908, 24.1167, 1.6, "Open Cluster", "Pleiades", []);
        Add(list, "M 46", 7.6958, -14.8167, 6.1, "Open Cluster", null, ["NGC 2437"]);
        Add(list, "M 47", 7.6100, -14.5000, 5.2, "Open Cluster", null, ["NGC 2422"]);
        Add(list, "M 48", 8.2283, -5.8000, 5.8, "Open Cluster", null, ["NGC 2548"]);
        Add(list, "M 49", 12.4975, 8.0003, 8.4, "Elliptical Galaxy", null, ["NGC 4472"]);
        Add(list, "M 50", 7.0525, -8.3500, 5.9, "Open Cluster", null, ["NGC 2323"]);
        Add(list, "M 51", 13.4992, 47.1953, 8.4, "Spiral Galaxy", "Whirlpool Galaxy", ["NGC 5194"]);
        Add(list, "M 52", 23.4117, 61.5833, 7.3, "Open Cluster", null, ["NGC 7654"]);
        Add(list, "M 53", 13.2133, 18.1681, 7.6, "Globular Cluster", null, ["NGC 5024"]);
        Add(list, "M 54", 18.9183, -30.4783, 7.6, "Globular Cluster", null, ["NGC 6715"]);
        Add(list, "M 55", 19.6667, -30.9647, 6.3, "Globular Cluster", null, ["NGC 6809"]);
        Add(list, "M 56", 19.2769, 30.1842, 8.3, "Globular Cluster", null, ["NGC 6779"]);
        Add(list, "M 57", 18.8933, 33.0286, 8.8, "Planetary Nebula", "Ring Nebula", ["NGC 6720"]);
        Add(list, "M 58", 12.6292, 11.8189, 9.7, "Spiral Galaxy", null, ["NGC 4579"]);
        Add(list, "M 59", 12.7000, 11.6472, 9.6, "Elliptical Galaxy", null, ["NGC 4621"]);
        Add(list, "M 60", 12.7275, 11.5528, 8.8, "Elliptical Galaxy", null, ["NGC 4649"]);
        Add(list, "M 61", 12.3650, 4.4736, 9.7, "Spiral Galaxy", null, ["NGC 4303"]);
        Add(list, "M 62", 17.0217, -30.1133, 6.5, "Globular Cluster", null, ["NGC 6266"]);
        Add(list, "M 63", 13.2633, 42.0292, 8.6, "Spiral Galaxy", "Sunflower Galaxy", ["NGC 5055"]);
        Add(list, "M 64", 12.9450, 21.6828, 8.5, "Spiral Galaxy", "Black Eye Galaxy", ["NGC 4826"]);
        Add(list, "M 65", 11.3150, 13.0922, 9.3, "Spiral Galaxy", null, ["NGC 3623"]);
        Add(list, "M 66", 11.3383, 12.9917, 8.9, "Spiral Galaxy", null, ["NGC 3627"]);
        Add(list, "M 67", 8.8575, 11.8167, 6.1, "Open Cluster", null, ["NGC 2682"]);
        Add(list, "M 68", 12.6564, -26.7447, 7.8, "Globular Cluster", null, ["NGC 4590"]);
        Add(list, "M 69", 18.5233, -32.3478, 7.6, "Globular Cluster", null, ["NGC 6637"]);
        Add(list, "M 70", 18.7225, -32.2992, 7.9, "Globular Cluster", null, ["NGC 6681"]);
        Add(list, "M 71", 19.8958, 18.7792, 8.2, "Globular Cluster", null, ["NGC 6838"]);
        Add(list, "M 72", 20.8922, -12.5372, 9.3, "Globular Cluster", null, ["NGC 6981"]);
        Add(list, "M 73", 20.9828, -12.6333, 9.0, "Asterism", null, ["NGC 6994"]);
        Add(list, "M 74", 1.6117, 15.7836, 9.4, "Spiral Galaxy", "Phantom Galaxy", ["NGC 628"]);
        Add(list, "M 75", 20.1017, -21.9214, 8.5, "Globular Cluster", null, ["NGC 6864"]);
        Add(list, "M 76", 1.7042, 51.5750, 10.1, "Planetary Nebula", "Little Dumbbell", ["NGC 650"]);
        Add(list, "M 77", 2.7117, -0.0133, 8.9, "Spiral Galaxy", null, ["NGC 1068"]);
        Add(list, "M 78", 5.7792, 0.0500, 8.3, "Reflection Nebula", null, ["NGC 2068"]);
        Add(list, "M 79", 5.4050, -24.5242, 7.7, "Globular Cluster", null, ["NGC 1904"]);
        Add(list, "M 80", 16.2833, -22.9753, 7.3, "Globular Cluster", null, ["NGC 6093"]);
        Add(list, "M 81", 9.9267, 69.0653, 6.9, "Spiral Galaxy", "Bode's Galaxy", ["NGC 3031"]);
        Add(list, "M 82", 9.9317, 69.6797, 8.4, "Irregular Galaxy", "Cigar Galaxy", ["NGC 3034"]);
        Add(list, "M 83", 13.6167, -29.8667, 7.6, "Spiral Galaxy", "Southern Pinwheel", ["NGC 5236"]);
        Add(list, "M 84", 12.4183, 12.8869, 9.1, "Elliptical Galaxy", null, ["NGC 4374"]);
        Add(list, "M 85", 12.4225, 18.1911, 9.1, "Lenticular Galaxy", null, ["NGC 4382"]);
        Add(list, "M 86", 12.4383, 12.9464, 8.9, "Elliptical Galaxy", null, ["NGC 4406"]);
        Add(list, "M 87", 12.5133, 12.3906, 8.6, "Elliptical Galaxy", "Virgo A", ["NGC 4486"]);
        Add(list, "M 88", 12.5333, 14.4203, 9.6, "Spiral Galaxy", null, ["NGC 4501"]);
        Add(list, "M 89", 12.5925, 12.5564, 9.8, "Elliptical Galaxy", null, ["NGC 4552"]);
        Add(list, "M 90", 12.6125, 13.1625, 9.5, "Spiral Galaxy", null, ["NGC 4569"]);
        Add(list, "M 91", 12.5900, 14.4964, 10.2, "Spiral Galaxy", null, ["NGC 4548"]);
        Add(list, "M 92", 17.2856, 43.1364, 6.4, "Globular Cluster", null, ["NGC 6341"]);
        Add(list, "M 93", 7.7442, -23.8667, 6.0, "Open Cluster", null, ["NGC 2447"]);
        Add(list, "M 94", 12.8508, 41.1200, 8.2, "Spiral Galaxy", null, ["NGC 4736"]);
        Add(list, "M 95", 10.7325, 11.7036, 9.7, "Spiral Galaxy", null, ["NGC 3351"]);
        Add(list, "M 96", 10.7817, 11.8200, 9.3, "Spiral Galaxy", null, ["NGC 3368"]);
        Add(list, "M 97", 11.2467, 55.0192, 9.9, "Planetary Nebula", "Owl Nebula", ["NGC 3587"]);
        Add(list, "M 98", 12.2283, 14.9003, 10.1, "Spiral Galaxy", null, ["NGC 4192"]);
        Add(list, "M 99", 12.3117, 14.4164, 9.9, "Spiral Galaxy", null, ["NGC 4254"]);
        Add(list, "M 100", 12.3833, 15.8219, 9.3, "Spiral Galaxy", null, ["NGC 4321"]);
        Add(list, "M 101", 14.0542, 54.3492, 7.9, "Spiral Galaxy", "Pinwheel Galaxy", ["NGC 5457"]);
        Add(list, "M 102", 15.1083, 55.7636, 9.9, "Lenticular Galaxy", "Spindle Galaxy", ["NGC 5866"]);
        Add(list, "M 103", 1.5558, 60.7000, 7.4, "Open Cluster", null, ["NGC 581"]);
        Add(list, "M 104", 12.6667, -11.6228, 8.0, "Spiral Galaxy", "Sombrero Galaxy", ["NGC 4594"]);
        Add(list, "M 105", 10.7975, 12.5817, 9.3, "Elliptical Galaxy", null, ["NGC 3379"]);
        Add(list, "M 106", 12.3158, 47.3042, 8.4, "Spiral Galaxy", null, ["NGC 4258"]);
        Add(list, "M 107", 16.5425, -13.0536, 7.9, "Globular Cluster", null, ["NGC 6171"]);
        Add(list, "M 108", 11.1867, 55.6742, 10.0, "Spiral Galaxy", "Surfboard Galaxy", ["NGC 3556"]);
        Add(list, "M 109", 11.9617, 53.3747, 9.8, "Spiral Galaxy", null, ["NGC 3992"]);
        Add(list, "M 110", 0.6722, 41.6853, 8.5, "Elliptical Galaxy", null, ["NGC 205"]);
    }

    private static void AddCaldwell(List<CatalogObject> list) {
        Add(list, "C 1", 0.5525, 85.3333, 9.5, "Open Cluster", null, ["NGC 188"]);
        Add(list, "C 2", 0.7167, 61.8833, 6.1, "Open Cluster", null, ["NGC 40"]);
        Add(list, "C 3", 1.3717, 58.6000, 9.2, "Planetary Nebula", null, ["NGC 4236"]);
        Add(list, "C 4", 1.5167, 59.4833, 8.1, "Open Cluster", null, ["NGC 7023"]);
        Add(list, "C 6", 6.5567, -23.9667, 4.0, "Open Cluster", null, ["NGC 6543"]);
        Add(list, "C 9", 1.8208, 57.1333, 6.5, "Emission Nebula", "Cave Nebula", ["Sh2-155"]);
        Add(list, "C 10", 1.4108, 58.2333, 7.1, "Open Cluster", null, ["NGC 663"]);
        Add(list, "C 11", 23.3908, 61.2000, 7.4, "Emission Nebula", "Bubble Nebula", ["NGC 7635"]);
        Add(list, "C 12", 22.3400, 58.3833, 6.6, "Star Cloud", null, ["NGC 6946"]);
        Add(list, "C 13", 23.8975, 56.6333, 5.5, "Open Cluster", "Owl Cluster", ["NGC 457"]);
        Add(list, "C 14", 2.3533, 61.3333, 5.0, "Open Cluster", "Double Cluster h", ["NGC 869"]);
        Add(list, "C 15", 12.6292, -7.5833, 9.6, "Emission Nebula", "Blinking Planetary", ["NGC 6826"]);
        Add(list, "C 19", 17.9958, 67.6333, 8.8, "Planetary Nebula", "Cocoon Nebula", ["IC 5146"]);
        Add(list, "C 20", 12.7867, -0.8167, 8.4, "Spiral Galaxy", null, ["NGC 7000"]);
        Add(list, "C 27", 21.0667, 68.2000, 7.7, "Planetary Nebula", "Crescent Nebula", ["NGC 6888"]);
        Add(list, "C 28", 21.8692, 47.2667, 7.4, "Open Cluster", null, ["NGC 752"]);
        Add(list, "C 30", 0.5417, 51.7333, 7.7, "Planetary Nebula", null, ["NGC 7331"]);
        Add(list, "C 31", 0.7167, 61.8833, 9.0, "Emission Nebula", "Flaming Star", ["IC 405"]);
        Add(list, "C 33", 20.1667, -12.5333, 7.0, "Emission Nebula", "East Veil", ["NGC 6992"]);
        Add(list, "C 34", 20.7583, 30.7167, 7.0, "Emission Nebula", "West Veil", ["NGC 6960"]);
        Add(list, "C 39", 12.5333, -26.7500, 9.0, "Spiral Galaxy", "Eskimo Nebula", ["NGC 2392"]);
        Add(list, "C 46", 13.0667, -49.4667, 7.4, "Open Cluster", "Hubble's Variable Nebula", ["NGC 2261"]);
        Add(list, "C 49", 12.7958, -5.8000, 8.3, "Emission Nebula", "Rosette Nebula", ["NGC 2237"]);
        Add(list, "C 55", 13.8758, 56.2167, 9.8, "Irregular Galaxy", null, ["NGC 7009"]);
        Add(list, "C 63", 13.8000, -47.4833, 9.1, "Spiral Galaxy", "Helix Nebula", ["NGC 7293"]);
        Add(list, "C 69", 6.5333, 9.8833, 9.6, "Planetary Nebula", "Bug Nebula", ["NGC 6302"]);

        // Popular NGC not in Messier or Caldwell
        Add(list, "NGC 7000", 20.9725, 44.3167, 4.0, "Emission Nebula", "North America Nebula", []);
        Add(list, "NGC 6960", 20.7583, 30.7167, 7.0, "Supernova Remnant", "Western Veil Nebula", ["C 34"]);
        Add(list, "NGC 6992", 20.9333, 31.7167, 7.0, "Supernova Remnant", "Eastern Veil Nebula", ["C 33"]);
        Add(list, "NGC 2024", 5.6833, -1.8500, 7.0, "Emission Nebula", "Flame Nebula", []);
        Add(list, "NGC 2244", 6.5333, 4.9500, 4.8, "Open Cluster", "Rosette Cluster", []);
        Add(list, "IC 1396", 21.6458, 57.5000, 3.5, "Emission Nebula", "Elephant Trunk Nebula", []);
        Add(list, "IC 434", 5.6825, -2.4500, 7.3, "Emission Nebula", "Horsehead Nebula", []);
        Add(list, "IC 1805", 2.5458, 61.4667, 6.5, "Emission Nebula", "Heart Nebula", []);
        Add(list, "IC 1848", 2.8500, 60.4333, 6.5, "Emission Nebula", "Soul Nebula", []);
        Add(list, "NGC 6888", 20.2000, 38.3500, 7.4, "Emission Nebula", "Crescent Nebula", []);
        Add(list, "NGC 7380", 22.7833, 58.1333, 7.2, "Open Cluster", "Wizard Nebula", []);
        Add(list, "NGC 281", 0.8833, 56.6333, 7.4, "Emission Nebula", "Pacman Nebula", []);
        Add(list, "NGC 1333", 3.4833, 31.3500, 5.6, "Reflection Nebula", null, []);
        Add(list, "NGC 2264", 6.6833, 9.8833, 3.9, "Open Cluster", "Christmas Tree Cluster", []);
        Add(list, "NGC 6334", 17.3375, -35.8167, 5.5, "Emission Nebula", "Cat's Paw Nebula", []);
        Add(list, "NGC 3372", 10.7333, -59.8667, 1.0, "Emission Nebula", "Carina Nebula", []);
        Add(list, "NGC 253", 0.7917, -25.2833, 7.2, "Spiral Galaxy", "Sculptor Galaxy", []);
        Add(list, "NGC 891", 2.3750, 42.3500, 9.9, "Spiral Galaxy", null, []);
        Add(list, "NGC 2903", 9.5375, 21.5167, 9.0, "Spiral Galaxy", null, []);
        Add(list, "NGC 4565", 12.6050, 25.9875, 9.6, "Spiral Galaxy", "Needle Galaxy", []);
        Add(list, "NGC 4631", 12.7033, 32.5417, 9.2, "Spiral Galaxy", "Whale Galaxy", []);
    }

    private static void Add(List<CatalogObject> list, string name, double ra, double dec,
        double magnitude, string type, string? commonName, string[] aliases) {
        list.Add(new CatalogObject {
            Name = name,
            Ra = ra,
            Dec = dec,
            Magnitude = magnitude,
            Type = type,
            CommonName = commonName,
            Aliases = aliases
        });
    }
}

public class CatalogFilter {
    public string? Query { get; set; }
    public string? Type { get; set; }
    public double? MinMagnitude { get; set; }
    public double? MaxMagnitude { get; set; }
    public double? MinDec { get; set; }
    public double? MaxDec { get; set; }
}

public class CatalogObject {
    public string Name { get; set; } = "";
    public double Ra { get; set; }
    public double Dec { get; set; }
    public double Magnitude { get; set; }
    public string Type { get; set; } = "";
    public string? CommonName { get; set; }
    public string[] Aliases { get; set; } = [];

    public string RaFormatted {
        get {
            var h = (int)Ra;
            var m = (int)((Ra - h) * 60);
            var s = ((Ra - h) * 60 - m) * 60;
            return $"{h:D2}h {m:D2}m {s:00.0}s";
        }
    }

    public string DecFormatted {
        get {
            var sign = Dec >= 0 ? "+" : "-";
            var abs = Math.Abs(Dec);
            var d = (int)abs;
            var m = (int)((abs - d) * 60);
            var s = ((abs - d) * 60 - m) * 60;
            return $"{sign}{d}° {m:D2}' {s:00}\"";
        }
    }
}

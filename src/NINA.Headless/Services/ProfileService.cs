using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Headless.Services;

public class ProfileService {
    private static readonly JsonSerializerOptions JsonOpts = new() {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _profileDir;
    private readonly string _activeProfilePath;
    private readonly ILogger<ProfileService> _logger;

    private UserProfile _activeProfile = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    public UserProfile Active => _activeProfile;

    public ProfileService(IConfiguration config, ILogger<ProfileService> logger) {
        _logger = logger;

        var baseDir = config.GetValue("Profiles:Directory",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NINA.Headless", "profiles"))!;

        _profileDir = baseDir;
        _activeProfilePath = Path.Combine(baseDir, "active.json");

        Directory.CreateDirectory(_profileDir);
        Load();
    }

    public List<ProfileSummary> ListProfiles() {
        var files = Directory.GetFiles(_profileDir, "*.json")
            .Where(f => !f.EndsWith("active.json"))
            .ToList();

        var profiles = new List<ProfileSummary>();
        foreach (var file in files) {
            try {
                var json = File.ReadAllText(file);
                var p = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts);
                if (p != null) {
                    profiles.Add(new ProfileSummary {
                        Id = Path.GetFileNameWithoutExtension(file),
                        Name = p.Name,
                        LastModified = File.GetLastWriteTimeUtc(file)
                    });
                }
            } catch { }
        }

        return profiles;
    }

    public void Load() {
        if (!File.Exists(_activeProfilePath)) {
            _activeProfile = new UserProfile { Name = "Default" };
            Save();
            _logger.LogInformation("Created default profile at {Path}", _activeProfilePath);
            return;
        }

        try {
            var json = File.ReadAllText(_activeProfilePath);
            _activeProfile = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts)
                ?? new UserProfile { Name = "Default" };
            _logger.LogInformation("Loaded profile: {Name}", _activeProfile.Name);
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load profile, using defaults");
            _activeProfile = new UserProfile { Name = "Default" };
        }
    }

    public void Save() {
        _saveLock.Wait();
        try {
            var json = JsonSerializer.Serialize(_activeProfile, JsonOpts);
            File.WriteAllText(_activeProfilePath, json);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save profile");
        } finally {
            _saveLock.Release();
        }
    }

    public void SaveAs(string name) {
        var id = SanitizeFileName(name);
        var path = Path.Combine(_profileDir, id + ".json");
        _activeProfile.Name = name;

        try {
            var json = JsonSerializer.Serialize(_activeProfile, JsonOpts);
            File.WriteAllText(path, json);
            File.WriteAllText(_activeProfilePath, json);
            _logger.LogInformation("Profile saved as: {Name} ({Path})", name, path);
        } catch (Exception ex) {
            _logger.LogError(ex, "Failed to save profile as {Name}", name);
        }
    }

    public bool LoadProfile(string id) {
        var path = Path.Combine(_profileDir, id + ".json");
        if (!File.Exists(path)) return false;

        try {
            var json = File.ReadAllText(path);
            _activeProfile = JsonSerializer.Deserialize<UserProfile>(json, JsonOpts)
                ?? new UserProfile { Name = "Default" };
            Save();
            _logger.LogInformation("Switched to profile: {Name}", _activeProfile.Name);
            return true;
        } catch (Exception ex) {
            _logger.LogWarning(ex, "Failed to load profile {Id}", id);
            return false;
        }
    }

    public void UpdateSettings(Action<UserProfile> update) {
        update(_activeProfile);
        Save();
    }

    // ----- Equipment profile (rig) management -----

    /// <summary>The currently-active equipment rig, creating a "Default" rig
    /// from the legacy LastXxx fields if the user has never used this feature
    /// before.</summary>
    public EquipmentProfile ActiveEquipmentProfile {
        get {
            EnsureMigratedToEquipmentProfiles();
            var id = _activeProfile.ActiveEquipmentProfileId;
            return _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id)
                ?? _activeProfile.EquipmentProfiles[0];
        }
    }

    public List<EquipmentProfile> ListEquipmentProfiles() {
        EnsureMigratedToEquipmentProfiles();
        return _activeProfile.EquipmentProfiles.ToList();
    }

    public EquipmentProfile CreateEquipmentProfile(string name) {
        EnsureMigratedToEquipmentProfiles();
        var existing = _activeProfile.EquipmentProfiles
            .FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return existing;

        var rig = new EquipmentProfile { Name = name };
        _activeProfile.EquipmentProfiles.Add(rig);
        Save();
        _logger.LogInformation("Created equipment profile: {Name}", name);
        return rig;
    }

    /// <summary>Save the active rig's current values under a new name without
    /// switching to it.</summary>
    public EquipmentProfile CloneActiveRigAs(string newName) {
        var src = ActiveEquipmentProfile;
        var copy = new EquipmentProfile {
            Name = newName,
            Camera = src.Camera, Telescope = src.Telescope, Focuser = src.Focuser,
            FilterWheel = src.FilterWheel, Rotator = src.Rotator,
            FlatDevice = src.FlatDevice, Dome = src.Dome, Weather = src.Weather,
            CoolerTargetTemperature = src.CoolerTargetTemperature,
            DefaultGain = src.DefaultGain, DefaultOffset = src.DefaultOffset,
            DefaultBinning = src.DefaultBinning,
            FocuserStepSize = src.FocuserStepSize,
            FocuserBacklashSteps = src.FocuserBacklashSteps,
            FocalLengthMm = src.FocalLengthMm,
            PHD2Host = src.PHD2Host, PHD2Port = src.PHD2Port
        };
        _activeProfile.EquipmentProfiles.Add(copy);
        Save();
        return copy;
    }

    public bool UpdateEquipmentProfile(string id, Action<EquipmentProfile> update) {
        EnsureMigratedToEquipmentProfiles();
        var rig = _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id);
        if (rig == null) return false;
        update(rig);
        Save();
        return true;
    }

    public bool RenameEquipmentProfile(string id, string newName) {
        return UpdateEquipmentProfile(id, r => r.Name = newName);
    }

    public bool DeleteEquipmentProfile(string id) {
        EnsureMigratedToEquipmentProfiles();
        if (_activeProfile.EquipmentProfiles.Count <= 1) return false; // never delete the last one
        var rig = _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id);
        if (rig == null) return false;
        _activeProfile.EquipmentProfiles.Remove(rig);
        if (_activeProfile.ActiveEquipmentProfileId == id)
            _activeProfile.ActiveEquipmentProfileId = _activeProfile.EquipmentProfiles[0].Id;
        Save();
        return true;
    }

    public bool ActivateEquipmentProfile(string id) {
        EnsureMigratedToEquipmentProfiles();
        if (!_activeProfile.EquipmentProfiles.Any(e => e.Id == id)) return false;
        _activeProfile.ActiveEquipmentProfileId = id;
        Save();
        _logger.LogInformation("Activated equipment profile {Id}", id);
        return true;
    }

    /// <summary>On first run (or upgrade from a pre-rig profile), create a
    /// "Default" rig populated from the legacy LastXxx fields.</summary>
    private void EnsureMigratedToEquipmentProfiles() {
        if (_activeProfile.EquipmentProfiles.Count > 0) return;
        var rig = new EquipmentProfile {
            Name = "Default",
            Camera = _activeProfile.LastCamera,
            Telescope = _activeProfile.LastTelescope,
            Focuser = _activeProfile.LastFocuser,
            FilterWheel = _activeProfile.LastFilterWheel,
            FocalLengthMm = _activeProfile.FocalLengthMm,
            DefaultGain = _activeProfile.DefaultGain,
            DefaultBinning = _activeProfile.DefaultBinning
        };
        _activeProfile.EquipmentProfiles.Add(rig);
        _activeProfile.ActiveEquipmentProfileId = rig.Id;
        Save();
        _logger.LogInformation("Migrated legacy equipment selection into Default rig");
    }

    private static string SanitizeFileName(string name) {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray())
            .Replace(' ', '_').ToLowerInvariant();
    }
}

public class UserProfile {
    public string Name { get; set; } = "Default";

    // Observatory location
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public double Altitude { get; set; }

    // Camera optics (fallback only — live sensor dims come from the camera)
    public double SensorWidthMm { get; set; } = 23.5;
    public double SensorHeightMm { get; set; } = 15.7;
    public double FocalLengthMm { get; set; } = 478;
    public int SensorPixelsX { get; set; } = 6248;
    public int SensorPixelsY { get; set; } = 4176;

    // Default imaging settings
    public double DefaultExposure { get; set; } = 30;
    public int DefaultGain { get; set; } = 100;
    public int DefaultBinning { get; set; } = 1;

    // INDI connection
    public string IndiHost { get; set; } = "localhost";
    public int IndiPort { get; set; } = 7624;

    // Legacy single-rig equipment selection (still serialised for
    // backward-compat; new code uses EquipmentProfiles below).
    public string? LastCamera { get; set; }
    public string? LastTelescope { get; set; }
    public string? LastFocuser { get; set; }
    public string? LastFilterWheel { get; set; }

    // Multi-rig support: a list of named equipment sets the user can switch
    // between. Loaded on first run by migrating the legacy LastXxx fields
    // into a "Default" rig.
    public List<EquipmentProfile> EquipmentProfiles { get; set; } = new();
    public string? ActiveEquipmentProfileId { get; set; }

    // Plate solver
    public string? AstapPath { get; set; }
    public double SolveToleranceArcsec { get; set; } = 30;

    // Image output
    public string ImageOutputDir { get; set; } = "";
    public string ImageNamePattern { get; set; } = "{target}_{filter}_{exposure}s_{date}_{seq}";
    public string ImageFormat { get; set; } = "fits";
}

/// <summary>
/// A named equipment set (a "rig"). The user can save the device-name +
/// per-rig preference pair for any combination of equipment, then switch
/// rigs in one click without re-selecting every device. Common use cases:
///   - "Backyard SCT" vs "Travel APO" vs "Remote site setup"
///   - Different cameras with different optimal cooler temps
///   - Different focuser positions / step sizes per OTA
/// </summary>
public class EquipmentProfile {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Default";

    // Device selections — INDI device names as returned by getProperties
    public string? Camera { get; set; }
    public string? Telescope { get; set; }
    public string? Focuser { get; set; }
    public string? FilterWheel { get; set; }
    public string? Rotator { get; set; }
    public string? FlatDevice { get; set; }
    public string? Dome { get; set; }
    public string? Weather { get; set; }

    // Per-rig defaults
    public double CoolerTargetTemperature { get; set; } = -10;
    public int DefaultGain { get; set; } = 100;
    public int DefaultOffset { get; set; } = 50;
    public int DefaultBinning { get; set; } = 1;
    public int FocuserStepSize { get; set; } = 50;
    public int FocuserBacklashSteps { get; set; }

    // Optics specific to this rig (cameras share the OTA's focal length when
    // physically swapped, but you may have multiple OTAs/reducers).
    public double FocalLengthMm { get; set; } = 478;

    // Per-rig PHD2 settings
    public string PHD2Host { get; set; } = "localhost";
    public int PHD2Port { get; set; } = 4400;
}

public class ProfileSummary {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastModified { get; set; }
}

using System.Text.Json;
using System.Text.Json.Serialization;

namespace NINA.Polaris.Services;

/// <summary>
/// Central runtime configuration service. Owns the active
/// <see cref="UserProfile"/> and the per-rig <c>EquipmentProfile</c>
/// list, persists to JSON under <c>{LocalAppData}/NINA.Polaris/profiles/</c>,
/// and raises <see cref="EquipmentProfileActivated"/> so dependent
/// services (PHD2ProfileSyncService, LiveStackTriggersService, the
/// meridian-flip orchestrator) can reconfigure themselves when the
/// user switches rigs.
///
/// All mutations go through a save-lock <see cref="SemaphoreSlim"/>
/// so concurrent endpoint writes don't tear the JSON file. Reads
/// return the current snapshot directly — callers should not mutate
/// the returned record; the profile is replaced wholesale on save,
/// not edited in place.
/// </summary>
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
                "NINA.Polaris", "profiles"))!;

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
            Camera = src.Camera, CameraDriver = src.CameraDriver,
            Telescope = src.Telescope, TelescopeDriver = src.TelescopeDriver,
            Focuser = src.Focuser,
            FilterWheel = src.FilterWheel, Rotator = src.Rotator,
            FlatDevice = src.FlatDevice, Dome = src.Dome, Weather = src.Weather,
            CoolerTargetTemperature = src.CoolerTargetTemperature,
            DefaultGain = src.DefaultGain, DefaultOffset = src.DefaultOffset,
            DefaultBinning = src.DefaultBinning,
            FocuserStepSize = src.FocuserStepSize,
            FocuserBacklashSteps = src.FocuserBacklashSteps,
            FocalLengthMm = src.FocalLengthMm,
            ApertureMm = src.ApertureMm,
            TelescopeBrand = src.TelescopeBrand,
            TelescopeModel = src.TelescopeModel,
            AccessoryType = src.AccessoryType,
            AccessoryModel = src.AccessoryModel,
            AccessoryFactor = src.AccessoryFactor,
            RequiredBackspacingMm = src.RequiredBackspacingMm,
            GuiderFocalLengthMm = src.GuiderFocalLengthMm,
            PHD2Host = src.PHD2Host, PHD2Port = src.PHD2Port,
            // PHD2 deep-integration fields (cloned rig starts un-matched —
            // it will run its own first-time profile lookup the first time
            // it activates).
            PHD2ProfileId = null,
            PHD2AlgoPreset = src.PHD2AlgoPreset,
            PHD2CalibrationStepMsOverride = src.PHD2CalibrationStepMsOverride,
            PHD2AutoSyncOnRigSwitch = src.PHD2AutoSyncOnRigSwitch,
            PHD2CustomAlgoParams = new Dictionary<string, double>(src.PHD2CustomAlgoParams),
            FilterOffsets = new Dictionary<string, int>(src.FilterOffsets),
            // Live-stack triggers — clone the whole shape so the new rig
            // gets the same refocus/recenter policy as the source. Reset
            // counters live on the orchestrator, not the settings.
            LiveStackTriggers = new LiveStackTriggers {
                RefocusEnabled = src.LiveStackTriggers.RefocusEnabled,
                RefocusEveryNFrames = src.LiveStackTriggers.RefocusEveryNFrames,
                RefocusEveryMinutes = src.LiveStackTriggers.RefocusEveryMinutes,
                RefocusTempDeltaC = src.LiveStackTriggers.RefocusTempDeltaC,
                RefocusHfrIncreasePercent = src.LiveStackTriggers.RefocusHfrIncreasePercent,
                RefocusRequest = src.LiveStackTriggers.RefocusRequest,
                RecenterEnabled = src.LiveStackTriggers.RecenterEnabled,
                RecenterEveryNFrames = src.LiveStackTriggers.RecenterEveryNFrames,
                RecenterEveryMinutes = src.LiveStackTriggers.RecenterEveryMinutes,
                RecenterDriftArcsec = src.LiveStackTriggers.RecenterDriftArcsec,
                RecenterToleranceArcsec = src.LiveStackTriggers.RecenterToleranceArcsec
            }
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

    /// <summary>
    /// Fired after a rig is successfully activated and persisted.
    /// PHD2ProfileSyncService subscribes here to push the matching PHD2
    /// profile + apply algo presets when AutoSyncOnRigSwitch is true.
    /// Event handlers run on the calling thread — keep them fast (do
    /// long work via Task.Run / fire-and-forget).
    /// </summary>
    public event Action<EquipmentProfile>? EquipmentProfileActivated;

    public bool ActivateEquipmentProfile(string id) {
        EnsureMigratedToEquipmentProfiles();
        var rig = _activeProfile.EquipmentProfiles.FirstOrDefault(e => e.Id == id);
        if (rig == null) return false;
        _activeProfile.ActiveEquipmentProfileId = id;
        Save();
        _logger.LogInformation("Activated equipment profile {Id}", id);
        try { EquipmentProfileActivated?.Invoke(rig); }
        catch (Exception ex) { _logger.LogWarning(ex, "EquipmentProfileActivated handler threw"); }
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

    // External post-processing tools (Siril + GraXpert). Empty/null
    // means "auto-detect" via BinaryLocator; set explicitly to
    // override the default path search.
    public string? SirilPath { get; set; }
    public string? SirilScriptsDir { get; set; }
    public string? GraXpertPath { get; set; }
    public double GraXpertBgeSmoothing { get; set; } = 1.0;
    public string GraXpertBgeCorrection { get; set; } = "Subtraction";
    public double GraXpertDeconStrength { get; set; } = 0.5;
    public double GraXpertDeconPsfSize { get; set; } = 4.0;
    public double GraXpertDenoiseStrength { get; set; } = 0.5;

    // Image output
    public string ImageOutputDir { get; set; } = "";
    public string ImageNamePattern { get; set; } = "{target}_{filter}_{exposure}s_{date}_{seq}";
    public string ImageFormat { get; set; } = "fits";

    // PHD2 lifecycle preferences (app-global, not per-rig). When true the
    // PHD2AutoStartService launches PHD2 (and connects the JSON-RPC client)
    // as soon as the Headless app starts.
    public bool PHD2AutoStart { get; set; } = false;

    // SIM-2: built-in equipment simulator (indi_simulator_* on Linux,
    // Alpaca Omni Simulator on Windows). When SimulatorAutoStart is
    // true, SimulatorAutoStartService launches the configured stack
    // ~3s after Polaris boots so the user doesn't need to babysit a
    // separate terminal. Toggleable from the Settings tab. Defaults
    // are conservative: off, sensible 4-device list, INDI default port.
    public bool SimulatorAutoStart { get; set; } = false;
    public List<string> SimulatorDevices { get; set; }
        = new() { "ccd", "telescope", "focus", "wheel" };
    // INDI default; AscomSimulatorBackend overrides to 32323 (Alpaca
    // Omni Sim default) when the active backend is "ascom". UI saves
    // whatever the user picked, the backend uses its own default
    // when the saved value doesn't make sense (0 / null).
    public int SimulatorPort { get; set; } = 7624;

    /// <summary>
    /// Which sequencer to surface as the default in the UI. The Simple
    /// Sequencer (legacy, A4-era) is a flat list of items; the Advanced
    /// Sequencer (Phase C) is a tree with containers, conditions, and
    /// triggers. Both run side-by-side; this flag only picks which tab
    /// the UI lands on first.
    /// </summary>
    public bool PreferAdvancedSequencer { get; set; } = false;
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

    // Device selections — INDI device names as returned by getProperties.
    // Camera is special: it accepts multiple backend kinds via
    // CameraDriver below. The Camera field carries the driver-specific
    // device id (INDI name, or vendor SDK serial number, etc.); for
    // legacy profiles that pre-date CameraDriver it's assumed to be an
    // INDI device name.
    public string? Camera { get; set; }
    /// <summary>Camera backend kind. One of: <c>indi</c>, <c>alpaca</c>,
    /// <c>canon-edsdk</c>, <c>nikon-sdk</c>, <c>sony-sdk</c>. Defaults
    /// to <c>indi</c> for backward compatibility with profiles created
    /// before this field existed.</summary>
    public string CameraDriver { get; set; } = "indi";
    public string? Telescope { get; set; }
    /// <summary>Mount backend kind. One of: <c>indi</c> (default, covers
    /// every mount the running indiserver exposes), <c>alpaca</c>,
    /// <c>synscan-wifi</c> (Sky-Watcher UDP, planned), <c>nexstar-wifi</c>
    /// (Celestron TCP, planned), <c>lx200-tcp</c> (Meade-compatible TCP,
    /// planned). Defaults to <c>indi</c> for backward compatibility.</summary>
    public string TelescopeDriver { get; set; } = "indi";
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

    // Optics specific to this rig. FocalLengthMm is the *effective*
    // focal length used everywhere downstream (FOV calc, FITS
    // FOCALLEN header, mosaic planner, etc.) — for OTAs with a
    // reducer / Barlow attached this is the native focal length
    // multiplied by AccessoryFactor. The picker in the Manage Rigs
    // modal computes it; the user can also override manually.
    public double FocalLengthMm { get; set; } = 478;
    /// <summary>OTA aperture in millimetres. Auto-filled from the
    /// telescopes.json catalogue when the user picks a model; can
    /// be set manually for off-catalogue scopes. Drives the FOV
    /// calculator's f-ratio readout.</summary>
    public double ApertureMm { get; set; }
    /// <summary>Telescope brand selected in the picker (e.g.
    /// "Celestron"). Empty string when the user filled the optics
    /// fields manually.</summary>
    public string TelescopeBrand { get; set; } = "";
    /// <summary>Telescope model selected in the picker (e.g.
    /// "EdgeHD 8"). Empty when manual.</summary>
    public string TelescopeModel { get; set; } = "";
    /// <summary>Optional accessory in the optical train. One of
    /// "reducer", "barlow", "flattener", or empty when none.</summary>
    public string AccessoryType { get; set; } = "";
    /// <summary>Accessory brand + model string (e.g. "Celestron
    /// 0.7x Reducer Lens (EdgeHD)"). Empty when none.</summary>
    public string AccessoryModel { get; set; } = "";
    /// <summary>Focal-length multiplier the accessory applies.
    /// 1.0 when no accessory; 0.7 for a typical reducer; 2.0 for
    /// a 2× Barlow; etc. Effective focal length =
    /// nativeFocalLength × AccessoryFactor.</summary>
    public double AccessoryFactor { get; set; } = 1.0;
    /// <summary>Back-focus (camera-side spacing) required by the
    /// current OTA + accessory combination, in millimetres.
    /// Surfaced as a reminder in the rig editor — wrong backspacing
    /// is the most common reason flatteners produce elongated
    /// stars in the corners. Null when the OTA / accessory doesn't
    /// publish a value.</summary>
    public double? RequiredBackspacingMm { get; set; }

    // Focal length of the guide scope. Used for record-keeping and as a
    // sanity-check reference against PHD2's reported pixel scale. PHD2 itself
    // computes its pixel scale from its own configuration; we just track what
    // the user *thinks* the guide scope is.
    public double GuiderFocalLengthMm { get; set; } = 200;

    /// <summary>
    /// Guide-scope aperture. Used for record-keeping and as the
    /// denominator of the guidescope f-ratio displayed in the
    /// Guidescope card on the RIGS tab. Default 50 mm matches the
    /// most common 50 mm × 200 mm finder-guider combo. Set to 0 to
    /// suppress the f-ratio display.
    /// </summary>
    public double GuiderApertureMm { get; set; } = 50;

    /// <summary>Brand of the guide telescope. Optional, free-form.</summary>
    public string? GuideTelescopeBrand { get; set; }

    /// <summary>Model of the guide telescope. Optional, free-form.</summary>
    public string? GuideTelescopeModel { get; set; }

    // Per-rig PHD2 settings
    public string PHD2Host { get; set; } = "localhost";
    public int PHD2Port { get; set; } = 4400;

    // ----- PHD2 deep integration (xpra + RPC orchestration) -----

    /// <summary>
    /// Cached PHD2 profile id matched by name to this rig. Set the first
    /// time PHD2ProfileSyncService finds a PHD2 profile whose name equals
    /// this rig's Name. Null = not yet matched or PHD2 profile missing.
    /// Don't rely on the value across PHD2 reinstalls — call
    /// PHD2ProfileSyncService.SyncRigToProfileAsync to refresh.
    /// </summary>
    public int? PHD2ProfileId { get; set; }

    /// <summary>
    /// Guide-algorithm preset Polaris applies on rig activation. One of
    /// "Default" / "Reactive" / "Smooth" / "Custom" — see PHD2AlgoPresets.
    /// "Custom" means use the per-rig PHD2CustomAlgoParams bag.
    /// </summary>
    public string PHD2AlgoPreset { get; set; } = "Default";

    /// <summary>
    /// Per-rig override for PHD2 calibration step (ms). Null = let the
    /// orchestrator auto-compute from pixel scale + guide rate.
    /// </summary>
    public int? PHD2CalibrationStepMsOverride { get; set; }

    /// <summary>
    /// When true (default), activating this rig automatically asks
    /// PHD2ProfileSyncService to switch PHD2 to the matching profile.
    /// Set false if the user wants manual control of PHD2 profile switching.
    /// </summary>
    public bool PHD2AutoSyncOnRigSwitch { get; set; } = true;

    /// <summary>
    /// Free-form algorithm-parameter overrides for the "Custom" preset.
    /// Keys are in the format "axis:paramName" (e.g. "ra:Hysteresis"),
    /// values are the raw doubles pushed via set_algo_param.
    /// </summary>
    public Dictionary<string, double> PHD2CustomAlgoParams { get; set; } = new();

    /// <summary>
    /// Per-filter focuser offset in steps, relative to the rig's reference
    /// filter (typically the L filter). Consumed by
    /// <c>MoveToFilterOffsetInstruction</c>: when an instruction names a filter
    /// here, it moves the focuser to <c>currentPos + offset</c>. Filters not
    /// in the table are treated as 0.
    /// </summary>
    public Dictionary<string, int> FilterOffsets { get; set; } = new();

    /// <summary>
    /// Auto re-focus + re-center policy applied during live stacking
    /// (LSTR-3). Persisted per-rig because thermal characteristics +
    /// guiding precision vary by setup. Default = all triggers disabled.
    /// </summary>
    public LiveStackTriggers LiveStackTriggers { get; set; } = new();

    /// <summary>CLST-7: where live-stacking math runs.
    /// <list type="bullet">
    /// <item><b>auto</b> (default) — server flips to MetricsOnly
    /// when a WASM-capable client connects, back to Full otherwise.</item>
    /// <item><b>server</b> — force server-side accumulator regardless
    /// of clients. Use when you want a Pi to be the canonical source
    /// for multiple browsers, or when WASM is slow on the client.</item>
    /// <item><b>client</b> — force MetricsOnly. Useful for testing the
    /// WASM path, or to free Pi CPU even if no client is currently
    /// hooked up (the next one that connects will pick up the stack
    /// from frame 1 on its side).</item>
    /// </list>
    /// Stored per-rig because the trade-off depends on the host:
    /// Pi 2/3 → client; Pi 5 / mini-PC → either works.</summary>
    public string LiveStackComputeMode { get; set; } = "auto";
}

public class ProfileSummary {
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastModified { get; set; }
}

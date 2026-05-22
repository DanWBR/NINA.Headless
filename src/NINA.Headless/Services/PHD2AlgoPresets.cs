namespace NINA.Headless.Services;

/// <summary>
/// Curated PHD2 guide-algorithm parameter presets. Each preset is a set
/// of (axis, paramName, value) triples we push via PHD2Client.SetAlgoParamAsync.
///
/// Values mirror PHD2 community recommendations + PHD2 stock defaults:
/// - Default: PHD2's out-of-the-box hysteresis (RA) + resist-switch (DEC)
///   defaults. Balanced for most setups.
/// - Reactive: higher aggressiveness, lower hysteresis, smaller min-move.
///   Better for short focal lengths, good seeing, fast mounts. Risk: overshoot.
/// - Smooth: gentler corrections, larger hysteresis + min-move. For long
///   focal lengths or windy/poor seeing where chasing seeing is the enemy.
///
/// Param surface depends on which algorithm the user has selected in
/// PHD2 Brain (Hysteresis vs PPEC vs Lowpass etc.) — we apply by name and
/// silently skip any param the current algorithm doesn't expose. That
/// keeps presets safe even when the user has overridden algorithms.
/// </summary>
public static class PHD2AlgoPresets {
    /// <summary>One concrete parameter setting to push.</summary>
    public sealed record AlgoParam(string Axis, string Name, double Value);

    /// <summary>A named bundle of param settings.</summary>
    public sealed record Preset(string Name, string Description, IReadOnlyList<AlgoParam> Params);

    /// <summary>Sentinel value persisted on a rig when the user has edited
    /// individual knobs in the Advanced UI rather than picking a built-in.</summary>
    public const string CustomPresetName = "Custom";

    /// <summary>
    /// Built-in presets, indexed by name. Custom is not in this list —
    /// callers handle it explicitly by reading rig.PHD2CustomAlgoParams.
    /// </summary>
    public static readonly IReadOnlyDictionary<string, Preset> Builtin =
        new Dictionary<string, Preset>(StringComparer.OrdinalIgnoreCase) {
            ["Default"] = new Preset(
                "Default",
                "PHD2 stock defaults. Balanced for most setups.",
                new[] {
                    new AlgoParam("ra",  "Hysteresis",        0.10),
                    new AlgoParam("ra",  "Aggressiveness",    0.70),
                    new AlgoParam("ra",  "MinMove",           0.15),
                    new AlgoParam("dec", "Aggressiveness",    0.65),
                    new AlgoParam("dec", "MinMove",           0.15),
                    new AlgoParam("dec", "FastSwitch",        1.0),  // 1 = true
                }),
            ["Reactive"] = new Preset(
                "Reactive",
                "Higher aggressiveness, lower hysteresis. For short focal "
                + "lengths, good seeing, fast mounts. Risk: overshoot.",
                new[] {
                    new AlgoParam("ra",  "Hysteresis",        0.05),
                    new AlgoParam("ra",  "Aggressiveness",    0.90),
                    new AlgoParam("ra",  "MinMove",           0.10),
                    new AlgoParam("dec", "Aggressiveness",    0.80),
                    new AlgoParam("dec", "MinMove",           0.10),
                    new AlgoParam("dec", "FastSwitch",        1.0),
                }),
            ["Smooth"] = new Preset(
                "Smooth",
                "Gentler corrections, higher hysteresis + min-move. For long "
                + "focal lengths or windy/poor seeing.",
                new[] {
                    new AlgoParam("ra",  "Hysteresis",        0.25),
                    new AlgoParam("ra",  "Aggressiveness",    0.50),
                    new AlgoParam("ra",  "MinMove",           0.20),
                    new AlgoParam("dec", "Aggressiveness",    0.45),
                    new AlgoParam("dec", "MinMove",           0.20),
                    new AlgoParam("dec", "FastSwitch",        0.0),  // 0 = false
                }),
        };

    /// <summary>Names of built-in presets in display order.</summary>
    public static IReadOnlyList<string> BuiltinNames => new[] { "Default", "Reactive", "Smooth" };

    /// <summary>
    /// Look up a built-in preset by name (case-insensitive). Returns null
    /// for unknown names including "Custom".
    /// </summary>
    public static Preset? GetBuiltin(string name) =>
        Builtin.TryGetValue(name, out var p) ? p : null;
}

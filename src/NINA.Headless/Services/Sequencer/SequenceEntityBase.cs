using System.Text.Json.Serialization;

namespace NINA.Headless.Services.Sequencer;

/// <summary>
/// Convenience base class — most entities only override <c>Type</c>,
/// <c>ExecuteAsync</c>, and optionally <c>Validate</c>.
/// </summary>
public abstract class SequenceEntityBase : ISequenceEntity {
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonIgnore] public abstract string Type { get; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }

    [JsonIgnore] public SequenceEntityStatus Status { get; set; } = SequenceEntityStatus.Idle;
    [JsonIgnore] public string? Error { get; set; }
    [JsonIgnore] public DateTime? StartedAt { get; set; }
    [JsonIgnore] public DateTime? FinishedAt { get; set; }

    public virtual IReadOnlyList<string> Validate() => Array.Empty<string>();

    public abstract Task ExecuteAsync(SequenceContext ctx, CancellationToken ct);

    /// <summary>
    /// Reset transient state in-place. Called by the engine before every run
    /// so the tree shows a clean slate even if it was edited mid-run.
    /// </summary>
    public virtual void ResetRuntimeState() {
        Status = SequenceEntityStatus.Idle;
        Error = null;
        StartedAt = null;
        FinishedAt = null;
    }
}

/// <summary>
/// Base for things that aggregate child entities. Holds the child list and a
/// list of triggers that are polled before each child step.
///
/// Subclasses control HOW children are executed (sequential, parallel, with a
/// preamble like a DSO target slew, etc).
/// </summary>
public abstract class SequenceContainer : SequenceEntityBase {
    public List<ISequenceEntity> Items { get; set; } = new();
    public List<SequenceTrigger> Triggers { get; set; } = new();
    public List<SequenceCondition> Conditions { get; set; } = new();

    /// <summary>If true the container loops until all conditions stop returning true.</summary>
    public bool IsLoop { get; set; } = false;

    public override IReadOnlyList<string> Validate() {
        var errors = new List<string>();
        for (int i = 0; i < Items.Count; i++) {
            foreach (var e in Items[i].Validate())
                errors.Add($"[{Name}/{Items[i].Name ?? "#" + i}] {e}");
        }
        foreach (var t in Triggers) {
            foreach (var e in t.Validate()) errors.Add($"[{Name}/trigger:{t.Name}] {e}");
        }
        foreach (var c in Conditions) {
            foreach (var e in c.Validate()) errors.Add($"[{Name}/condition:{c.Name}] {e}");
        }
        return errors;
    }

    public override void ResetRuntimeState() {
        base.ResetRuntimeState();
        foreach (var item in Items)
            if (item is SequenceEntityBase b) b.ResetRuntimeState();
        foreach (var t in Triggers) t.ResetRuntimeState();
        foreach (var c in Conditions) c.ResetRuntimeState();
    }

    /// <summary>
    /// Walks <see cref="Triggers"/>, asking each whether it wants to fire now;
    /// runs the trigger's action body if so. Used by subclasses between
    /// child steps.
    /// </summary>
    protected async Task EvaluateTriggersAsync(SequenceContext ctx, CancellationToken ct) {
        foreach (var trigger in Triggers) {
            if (ctx.AbortRequested) return;
            try {
                if (await trigger.ShouldFireAsync(ctx, ct)) {
                    trigger.Status = SequenceEntityStatus.Running;
                    trigger.StartedAt = DateTime.UtcNow;
                    try {
                        await trigger.ExecuteAsync(ctx, ct);
                        trigger.Status = SequenceEntityStatus.Completed;
                    } catch (Exception ex) {
                        trigger.Status = SequenceEntityStatus.Failed;
                        trigger.Error = ex.Message;
                        ctx.Logger.LogWarning(ex, "Trigger {Name} crashed", trigger.Name);
                    } finally {
                        trigger.FinishedAt = DateTime.UtcNow;
                    }
                }
            } catch (OperationCanceledException) { throw; }
        }
    }

    /// <summary>True while every condition's <c>StillTrueAsync</c> returns true.</summary>
    protected async Task<bool> AllConditionsHoldAsync(SequenceContext ctx, CancellationToken ct) {
        foreach (var c in Conditions) {
            if (!await c.StillTrueAsync(ctx, ct)) return false;
        }
        return true;
    }
}

/// <summary>Atomic action — does one thing and returns.</summary>
public abstract class SequenceInstruction : SequenceEntityBase {
    // Marker base class. Subclasses live in Sequencer/Instructions/*.
}

/// <summary>Boolean predicate used by containers / loops to decide whether to keep going.</summary>
public abstract class SequenceCondition : SequenceEntityBase {
    /// <summary>Returns true while the loop should keep running.</summary>
    public abstract Task<bool> StillTrueAsync(SequenceContext ctx, CancellationToken ct);

    /// <summary>
    /// Conditions don't execute as standalone steps — the container consults
    /// them. Implement no-op here so subclasses don't have to.
    /// </summary>
    public override Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>
/// Event-based hook polled between child steps. <see cref="ShouldFireAsync"/>
/// decides; <see cref="ExecuteAsync"/> runs the side-effect (auto-focus,
/// dither, meridian flip, …).
/// </summary>
public abstract class SequenceTrigger : SequenceEntityBase {
    public abstract Task<bool> ShouldFireAsync(SequenceContext ctx, CancellationToken ct);
}

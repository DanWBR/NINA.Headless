namespace NINA.Headless.Services.Sequencer;

/// <summary>
/// Anything that can be executed inside the Advanced Sequencer tree:
/// containers, instructions, conditions, triggers. The tree is a directed
/// acyclic graph in practice — containers reference their child entities,
/// and the engine walks the tree depth-first by default.
///
/// All entities carry a stable <see cref="Id"/> so the UI can refer to
/// them across edits, and a mutable <see cref="Status"/> for live tree
/// rendering during execution.
/// </summary>
public interface ISequenceEntity {
    /// <summary>Stable identifier (assigned on creation, persisted in JSON).</summary>
    string Id { get; }

    /// <summary>Discriminator used by the polymorphic JSON serializer.</summary>
    string Type { get; }

    /// <summary>Human-readable label shown in the tree editor.</summary>
    string Name { get; set; }

    /// <summary>Optional free-form note (one-liner shown in tooltips).</summary>
    string? Description { get; set; }

    /// <summary>Live execution state for the UI.</summary>
    SequenceEntityStatus Status { get; set; }

    /// <summary>Set to the last exception's message when <see cref="Status"/> = Failed.</summary>
    string? Error { get; set; }

    /// <summary>Started timestamp for elapsed-time display in the UI; null when Idle.</summary>
    DateTime? StartedAt { get; set; }

    /// <summary>Finished timestamp for elapsed-time display in the UI; null when Idle/Running.</summary>
    DateTime? FinishedAt { get; set; }

    /// <summary>
    /// Static validation: returns null when the entity is well-formed,
    /// or a list of human-readable problems. Used by the tree editor to
    /// gate "Start" and show inline warnings.
    /// </summary>
    IReadOnlyList<string> Validate();

    /// <summary>
    /// Execute this entity. Containers walk their children; instructions
    /// perform their action; conditions are evaluated, not executed directly
    /// (containers consult them). The engine ensures <see cref="Status"/>,
    /// <see cref="StartedAt"/>, <see cref="FinishedAt"/>, and <see cref="Error"/>
    /// are populated around the call.
    /// </summary>
    Task ExecuteAsync(SequenceContext ctx, CancellationToken ct);
}

public enum SequenceEntityStatus {
    Idle,
    Running,
    Completed,
    Failed,
    Skipped
}

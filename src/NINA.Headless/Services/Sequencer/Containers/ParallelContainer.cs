namespace NINA.Headless.Services.Sequencer.Containers;

/// <summary>
/// Runs all children concurrently and waits for every one to complete.
/// Triggers + conditions don't make as much sense here so we evaluate
/// triggers only at start. If any child throws, the others get the
/// shared CancellationToken and are expected to wind down promptly.
/// </summary>
public class ParallelContainer : SequenceContainer {
    public override string Type => "Parallel";

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        await EvaluateTriggersAsync(ctx, ct);
        if (ctx.AbortRequested || Items.Count == 0) return;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var tasks = Items.Select(async item => {
            if (item is SequenceEntityBase b) b.ResetRuntimeState();
            item.Status = SequenceEntityStatus.Running;
            item.StartedAt = DateTime.UtcNow;
            try {
                await item.ExecuteAsync(ctx, linked.Token);
                item.Status = SequenceEntityStatus.Completed;
            } catch (OperationCanceledException) {
                item.Status = SequenceEntityStatus.Skipped;
                throw;
            } catch (Exception ex) {
                item.Status = SequenceEntityStatus.Failed;
                item.Error = ex.Message;
                ctx.Logger.LogWarning(ex, "Parallel child {Name} failed", item.Name);
                linked.Cancel(); // make siblings hang up
                throw;
            } finally {
                item.FinishedAt = DateTime.UtcNow;
            }
        }).ToArray();

        try {
            await Task.WhenAll(tasks);
        } catch {
            // First failure already logged on the child; bubble out so the
            // parent container sees this parallel block as failed.
            throw;
        }
    }
}

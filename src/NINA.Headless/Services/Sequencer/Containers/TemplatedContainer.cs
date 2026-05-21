namespace NINA.Headless.Services.Sequencer.Containers;

/// <summary>
/// A pass-through container backed by a saved template name. At load time
/// the engine substitutes the template's contents into <see cref="Items"/>;
/// at run time it behaves exactly like a sequential container.
///
/// Useful for re-using "standard preamble" blocks (e.g. a "Twilight flats"
/// block, a "Cool camera to -10C and wait" block) across many sequences
/// without copy-pasting.
/// </summary>
public class TemplatedContainer : SequenceContainer {
    public override string Type => "Templated";

    /// <summary>Name of the saved template to load. See <c>SequenceTemplateStore</c>.</summary>
    public string TemplateName { get; set; } = "";

    public override IReadOnlyList<string> Validate() {
        var errors = new List<string>(base.Validate());
        if (string.IsNullOrWhiteSpace(TemplateName))
            errors.Add("TemplateName is empty");
        return errors;
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        // Same body as SequentialContainer once the engine has hydrated us.
        do {
            for (int i = 0; i < Items.Count; i++) {
                if (ctx.AbortRequested) return;
                ct.ThrowIfCancellationRequested();
                await EvaluateTriggersAsync(ctx, ct);
                if (ctx.AbortRequested) return;

                var item = Items[i];
                if (item is SequenceEntityBase b) b.ResetRuntimeState();
                item.Status = SequenceEntityStatus.Running;
                item.StartedAt = DateTime.UtcNow;
                try {
                    await item.ExecuteAsync(ctx, ct);
                    item.Status = SequenceEntityStatus.Completed;
                } catch (OperationCanceledException) {
                    item.Status = SequenceEntityStatus.Skipped;
                    throw;
                } catch (Exception ex) {
                    item.Status = SequenceEntityStatus.Failed;
                    item.Error = ex.Message;
                    throw;
                } finally {
                    item.FinishedAt = DateTime.UtcNow;
                }
            }
        } while (IsLoop && !ctx.AbortRequested && await AllConditionsHoldAsync(ctx, ct));
    }
}

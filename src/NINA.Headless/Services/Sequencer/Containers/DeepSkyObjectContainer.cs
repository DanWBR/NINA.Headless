namespace NINA.Headless.Services.Sequencer.Containers;

/// <summary>
/// A sequential container scoped to a specific deep-sky target. Slews and
/// plate-solve-centers on the target before running the children, so the
/// children can take exposures, do filter changes, etc. without re-pointing.
///
/// Target rotation (PA) is record-keeping only today — when a rotator is
/// added in a later release the container will rotate to <see cref="Rotation"/>
/// after centering.
/// </summary>
public class DeepSkyObjectContainer : SequenceContainer {
    public override string Type => "DeepSkyObject";

    /// <summary>Target display name (free text — "M31", "NGC 7000 west panel").</summary>
    public string Target { get; set; } = "";

    /// <summary>J2000 right ascension in decimal hours.</summary>
    public double RaHours { get; set; }

    /// <summary>J2000 declination in decimal degrees.</summary>
    public double DecDeg { get; set; }

    /// <summary>Target rotation angle (PA) in degrees. 0 = north up.</summary>
    public double Rotation { get; set; }

    /// <summary>
    /// If true the container performs Slew &amp; Center via plate-solving
    /// before running children. If false it assumes the mount is already
    /// pointed at the target (useful for re-runs after a flip).
    /// </summary>
    public bool CenterOnStart { get; set; } = true;

    public override IReadOnlyList<string> Validate() {
        var errors = new List<string>(base.Validate());
        if (string.IsNullOrWhiteSpace(Target))
            errors.Add("Target name is empty");
        if (RaHours < 0 || RaHours >= 24)
            errors.Add($"RA hours out of range: {RaHours}");
        if (DecDeg < -90 || DecDeg > 90)
            errors.Add($"Dec out of range: {DecDeg}");
        return errors;
    }

    public override async Task ExecuteAsync(SequenceContext ctx, CancellationToken ct) {
        if (CenterOnStart) {
            ctx.Logger.LogInformation("DSO container '{Target}': Slew & Center → RA={Ra}h Dec={Dec}°",
                Target, RaHours, DecDeg);
            var job = ctx.SlewCenter.StartJob(RaHours, DecDeg);
            while (true) {
                ct.ThrowIfCancellationRequested();
                var status = ctx.SlewCenter.GetJob(job.Id);
                if (status == null) throw new InvalidOperationException("Slew & Center job vanished");
                if (status.State == SlewCenterState.Centered) break;
                if (status.State == SlewCenterState.Failed)
                    throw new InvalidOperationException($"Slew & Center failed: {status.Error}");
                if (status.State == SlewCenterState.Cancelled)
                    throw new OperationCanceledException("Slew & Center cancelled");
                await Task.Delay(500, ct);
            }
        }

        // From here on it's a sequential container — reuse that logic by
        // running children inline (we can't easily delegate to the base
        // because it's abstract; copy the loop).
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

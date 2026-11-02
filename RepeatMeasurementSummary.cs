namespace D2AbilityTracker;

internal static class RepeatMeasurementSummary
{
    internal static (double Mean, double Uncertainty) Aggregate(IReadOnlyList<(double Value, double Uncertainty)> samples)
    {
        if (samples.Count == 0 || samples.Any(sample => !double.IsFinite(sample.Value) || sample.Value <= 0 ||
            !double.IsFinite(sample.Uncertainty) || sample.Uncertainty < 0))
            throw new ArgumentException("Positive measurements with finite, non-negative uncertainty are required.", nameof(samples));

        var mean = samples.Average(sample => sample.Value);
        var timingFloor = samples.Average(sample => sample.Uncertainty);
        var scatterOfMean = samples.Count > 1
            ? samples.Sum(sample => Math.Pow(sample.Value - mean, 2)) / (samples.Count * (samples.Count - 1))
            : 0;
        // Capture timing errors may be correlated; do not shrink their reported floor with cycle count.
        return (mean, Math.Sqrt(timingFloor * timingFloor + scatterOfMean));
    }

    internal static MeasurementHistoryEntry Create(Guid runId, IReadOnlyList<MeasurementHistoryEntry> cycles, DateTime timestamp)
    {
        if (runId == Guid.Empty || cycles.Count is < 1 or > 100 ||
            cycles.Select(cycle => cycle.Id).Distinct().Count() != cycles.Count || cycles.Any(cycle =>
            cycle.Ability != cycles[0].Ability || cycle.Source != MeasurementSource.Event || cycle.CycleCount != 1 ||
            cycle.RepeatTestId != runId))
            throw new ArgumentException("A repeat summary requires validated cycles of one ability.", nameof(cycles));

        var measured = Aggregate(cycles.Select(cycle => (cycle.DurationSeconds, cycle.UncertaintySeconds)).ToArray());
        var fitted = cycles.Where(cycle => cycle.RegressionSeconds is not null && cycle.RegressionUncertaintySeconds is not null).ToArray();
        var regression = fitted.Length > 0
            ? Aggregate(fitted.Select(cycle => (cycle.RegressionSeconds!.Value, cycle.RegressionUncertaintySeconds!.Value)).ToArray())
            : ((double Mean, double Uncertainty)?)null;
        return new MeasurementHistoryEntry(runId, cycles[0].Ability, measured.Mean, regression?.Mean,
            measured.Uncertainty, regression?.Uncertainty, MeasurementSource.Event,
            new DateTimeOffset(timestamp.ToUniversalTime()), null, null,
            cycles.Count, fitted.Length, runId);
    }
}

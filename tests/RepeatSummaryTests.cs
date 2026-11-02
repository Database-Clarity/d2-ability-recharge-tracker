using D2AbilityTracker;

internal static class RepeatSummaryTests
{
    internal static void Run()
    {
        var now = new DateTime(2026, 11, 2, 0, 0, 0, DateTimeKind.Utc);
        var binding = new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.None);
        var test = new RepeatTestSession();
        test.Start(AbilityKind.Grenade, 2, TimeSpan.Zero, binding);
        var run = test.RunId;
        var first = new MeasurementHistoryEntry(Guid.NewGuid(), AbilityKind.Grenade, 10, 10.1, 0.1, 0.2,
            MeasurementSource.Event, new DateTimeOffset(now), null, null, RepeatTestId: run);
        var second = first with { Id = Guid.NewGuid(), DurationSeconds = 12, RegressionSeconds = 12.1 };
        test.ObserveState(AbilityKind.Grenade, AbilityState.Recharging, now);
        test.RecordRecharge(AbilityKind.Grenade, TimeSpan.FromSeconds(10), now, TimeSpan.FromSeconds(10.1), first);
        Assert(test.ValidatedSummary is null, "Incomplete repeat tests must not produce a saved mean.");
        Assert(test.TryGetDueUse(now, true, out _), "The second validated cycle must use the normal repeat flow.");
        test.ObserveState(AbilityKind.Grenade, AbilityState.Recharging, now);
        test.RecordRecharge(AbilityKind.Grenade, TimeSpan.FromSeconds(12), now, TimeSpan.FromSeconds(12.1), second);
        var summary = test.ValidatedSummary;
        Assert(summary is { CycleCount: 2, RegressionCycleCount: 2, DurationSeconds: 11 } &&
            Math.Abs(summary.UncertaintySeconds - Math.Sqrt(1.01)) < 0.000001,
            "A completed validated repeat test must expose its mean and aggregate uncertainty.");
        Assert(test.AverageRegressionDuration == TimeSpan.FromSeconds(11.1) &&
            test.Status(now).Contains("± 1.005 s"),
            "Repeat results must show regression averages and measured aggregate uncertainty.");
        test.Start(AbilityKind.Grenade, 1, TimeSpan.Zero, binding);
        Assert(test.RunId != run && test.AverageRegressionDuration is null && test.ValidatedSummary is null,
            "Starting another run must reset all aggregate state, including regressions.");
        test.ObserveState(AbilityKind.Grenade, AbilityState.Recharging, now);
        test.RecordRecharge(AbilityKind.Grenade, TimeSpan.FromSeconds(10), now, TimeSpan.FromSeconds(10.1));
        Assert(test.ValidatedSummary is null && test.Status(now).Contains("aggregate not validated"),
            "A completed run with an unvalidated cycle must not enter validated aggregate history.");
        test.Start(AbilityKind.Grenade, 1, TimeSpan.Zero, binding);
        test.ObserveState(AbilityKind.Grenade, AbilityState.Recharging, now);
        test.RecordRecharge(AbilityKind.Grenade, TimeSpan.FromSeconds(10), now, null,
            first with { RepeatTestId = test.RunId, RegressionSeconds = null, RegressionUncertaintySeconds = null });
        Assert(test.ValidatedSummary is { CycleCount: 1, RegressionCycleCount: 0, RegressionSeconds: null, UncertaintySeconds: 0.1 },
            "One-cycle repeats must retain the measured uncertainty without fabricating a regression.");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

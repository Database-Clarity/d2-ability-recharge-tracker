using D2AbilityTracker;

internal static class MeasurementHistoryTests
{
    public static void Run()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"D2AbilityTracker-history-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "history.json");
        try
        {
            var measuredAt = new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc);
            Assert(MeasurementHistoryStore.TryCreateValidatedMeasurement(
                AbilityKind.Grenade,
                AbilityState.Complete,
                TimeSpan.FromSeconds(5),
                TimeSpan.FromMilliseconds(50),
                MeasurementSource.Event,
                discarded: false,
                trackingWarning: null,
                measuredAt,
                TimeSpan.FromSeconds(5.1),
                TimeSpan.FromMilliseconds(100),
                out var measurement) && measurement is not null,
                "A complete, event-sourced measurement with captured uncertainty should be eligible.");

            Assert(!CanCreate(AbilityState.Recharging, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50),
                    MeasurementSource.Event, discarded: false),
                "An incomplete cycle must not be recorded.");
            Assert(!CanCreate(AbilityState.Complete, TimeSpan.Zero, TimeSpan.FromMilliseconds(50),
                    MeasurementSource.Event, discarded: false),
                "A zero-duration measurement must not be recorded.");
            Assert(!CanCreate(AbilityState.Complete, TimeSpan.FromSeconds(5), null,
                    MeasurementSource.Event, discarded: false),
                "A measurement without captured uncertainty must not be recorded.");
            Assert(!CanCreate(AbilityState.Complete, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50),
                    MeasurementSource.EventRefinedByModel, discarded: false),
                "A model-refined measurement must not pass the strict event-only gate.");
            Assert(!CanCreate(AbilityState.Complete, TimeSpan.FromSeconds(5), TimeSpan.FromMilliseconds(50),
                    MeasurementSource.Event, discarded: true),
                "A discarded cycle must not be recorded.");
            Assert(!MeasurementHistoryStore.TryCreateValidatedMeasurement(
                    AbilityKind.Grenade,
                    AbilityState.Complete,
                    TimeSpan.FromSeconds(5),
                    TimeSpan.FromMilliseconds(50),
                    MeasurementSource.Event,
                    discarded: false,
                    trackingWarning: "HUD signal lost",
                    measuredAt,
                    null,
                    null,
                    out _),
                "A cycle with a tracking warning must not be recorded.");

            var history = new MeasurementHistoryStore(path);
            var firstSession = history.CreateSession("Grenade test", new DateTimeOffset(measuredAt));
            Assert(history.TryAddMeasurement(measurement!), "A new validated measurement should be stored.");
            Assert(!history.TryAddMeasurement(measurement!), "Recording the same completion twice should be idempotent.");
            history.SaveNote(measurement!.Id, "Grenade stat 5");
            var secondSession = history.CreateSession("Grenade stat 10", new DateTimeOffset(measuredAt.AddMinutes(1)));
            history.AssignToActiveSession(measurement.Id);
            Assert(MeasurementHistoryStore.TryCreateValidatedMeasurement(
                AbilityKind.Grenade,
                AbilityState.Complete,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromMilliseconds(80),
                MeasurementSource.Event,
                discarded: false,
                trackingWarning: null,
                measuredAt.AddSeconds(30),
                null,
                null,
                out var secondMeasurement) && secondMeasurement is not null,
                "A later valid measurement should be eligible for the same ability.");
            Assert(history.TryAddMeasurement(secondMeasurement!), "Multiple measurements should be stored in one history.");
            history.SaveNote(secondMeasurement!.Id, "Grenade stat 10");

            var restored = new MeasurementHistoryStore(path);
            Assert(restored.Measurements.Count == 2, "History should retain multiple measurements.");
            var savedMeasurement = restored.Measurements.Single(item => item.Id == measurement.Id);
            var savedSecondMeasurement = restored.Measurements.Single(item => item.Id == secondMeasurement.Id);
            Assert(savedMeasurement.Note == "Grenade stat 5" && savedSecondMeasurement.Note == "Grenade stat 10",
                "Per-measurement notes should persist independently.");
            Assert(savedMeasurement.DurationSeconds == 5 && savedSecondMeasurement.DurationSeconds == 10,
                "Distinct measurement values should persist for the same ability.");
            Assert(savedMeasurement.RegressionSeconds == 5.1 && savedMeasurement.Source == MeasurementSource.Event &&
                savedMeasurement.UncertaintySeconds == 0.05 &&
                savedMeasurement.MeasuredAt == new DateTimeOffset(measuredAt),
                "Regression, event source, uncertainty, and measurement time should round-trip.");
            Assert(savedMeasurement.SessionId == secondSession.Id && savedSecondMeasurement.SessionId == secondSession.Id,
                "A stored measurement should be assignable to the active named session.");
            Assert(restored.Sessions.Single(session => session.Id == firstSession.Id).EndedAt is not null,
                "Starting a new session should end the previous active session.");
            Assert(restored.ActiveSessionId == secondSession.Id, "The active named session should persist.");

            var run = Guid.NewGuid();
            var cycles = new[]
            {
                measurement with { Id = Guid.NewGuid(), DurationSeconds = 10, UncertaintySeconds = 0.1, RegressionSeconds = 9.9, RegressionUncertaintySeconds = 0.2, RepeatTestId = run },
                measurement with { Id = Guid.NewGuid(), DurationSeconds = 12, UncertaintySeconds = 0.3, RegressionSeconds = null, RegressionUncertaintySeconds = null, RepeatTestId = run }
            };
            var summary = RepeatMeasurementSummary.Create(run, cycles, measuredAt.AddMinutes(2));
            Assert(summary.DurationSeconds == 11 &&
                Math.Abs(summary.UncertaintySeconds - Math.Sqrt(1.04)) < 0.000001,
                "Aggregate uncertainty must include scatter of the mean and retain the correlated timing floor.");
            Assert(summary.RegressionCycleCount == 1 && summary.RegressionSeconds == 9.9 &&
                summary.RegressionUncertaintySeconds == 0.2,
                "Regression aggregation must include only cycles with a fit and disclose its count.");
            var repeated = RepeatMeasurementSummary.Aggregate([(10, 0.2), (10, 0.2), (10, 0.2)]);
            Assert(repeated.Mean == 10 && Math.Abs(repeated.Uncertainty - 0.2) < 0.000001,
                "Repeated identical cycles must not erase a possibly shared timing uncertainty.");
            Assert(restored.TryAddMeasurement(summary, firstSession.Id),
                "A repeat result must remain attached to its original session even after the active session changes.");
            restored.SaveNote(summary.Id, "Repeated at grenade stat 5");
            Assert(!restored.TryAddMeasurement(summary, firstSession.Id), "Saving a repeat summary twice must be idempotent.");
            var restoredRepeat = new MeasurementHistoryStore(path).Measurements.Single(item => item.Id == run);
            Assert(restoredRepeat.CycleCount == 2 && restoredRepeat.RegressionCycleCount == 1 &&
                restoredRepeat.RepeatTestId == run && restoredRepeat.SessionId == firstSession.Id &&
                restoredRepeat.Note == "Repeated at grenade stat 5" && restoredRepeat.UncertaintySeconds == summary.UncertaintySeconds,
                "Repeat aggregates, uncertainty, notes, counts, and original session must round-trip.");
            var oneRun = Guid.NewGuid();
            var oneCycle = cycles[0] with { Id = Guid.NewGuid(), RepeatTestId = oneRun, MeasuredAt = summary.MeasuredAt };
            var oneSummary = RepeatMeasurementSummary.Create(oneRun, [oneCycle], summary.MeasuredAt.UtcDateTime);
            Assert(restored.TryAddMeasurement(oneCycle) && restored.TryAddMeasurement(oneSummary),
                "A one-cycle repeat mean and its individual cycle must both be retained even with identical values and timestamps.");

            Assert(restored.EndActiveSession(new DateTimeOffset(measuredAt.AddMinutes(2))),
                "Ending an active session should succeed.");
            var ended = new MeasurementHistoryStore(path);
            Assert(ended.ActiveSessionId is null &&
                ended.Sessions.Single(session => session.Id == secondSession.Id).EndedAt is not null,
                "Ending a session should persist its end time and clear active state.");

            const string corruptContents = "{ history is not valid JSON";
            File.WriteAllText(path, corruptContents);
            var corrupt = new MeasurementHistoryStore(path);
            Assert(!corrupt.CanWrite && corrupt.LoadError is not null,
                "Corrupt history should surface a load error and disable writes.");
            Assert(corrupt.Measurements.Count == 0, "Corrupt data must not be presented as valid history.");
            var writeRejected = false;
            try
            {
                corrupt.CreateSession("Must not overwrite", new DateTimeOffset(measuredAt.AddDays(1)));
            }
            catch (InvalidOperationException)
            {
                writeRejected = true;
            }

            Assert(writeRejected && File.ReadAllText(path) == corruptContents,
                "A write after a corrupt load must leave the original file untouched.");

            foreach (var nullEntry in new[]
            {
                """{"Version":1,"Sessions":[null],"Measurements":[]}""",
                """{"Version":1,"Sessions":[],"Measurements":[null]}"""
            })
            {
                File.WriteAllText(path, nullEntry);
                var invalid = new MeasurementHistoryStore(path);
                Assert(!invalid.CanWrite && invalid.LoadError is not null &&
                    File.ReadAllText(path) == nullEntry,
                    "Null history records must be reported without crashing or overwriting the file.");
            }
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            Directory.Delete(directory);
        }
    }

    private static bool CanCreate(
        AbilityState state,
        TimeSpan? duration,
        TimeSpan? uncertainty,
        MeasurementSource? source,
        bool discarded) =>
        MeasurementHistoryStore.TryCreateValidatedMeasurement(
            AbilityKind.Grenade,
            state,
            duration,
            uncertainty,
            source,
            discarded,
            trackingWarning: null,
            new DateTime(2026, 11, 1, 12, 0, 0, DateTimeKind.Utc),
            regressionDuration: null,
            regressionUncertainty: null,
            out _);

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }
}

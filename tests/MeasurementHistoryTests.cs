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
            history.AssignToSession(measurement.Id, secondSession.Id);
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
            restored.AddCustomColumn(firstSession.Id, "Grenade Stat");
            restored.AddCustomColumn(firstSession.Id, "Loadout, notes");
            restored.SaveCustomValue(summary.Id, "Grenade Stat", "5");
            restored.SaveCustomValue(summary.Id, "Loadout, notes", "=1+1\n\"test\"");
            var customRestored = new MeasurementHistoryStore(path);
            Assert(customRestored.GetCustomColumns(firstSession.Id).SequenceEqual(new[] { "Grenade Stat", "Loadout, notes" }) &&
                customRestored.Measurements.Single(item => item.Id == run).CustomValues!["Grenade Stat"] == "5",
                "Named columns and per-result values must persist independently of notes.");
            var duplicateRejected = false;
            try { customRestored.AddCustomColumn(firstSession.Id, "grenade stat"); }
            catch (ArgumentException) { duplicateRejected = true; }
            Assert(duplicateRejected, "Custom column names must be unique without case ambiguity.");
            var snapshot = customRestored.Measurements.Single(item => item.Id == run);
            snapshot.CustomValues!["Grenade Stat"] = "999";
            Assert(customRestored.Measurements.Single(item => item.Id == run).CustomValues!["Grenade Stat"] == "5",
                "Editing a snapshot must not mutate persisted history.");
            var csv = MeasurementCsv.Create(customRestored.Measurements.Where(item => item.Id == run),
                customRestored.Sessions, customRestored.GetCustomColumns(firstSession.Id));
            Assert(csv.Contains("\"Grenade Stat\",\"Loadout, notes\"") && csv.Contains("\"5\",\"'=1+1\n\"\"test\"\"\"") &&
                csv.Contains("\"Mean\"") && csv.Contains("\"2\",\"1\"") && csv.Contains("\"Repeated at grenade stat 5\""),
                "CSV must include custom columns, repeat counts, notes and safely quoted formula-like values.");
            var measurementCsv = MeasurementCsv.Create([savedMeasurement], restored.Sessions, []);
            Assert(measurementCsv.Contains("\"Measured seconds\",\"Measurement uncertainty seconds\",\"Regression seconds\",\"Regression uncertainty seconds\"") &&
                measurementCsv.Contains("\"5\",\"0.05\",\"5.1\",\"0.1\""),
                "CSV must export measured and regression uncertainties in their own respective columns.");
            var culture = System.Globalization.CultureInfo.CurrentCulture;
            try
            {
                System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo("de-DE");
                Assert(MeasurementCsv.Create([summary], customRestored.Sessions, []).Contains("\"11\",\"1.019803902718557\""),
                    "CSV numeric values must remain invariant regardless of desktop locale.");
            }
            finally { System.Globalization.CultureInfo.CurrentCulture = culture; }

            SessionColumnTests(customRestored, firstSession.Id, secondSession.Id, run, path);
            restored = new MeasurementHistoryStore(path);
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

    /// <summary>Per-session columns, moving between sessions, renaming, deleting a session and reopening.</summary>
    private static void SessionColumnTests(MeasurementHistoryStore history, Guid columnsSession, Guid otherSession, Guid meanId, string path)
    {
        Assert(history.GetCustomColumns(otherSession).Count == 0,
            "A column added to one session must not appear in another session.");
        history.AddCustomColumn(otherSession, "Grenade Stat");
        Assert(history.GetCustomColumns(otherSession).SequenceEqual(["Grenade Stat"]),
            "The same column name may exist independently in different sessions.");
        var foreignRejected = false;
        var otherMeasurement = history.Measurements.First(item => item.SessionId == otherSession).Id;
        try { history.SaveCustomValue(otherMeasurement, "Loadout, notes", "x"); }
        catch (ArgumentException) { foreignRejected = true; }
        Assert(foreignRejected, "A value can only be saved in a column of the measurement's own session.");

        // Moving keeps every stored value; only columns that exist in the destination show there.
        history.AssignToSession(meanId, otherSession);
        var moved = new MeasurementHistoryStore(path).Measurements.Single(item => item.Id == meanId);
        Assert(moved.SessionId == otherSession && moved.CustomValues!["Grenade Stat"] == "5" &&
            moved.CustomValues.ContainsKey("Loadout, notes"),
            "Moving a measurement must keep its values; matching columns show them in the new session.");
        history.AssignToSession(meanId, null);
        Assert(history.Measurements.Single(item => item.Id == meanId).SessionId is null,
            "A measurement can be moved back to Unsorted.");
        history.AssignToSession(meanId, columnsSession);
        Assert(history.Measurements.Single(item => item.Id == meanId).CustomValues!["Loadout, notes"] == "=1+1\n\"test\"",
            "Returning to the original session must show its hidden values again.");
        var missingRejected = false;
        try { history.AssignToSession(meanId, Guid.NewGuid()); }
        catch (ArgumentException) { missingRejected = true; }
        Assert(missingRejected, "Moving to a missing session must be rejected.");

        history.RenameSession(otherSession, "  Renamed  ");
        Assert(new MeasurementHistoryStore(path).Sessions.Single(session => session.Id == otherSession).Name == "Renamed",
            "Renaming must trim and persist.");

        var scratch = history.CreateSession("Scratch", DateTimeOffset.UtcNow);
        history.AddCustomColumn(scratch.Id, "Temp");
        history.AssignToSession(otherMeasurement, scratch.Id);
        history.SaveCustomValue(otherMeasurement, "Temp", "kept");
        var countBefore = history.Measurements.Count;
        history.DeleteSession(scratch.Id);
        var afterDelete = new MeasurementHistoryStore(path);
        Assert(afterDelete.Sessions.All(session => session.Id != scratch.Id) && afterDelete.ActiveSessionId is null &&
            afterDelete.Measurements.Count == countBefore &&
            afterDelete.Measurements.Single(item => item.Id == otherMeasurement) is { SessionId: null } orphan &&
            orphan.CustomValues!["Temp"] == "kept" && afterDelete.GetCustomColumns(scratch.Id).Count == 0,
            "Deleting a session must keep its measurements (unsorted, values preserved) and clear it as active.");
        afterDelete.SetActiveSession(otherSession, DateTimeOffset.UtcNow);
        Assert(afterDelete.ActiveSessionId == otherSession &&
            afterDelete.Sessions.Single(session => session.Id == otherSession).EndedAt is null,
            "Make active must reopen an ended session.");
        afterDelete.AssignToSession(otherMeasurement, otherSession);
        var single = afterDelete.Measurements.Single(item => item.Id == otherMeasurement);
        Assert(afterDelete.TryAddMeasurement(single with { Id = Guid.NewGuid(), DurationSeconds = 3, CustomValues = null }, Guid.NewGuid()) &&
            afterDelete.Measurements.Single(item => item.DurationSeconds == 3).SessionId is null,
            "A result for a session that no longer exists must be stored as unsorted.");
        var saved = File.ReadAllText(path);
        Assert(saved.Contains("\"Version\": 2") && !saved.Contains("\"CustomColumns\": null"),
            "Saved history must use the per-session document version.");
        MigrationTests(Path.Combine(Path.GetDirectoryName(path)!, "legacy.json"));
    }

    /// <summary>A version 1 file (global columns) loads with each column copied onto the sessions that use it.</summary>
    private static void MigrationTests(string path)
    {
        var used = Guid.NewGuid();
        var unused = Guid.NewGuid();
        var withValue = Guid.NewGuid();
        var unsorted = Guid.NewGuid();
        var plain = Guid.NewGuid();
        File.WriteAllText(path, $$"""
            {
              "Version": 1,
              "Sessions": [
                { "Id": "{{used}}", "Name": "Used", "StartedAt": "2026-10-01T10:00:00+00:00", "EndedAt": "2026-10-01T11:00:00+00:00" },
                { "Id": "{{unused}}", "Name": "Unused", "StartedAt": "2026-10-02T10:00:00+00:00", "EndedAt": null }
              ],
              "Measurements": [
                { "Id": "{{withValue}}", "Ability": "Grenade", "DurationSeconds": 5, "UncertaintySeconds": 0.05, "Source": "Event",
                  "MeasuredAt": "2026-10-01T10:30:00+00:00", "SessionId": "{{used}}", "CustomValues": { "Stat": "70" } },
                { "Id": "{{unsorted}}", "Ability": "Melee", "DurationSeconds": 6, "UncertaintySeconds": 0.05, "Source": "Event",
                  "MeasuredAt": "2026-10-01T10:40:00+00:00", "SessionId": null, "CustomValues": { "Stat": "40", "Exotic": "Yes" } },
                { "Id": "{{plain}}", "Ability": "Melee", "DurationSeconds": 7, "UncertaintySeconds": 0.05, "Source": "Event",
                  "MeasuredAt": "2026-10-02T10:40:00+00:00", "SessionId": "{{unused}}" }
              ],
              "ActiveSessionId": "{{unused}}",
              "CustomColumns": [ "Stat", "Exotic" ]
            }
            """);
        var legacy = new MeasurementHistoryStore(path);
        Assert(legacy.CanWrite && legacy.GetCustomColumns(used).SequenceEqual(["Stat"]) &&
            legacy.GetCustomColumns(unused).Count == 0 &&
            legacy.Measurements.Single(item => item.Id == unsorted).CustomValues!["Exotic"] == "Yes" &&
            legacy.ActiveSessionId == unused,
            "Migration must copy each global column only onto sessions with values and keep unsorted values.");
        legacy.AssignToSession(unsorted, used);
        var upgraded = new MeasurementHistoryStore(path);
        var upgradedText = File.ReadAllText(path);
        Assert(upgraded.GetCustomColumns(used).SequenceEqual(["Stat"]) &&
            upgraded.Measurements.Single(item => item.Id == unsorted).CustomValues!["Stat"] == "40" &&
            upgradedText.Contains("\"Version\": 2") && upgradedText.IndexOf("\"CustomColumns\"", StringComparison.Ordinal) ==
                upgradedText.LastIndexOf("\"CustomColumns\"", StringComparison.Ordinal),
            "A migrated file must save as version 2 with columns only on the session that uses them.");

        foreach (var invalid in new[]
        {
            """{"Version":2,"Sessions":[],"Measurements":[],"CustomColumns":["Stat"]}""",
            $$"""{"Version":2,"Sessions":[{"Id":"{{used}}","Name":"A","StartedAt":"2026-10-01T10:00:00+00:00","CustomColumns":["Stat","stat"]}],"Measurements":[]}""",
            $$"""{"Version":1,"Sessions":[{"Id":"{{used}}","Name":"A","StartedAt":"2026-10-01T10:00:00+00:00","CustomColumns":["Stat"]}],"Measurements":[],"CustomColumns":[]}""",
            """{"Version":3,"Sessions":[],"Measurements":[]}"""
        })
        {
            File.WriteAllText(path, invalid);
            var rejected = new MeasurementHistoryStore(path);
            Assert(!rejected.CanWrite && File.ReadAllText(path) == invalid,
                "Global columns in version 2, duplicate session columns, session columns in version 1 and unknown versions must be rejected without overwriting.");
        }

        File.Delete(path);
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

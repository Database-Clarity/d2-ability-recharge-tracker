using System.Globalization;
using System.Text;

namespace D2AbilityTracker;

internal static class MeasurementCsv
{
    internal static string Create(IEnumerable<MeasurementHistoryEntry> measurements,
        IReadOnlyList<MeasurementHistorySession> sessions, IReadOnlyList<string> columns)
    {
        var result = new StringBuilder();
        void Row(IEnumerable<string> cells) => result.AppendLine(string.Join(",", cells.Select(Escape)));
        Row(new[] { "Ability", "Result", "Session", "Measured seconds", "Measurement uncertainty seconds",
            "Regression seconds", "Regression uncertainty seconds", "Cycles", "Regression cycles",
            "Measured at", "Repeat test ID", "Note" }.Concat(columns));
        foreach (var item in measurements)
        {
            Row(new[] { item.Ability.ToString(),
                item.Id == item.RepeatTestId ? "Mean" : item.RepeatTestId is not null ? "Cycle" : "Single",
                sessions.FirstOrDefault(session => session.Id == item.SessionId)?.Name ?? "",
                Number(item.DurationSeconds), Number(item.UncertaintySeconds), Number(item.RegressionSeconds),
                Number(item.RegressionUncertaintySeconds), item.CycleCount.ToString(CultureInfo.InvariantCulture),
                item.RegressionCycleCount?.ToString(CultureInfo.InvariantCulture) ?? "",
                item.MeasuredAt.ToString("O", CultureInfo.InvariantCulture), item.RepeatTestId?.ToString() ?? "", item.Note ?? "" }
                .Concat(columns.Select(column => item.CustomValues?.GetValueOrDefault(column) ?? "")));
        }
        return result.ToString();
    }

    private static string Number(double? value) => value?.ToString("R", CultureInfo.InvariantCulture) ?? "";

    private static string Escape(string value)
    {
        // CSV quoting alone does not prevent spreadsheet formula execution.
        if (value.TrimStart().FirstOrDefault() is '=' or '+' or '-' or '@' || value.StartsWith('\t') || value.StartsWith('\r'))
            value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}

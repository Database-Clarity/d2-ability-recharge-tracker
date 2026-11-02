namespace D2AbilityTracker;

internal sealed partial class MainForm
{
    private MeasurementHistoryStore? _measurementHistory;
    private Guid? _repeatHistorySessionId;

    private void InitializeMeasurementHistory()
    {
        _measurementHistory = new MeasurementHistoryStore();
        if (_measurementHistory.LoadError is { } error)
        {
            Shown += (_, _) => ReportMeasurementHistoryError($"Couldn't load measurement history; the file was left unchanged: {error}");
        }
    }

    private void ShowMeasurementHistory()
    {
        var history = _measurementHistory ??= new MeasurementHistoryStore();
        using var form = new MeasurementHistoryForm(history);
        form.ShowDialog(this);
    }

    private MeasurementHistoryEntry? RecordValidatedMeasurement(AbilityKind kind, AbilitySlotTracker tracker, DateTime timestamp)
    {
        if (!MeasurementHistoryStore.TryCreateValidatedMeasurement(
            kind,
            tracker.State,
            tracker.LastDuration,
            tracker.LastUncertainty,
            tracker.LastSource,
            tracker.MeasurementDiscarded,
            tracker.TrackingWarning,
            timestamp,
            tracker.LastRegressionDuration,
            tracker.LastRegressionUncertainty,
            out var measurement) ||
            measurement is null)
        {
            return null;
        }

        var repeatCycle = _repeatTest.IsActive && _repeatTest.Ability == kind;
        if (repeatCycle) measurement = measurement with { RepeatTestId = _repeatTest.RunId };
        SaveHistoryEntry(measurement, repeatCycle ? _repeatHistorySessionId : _measurementHistory?.ActiveSessionId);
        return measurement;
    }

    private void SaveHistoryEntry(MeasurementHistoryEntry measurement, Guid? sessionId)
    {
        void SaveMeasurement()
        {
            try
            {
                _measurementHistory ??= new MeasurementHistoryStore();
                if (_measurementHistory.CanWrite)
                {
                    _measurementHistory.TryAddMeasurement(measurement, sessionId);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or InvalidOperationException)
            {
                ReportMeasurementHistoryError($"Couldn't record measurement history: {exception.Message}");
            }
        }

        if (InvokeRequired)
        {
            BeginInvoke((Action)SaveMeasurement);
        }
        else
        {
            SaveMeasurement();
        }
    }

    private void ReportMeasurementHistoryError(string message)
    {
        if (IsDisposed || Disposing || !IsHandleCreated)
        {
            return;
        }

        void ShowError() =>
            MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);

        if (InvokeRequired)
        {
            BeginInvoke((Action)ShowError);
        }
        else
        {
            ShowError();
        }
    }
}

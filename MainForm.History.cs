namespace D2AbilityTracker;

internal sealed partial class MainForm
{
    private MeasurementHistoryStore? _measurementHistory;
    private Guid? _repeatHistorySessionId;
    private MeasurementHistoryView? _historyView;

    private void InitializeMeasurementHistory()
    {
        _measurementHistory = new MeasurementHistoryStore(_storagePaths.MeasurementHistoryPath);
        if (_measurementHistory.LoadError is { } error)
        {
            Shown += (_, _) => ReportMeasurementHistoryError($"Couldn't load measurement history; the file was left unchanged: {error}");
        }
    }

    private MeasurementHistoryView CreateHistoryView()
    {
        _historyView = new MeasurementHistoryView(_measurementHistory ??=
            new MeasurementHistoryStore(_storagePaths.MeasurementHistoryPath))
        {
            Visible = false,
            Width = GridWidth,
            Height = HistoryHeight,
            Margin = Padding.Empty
        };
        return _historyView;
    }

    /// <summary>True while the History tab is the selected page.</summary>
    internal bool HistoryVisible => _historyView is not null && _selectedPage == _historyView && _historyView.Visible;

    /// <summary>Shows the History tab (reloading it), or returns to the last compact page.</summary>
    internal void SetHistoryVisible(bool visible)
    {
        if (_historyView is null) return;
        if (visible)
        {
            _historyView.Reload();
            SelectPage(_historyView);
        }
        else if (_selectedPage == _historyView && _lastCompactPage is { } previous)
        {
            SelectPage(previous);
        }
    }

    internal void UseMeasurementHistory(MeasurementHistoryStore history)
    {
        _measurementHistory = history;
        _historyView?.UseStore(history);
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
                _measurementHistory ??= new MeasurementHistoryStore(_storagePaths.MeasurementHistoryPath);
                if (_measurementHistory.CanWrite)
                {
                    _measurementHistory.TryAddMeasurement(measurement, sessionId);
                    if (HistoryVisible) _historyView!.Reload();
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

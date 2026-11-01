using System.Drawing;

namespace D2AbilityTracker;

// Overlay content, layout, and move mode.
internal sealed partial class MainForm
{
    private bool _overlayMoveMode;

    /// <summary>Lets the user drag the overlay; when off the overlay is click-through.</summary>
    private void SetOverlayMoveMode(bool enabled)
    {
        _overlayMoveMode = enabled;
    }

    /// <summary>Chooses one line per ability (false) or two lines per ability (true).</summary>
    private void SetOverlayTwoLines(bool twoLines)
    {
        _settings.OverlayTwoLines = twoLines;
        SaveSettings();
        UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        lock (_captureSync)
        {
            var rows = AbilityKinds.Where(IsTracked).Select(kind =>
            {
                var tracker = _trackers.GetValueOrDefault(kind);
                var slot = _settings.Slots.FirstOrDefault(item => item.Kind == kind);
                var label = KindShortName(kind);
                if (!_monitoring)
                {
                    return $"{label}  Paused{DurationSuffix(tracker?.LastDuration)}";
                }

                if (slot?.Region is null)
                {
                    return $"{label}  Select area";
                }

                if (tracker?.State == AbilityState.Recharging)
                {
                    var progressText = tracker.TrackingWarning is not null
                        ? "recovering"
                        : tracker.DisplayProgress is { } progress ? $"{progress:P0}" : "acquiring progress";
                    var estimateText = tracker.EstimatedDuration is { } estimate && tracker.EstimateUncertainty is { } spread
                        ? $"Est. total {estimate.TotalSeconds:F2} ± {spread.TotalSeconds:F2} s"
                        : "Est. total: measuring…";
                    return $"{label}  {FormatDuration(tracker.CurrentDuration)} · {progressText}\n          {estimateText}";
                }

                if (tracker?.MeasurementDiscarded == true)
                {
                    return $"{label}  Endpoint obscured · measurement discarded";
                }

                if (tracker?.State == AbilityState.Consumed && tracker.TrackingWarning is not null)
                {
                    return $"{label}  Recharge-start pulse missed · reset detection";
                }

                return tracker?.State switch
                {
                    AbilityState.Ready => $"{label}  Ready{DurationSuffix(tracker.LastDuration)}",
                    AbilityState.Consumed => $"{label}  Used · waiting",
                    AbilityState.Complete => $"{label}  Complete · {FormatDuration(tracker.LastDuration)}{MeasurementSuffix(tracker)}",
                    _ => $"{label}  Watching"
                };
            }).ToArray();

            var accent = !_monitoring
                ? Color.Gray
                : _trackers.Values.Any(tracker => tracker.State == AbilityState.Recharging)
                    ? Color.FromArgb(86, 167, 229)
                    : Color.FromArgb(89, 190, 130);
            var savedMonitor = _settings.Slots.FirstOrDefault(slot => slot.Region is not null);
            var monitor = savedMonitor?.MonitorDeviceName is { } deviceName
                ? Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))?.Bounds
                    ?? savedMonitor.MonitorBounds
                : savedMonitor?.MonitorBounds;
            _overlay.SetStatus(_monitoring ? "ABILITY RECHARGE" : "TRACKING PAUSED", string.Join(Environment.NewLine, rows), accent, monitor);
        }
    }
}

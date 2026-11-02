using System.Drawing;
using System.Globalization;

namespace D2AbilityTracker;

// Overlay content, layout, and move mode.
internal sealed partial class MainForm
{
    private static readonly Color OverlayReadyColor = Color.FromArgb(89, 190, 130);
    private static readonly Color OverlayRechargingColor = Color.FromArgb(86, 167, 229);
    private static readonly Color OverlayWarningColor = Color.FromArgb(245, 172, 65);
    private static readonly Color OverlayIdleColor = Color.FromArgb(120, 126, 140);

    private bool _overlayMoveMode;
    private bool _overlayInteractionsInitialized;

    /// <summary>Raised whenever move mode turns on or off (from the main window or from the overlay itself).</summary>
    private event Action<bool>? OverlayMoveModeChanged;

    /// <summary>Hooks overlay drag / anchor events. Call once from the constructor after BuildControls().</summary>
    private void InitializeOverlayInteractions()
    {
        if (_overlayInteractionsInitialized)
        {
            return;
        }

        _overlayInteractionsInitialized = true;
        _overlay.CustomLocation = _settings.OverlayLocation;
        _overlay.UpdatePosition(_settings.OverlayAnchor);
        _overlay.MoveFinished += _ => SetOverlayMoveMode(false);
        _overlay.LocationCommitted += location =>
        {
            _settings.OverlayLocation = location;
            SaveSettings();
        };
        _anchorPicker.SelectionChangeCommitted += (_, _) => ClearOverlayCustomLocation();
    }

    /// <summary>Lets the user drag the overlay; when off the overlay is click-through.</summary>
    private void SetOverlayMoveMode(bool enabled)
    {
        if (_overlayMoveMode == enabled)
        {
            return;
        }

        _overlayMoveMode = enabled;
        if (enabled)
        {
            _overlay.Show();
            UpdateOverlay();
            _overlay.SetMoveMode(true);
        }
        else
        {
            _overlay.SetMoveMode(false);
            _settings.OverlayLocation = _overlay.Location;
            _overlay.CustomLocation = _settings.OverlayLocation;
            SaveSettings();
            ShowOverlayIfEnabled();
        }

        OverlayMoveModeChanged?.Invoke(enabled);
    }

    /// <summary>Drops the dragged overlay position so the anchor picker controls placement again.</summary>
    private void ClearOverlayCustomLocation()
    {
        if (_overlayMoveMode)
        {
            SetOverlayMoveMode(false);
        }

        _settings.OverlayLocation = null;
        _overlay.CustomLocation = null;
        _overlay.UpdatePosition(_settings.OverlayAnchor);
        SaveSettings();
        UpdateOverlay();
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
            var twoLines = _settings.OverlayTwoLines;
            var rows = AbilityKinds
                .Where(IsTracked)
                .Select(kind => BuildOverlayRow(kind, twoLines))
                .ToArray();

            var capturedKinds = CapturedKinds();
            var accent = !_monitoring
                ? Color.Gray
                : capturedKinds.Any(_captureErrors.ContainsKey) || CaptureStalled(capturedKinds.Length > 0)
                    ? OverlayWarningColor
                    : _trackers.Values.Any(tracker => tracker.State == AbilityState.Recharging)
                        ? OverlayRechargingColor
                        : OverlayReadyColor;
            var savedMonitor = _settings.Slots.FirstOrDefault(slot => slot.Region is not null);
            var monitor = savedMonitor?.MonitorDeviceName is { } deviceName
                ? Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))?.Bounds
                    ?? savedMonitor.MonitorBounds
                : savedMonitor?.MonitorBounds;
            var title = _monitoring ? "ABILITY RECHARGE"
                : PauseBinding is { } pause ? $"PAUSED · {pause} TO RESUME".ToUpperInvariant() : "PAUSED";
            _overlay.SetContent(title, rows, twoLines, accent, monitor, _settings.OverlayAnchor);
        }
    }

    private OverlayRow BuildOverlayRow(AbilityKind kind, bool twoLines)
    {
        var tracker = _trackers.GetValueOrDefault(kind);
        var slot = _settings.Slots.FirstOrDefault(item => item.Kind == kind);
        var label = KindShortName(kind).Trim();
        var regression = tracker?.LastRegressionDuration is { } fitted ? $"regression {CompactDuration(fitted)}" : null;
        var last = tracker?.LastDuration is { } lastDuration
            ? regression is null ? $"Last {CompactDuration(lastDuration)}" : $"Last {CompactDuration(lastDuration)} · {regression}"
            : null;

        OverlayRow Row(string line1, string? line2, string? compact, Color color) =>
            new(label, twoLines ? line1 : compact ?? JoinCompact(line1, line2), twoLines ? line2 : null, color);

        if (!_monitoring)
        {
            return Row("Paused", last, null, OverlayIdleColor);
        }

        if (slot?.Region is null)
        {
            return Row("Select area", "Pick the HUD slot in the app", "Select area", OverlayWarningColor);
        }

        if (_captureErrors.TryGetValue(kind, out var captureError))
        {
            var reason = captureError == GameWindowUnavailableError ? "Waiting for game"
                : _captureLoopError is not null ? "Capture stopped" : "Capture failed";
            return Row(reason, last, null, OverlayWarningColor);
        }

        if (CaptureStalled(true))
        {
            return Row("No capture frames", last, null, OverlayWarningColor);
        }

        if (tracker?.State == AbilityState.Recharging)
        {
            var progressText = tracker.TrackingWarning is not null
                ? "recovering"
                : tracker.DisplayProgress is { } progress ? progress.ToString("P0", CultureInfo.CurrentCulture) : "acquiring";
            var color = tracker.TrackingWarning is not null ? OverlayWarningColor : OverlayRechargingColor;
            var estimate = tracker.EstimatedDuration;
            var estimateText = estimate is { } total && tracker.EstimateUncertainty is { } spread
                ? $"Est. total {total.TotalSeconds:F2} ± {spread.TotalSeconds:F2} s"
                : "Est. total: measuring…";
            var compactEstimate = estimate is { } compactTotal ? $"est {CompactDuration(compactTotal)}" : "est …";
            return Row(
                $"{FormatDuration(tracker.CurrentDuration)} · {progressText}",
                estimateText,
                $"{CompactDuration(tracker.CurrentDuration)} · {progressText} · {compactEstimate}",
                color);
        }

        if (tracker?.MeasurementDiscarded == true)
        {
            return Row("Measurement discarded", "Endpoint obscured", "Discarded · endpoint obscured", OverlayWarningColor);
        }

        if (tracker?.State == AbilityState.Consumed && tracker.TrackingWarning is not null)
        {
            return Row("Recharge start missed", "Resetting detection", "Start missed · resetting", OverlayWarningColor);
        }

        return tracker?.State switch
        {
            AbilityState.Ready => Row("Ready", last, null, OverlayReadyColor),
            AbilityState.Consumed => Row("Used · waiting", "Waiting for recharge to start", "Used · waiting", OverlayRechargingColor),
            AbilityState.Complete => Row(
                $"Complete · {CompactDuration(tracker.LastDuration)}{MeasurementSuffix(tracker)}",
                tracker.LastRegressionDuration is { } fit && tracker.LastRegressionUncertainty is { } fitSpread
                    ? $"Regression {CompactDuration(fit)} ± {fitSpread.TotalSeconds:F2} s"
                    : "Regression: not enough fill observed",
                $"Complete · {CompactDuration(tracker.LastDuration)}{(regression is null ? MeasurementSuffix(tracker) : $" · {regression}")}",
                OverlayReadyColor),
            _ => Row("Watching", null, null, OverlayIdleColor)
        };
    }

    private static string JoinCompact(string line1, string? line2) =>
        string.IsNullOrEmpty(line2) ? line1 : $"{line1} · {char.ToLowerInvariant(line2[0])}{line2[1..]}";

    private static string CompactDuration(TimeSpan? duration) =>
        duration is { } value
            ? $"{Math.Round(value.TotalSeconds, 2, MidpointRounding.AwayFromZero).ToString("F2", CultureInfo.InvariantCulture)} s"
            : "--.-- s";
}
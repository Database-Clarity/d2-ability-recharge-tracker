using System.Diagnostics;

namespace D2AbilityTracker;

internal sealed record CaptureErrorRouting(
    bool WaitingForGame,
    string? SharedError,
    IReadOnlyDictionary<AbilityKind, string> RowErrors);

internal sealed record TrackingStatusSnapshot(
    bool Monitoring,
    InputBinding? PauseBinding,
    string? CaptureLoopError,
    string? InputListenerError,
    bool HasRegions,
    CaptureErrorRouting Errors,
    bool CaptureStalled,
    string Backend,
    double CaptureRate,
    int TargetCaptureRate,
    bool AnyRegions = true);

// Consolidated capture state: one banner on Tracking and one pill in the navigation row.
internal sealed partial class MainForm
{
    internal const string GameWindowUnavailableError = "Destiny 2 window is unavailable; waiting to recover the HUD.";
    internal const string WaitingForGameBanner = "Waiting for Destiny 2 — start the game and keep the HUD visible.";

    private AbilityKind[] CapturedKinds() =>
        AbilityKinds.Where(kind => IsTracked(kind) && GetSlot(kind).Region is not null).ToArray();

    /// <summary>
    /// Separates global capture failures (missing game window, or one identical error on every captured slot)
    /// from failures that belong to individual rows. Does not modify the per-slot error map.
    /// </summary>
    internal static CaptureErrorRouting RouteCaptureErrors(
        IReadOnlyDictionary<AbilityKind, string> errors,
        IReadOnlyCollection<AbilityKind> capturedKinds)
    {
        var waitingForGame = false;
        var remaining = new List<AbilityKind>();
        foreach (var kind in capturedKinds)
        {
            if (errors.TryGetValue(kind, out var error) && error == GameWindowUnavailableError)
            {
                waitingForGame = true;
            }
            else
            {
                remaining.Add(kind);
            }
        }

        var failed = remaining.Where(errors.ContainsKey).ToArray();
        if (failed.Length >= 2 && failed.Length == remaining.Count &&
            failed.Select(kind => errors[kind]).Distinct(StringComparer.Ordinal).Count() == 1)
        {
            return new CaptureErrorRouting(waitingForGame, errors[failed[0]], new Dictionary<AbilityKind, string>());
        }

        return new CaptureErrorRouting(waitingForGame, null, failed.ToDictionary(kind => kind, kind => errors[kind]));
    }

    /// <summary>Returns the banner (if any) and the always-visible status pill for the current capture state.</summary>
    internal static (StatusMessage? Banner, StatusMessage Pill) DescribeTrackingStatus(TrackingStatusSnapshot status)
    {
        StatusMessage? inputBanner = status.InputListenerError is { } inputError
            ? new StatusMessage($"Shortcuts unavailable: {inputError}", StatusTone.Warning)
            : null;
        var paused = new StatusMessage(
            status.PauseBinding is { } pause ? $"Paused · {pause} to resume" : "Paused", StatusTone.Neutral);
        if (status.CaptureLoopError is { } loopError)
        {
            return (new StatusMessage($"Capture stopped: {loopError}", StatusTone.Error),
                new StatusMessage("Capture stopped", StatusTone.Error));
        }

        if (!status.Monitoring)
        {
            return (inputBanner, paused);
        }

        if (!status.HasRegions)
        {
            // With no areas at all, the first-run card explains setup; otherwise only the tracked ability lacks one.
            return (status.AnyRegions
                    ? new StatusMessage("The tracked ability has no area · select it from Areas ▾ or track All.", StatusTone.Neutral)
                    : null,
                new StatusMessage("Areas not set", StatusTone.Neutral));
        }

        if (status.Errors.SharedError is { } sharedError)
        {
            return (new StatusMessage($"Capture failed: {sharedError}", StatusTone.Error),
                new StatusMessage("Capture failed", StatusTone.Error));
        }

        if (status.Errors.WaitingForGame)
        {
            return (new StatusMessage(WaitingForGameBanner, StatusTone.Warning),
                new StatusMessage("Waiting for game", StatusTone.Warning));
        }

        if (status.CaptureStalled)
        {
            return (new StatusMessage($"No capture frames · {status.Backend}", StatusTone.Warning),
                new StatusMessage("No capture frames", StatusTone.Warning));
        }

        return (inputBanner, new StatusMessage(status.CaptureRate <= 0
            ? $"Detecting · {status.Backend} · starting"
            : $"Detecting · {status.Backend} · {status.CaptureRate:F0}/{status.TargetCaptureRate} Hz", StatusTone.Good));
    }

    /// <summary>True when capture should be producing frames but none arrived for two seconds.</summary>
    private bool CaptureStalled(bool hasRegions)
    {
        if (!_monitoring || !hasRegions || HasRecentCapture())
        {
            return false;
        }

        // Give a fresh start a moment before reporting missing frames.
        return Interlocked.Read(ref _lastCapturedFrame) != 0 ||
            !CaptureIsRecent(Stopwatch.GetTimestamp(), _monitoringStarted, Stopwatch.Frequency);
    }

    private void ShowTrackingStatus((StatusMessage? Banner, StatusMessage Pill) status)
    {
        _statusPill.Text = status.Pill.Text;
        _statusPill.Tone = status.Pill.Tone;
        var pillTip = status.Banner is { } detail && detail.Text != status.Pill.Text
            ? $"{status.Pill.Text}\n{detail.Text}"
            : status.Pill.Text;
        if (_toolTip.GetToolTip(_statusPill) != pillTip)
        {
            _toolTip.SetToolTip(_statusPill, pillTip);
        }

        if (status.Banner is { } banner)
        {
            _statusBanner.Text = banner.Text;
            _statusBanner.Tone = banner.Tone;
            if (_toolTip.GetToolTip(_statusBanner) != banner.Text)
            {
                _toolTip.SetToolTip(_statusBanner, banner.Text);
            }
        }

        var show = status.Banner is not null;
        if (_statusBannerShown == show)
        {
            return;
        }

        // The Tracking page reserves the banner's height, so showing it never resizes the window.
        _statusBannerShown = show;
        _statusBanner.Visible = show;
    }
}

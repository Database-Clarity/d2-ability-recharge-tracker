using System.Drawing;

namespace D2AbilityTracker;

internal enum AbilityKind
{
    Grenade,
    Melee,
    ClassAbility
}

internal enum AbilityState
{
    Unknown,
    Ready,
    Consumed,
    Recharging,
    Complete
}

internal sealed class AbilitySlotSettings
{
    public AbilityKind Kind { get; set; }
    public Rectangle? Region { get; set; }
    public Rectangle? MonitorBounds { get; set; }
    public string? MonitorDeviceName { get; set; }
    public Rectangle? GameWindowBounds { get; set; }
}

internal sealed class TrackerSettings
{
    public List<AbilitySlotSettings> Slots { get; set; } =
    [
        new() { Kind = AbilityKind.Grenade },
        new() { Kind = AbilityKind.Melee },
        new() { Kind = AbilityKind.ClassAbility }
    ];

    // Kept to migrate region settings saved by earlier versions.
    public Rectangle? Region { get; set; }
    public Rectangle? RegionMonitorBounds { get; set; }
    public string? RegionMonitorDeviceName { get; set; }
    public float[]? ReadySignature { get; set; }
    public float[]? RechargingSignature { get; set; }
    public string Hotkey { get; set; } = "F8";
    public InputBinding? PauseBinding { get; set; }
    public InputBinding? AbilityUseBinding { get; set; }
    public InputBinding? RepeatTestBinding { get; set; }
    public Dictionary<AbilityKind, InputBinding> SelectionBindings { get; set; } = [];
    public int CaptureRate { get; set; } = 120;
    public AbilityKind TestAbility { get; set; } = AbilityKind.Grenade;
    public int TestCycles { get; set; } = 5;
    public decimal TestDelaySeconds { get; set; } = 1;
    public string OverlayAnchor { get; set; } = "Bottom right";
}

internal readonly record struct AbilitySignal(
    bool Charged,
    bool CooldownLine,
    float ChargedBackgroundRatio,
    float CooldownLineRatio,
    float BottomBorderThickness = 0,
    float? ChargeProgress = null,
    float? FillHue = null,
    Color? FillColor = null,
    bool UseFlash = false,
    bool PixelEvidence = false);

internal sealed class AbilitySlotTracker
{
    private DateTime? _rechargeStarted;
    private DateTime? _completionCandidateSince;
    private float? _bottomBorderBaseline;
    private bool _recovering;
    private DateTime? _lastProgressAt;
    private float? _lastProgress;
    private int _progressSamples;
    private readonly RechargeEstimate _estimate = new();
    private float? _readyHue;
    private Color? _readyColor;
    private DateTime? _readyCandidateSince;

    public AbilityKind Kind { get; }
    public AbilityState State { get; private set; } = AbilityState.Unknown;
    public bool IsCharged { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    public TimeSpan? CurrentDuration { get; private set; }
    public float? ChargeProgress { get; private set; }
    public string? TrackingWarning { get; private set; }
    public bool MeasurementDiscarded { get; private set; }
    public TimeSpan? EstimatedDuration => _estimate.TotalDuration;
    public double? EstimateConfidence => _estimate.Confidence;

    public float ProgressSearchTolerance(DateTime timestamp)
    {
        if (_lastProgressAt is not { } seen || _rechargeStarted is not { } started || seen <= started)
        {
            return 0.12f;
        }

        var observedRate = (_lastProgress ?? 0) / (seen - started).TotalSeconds;
        return (float)Math.Min(1, 0.12 + Math.Max(0, (timestamp - seen).TotalSeconds) * observedRate * 2);
    }

    public AbilitySlotTracker(AbilityKind kind)
    {
        Kind = kind;
    }

    public void ResetCycle()
    {
        _rechargeStarted = null;
        _completionCandidateSince = null;
        _bottomBorderBaseline = null;
        _recovering = false;
        _lastProgressAt = null;
        _lastProgress = null;
        _progressSamples = 0;
        ChargeProgress = null;
        TrackingWarning = null;
        MeasurementDiscarded = false;
        _estimate.Reset();
        _readyHue = null;
        _readyColor = null;
        _readyCandidateSince = null;
        IsCharged = false;
        CurrentDuration = null;
        State = AbilityState.Unknown;
    }

    public bool Observe(AbilitySignal signal, DateTime timestamp)
    {
        if (signal.FillHue is { } hue)
        {
            if (_readyHue is { } readyHue)
            {
                var difference = Math.Abs(hue - readyHue);
                signal = signal with { Charged = signal.Charged && Math.Min(difference, 360 - difference) < 25 };
            }
            else if (signal.Charged)
            {
                _readyHue = hue;
            }
        }

        if (signal.FillColor is { } color)
        {
            if (_readyColor is { } readyColor)
            {
                signal = signal with
                {
                    Charged = signal.Charged && Math.Abs(color.R - readyColor.R) < 30 &&
                        Math.Abs(color.G - readyColor.G) < 30 && Math.Abs(color.B - readyColor.B) < 30
                };
            }
            else if (signal.Charged)
            {
                _readyColor = color;
            }
        }

        if (signal.Charged) { _readyCandidateSince ??= timestamp; }
        else { _readyCandidateSince = null; }
        var confirmedReady = signal.Charged && (!signal.PixelEvidence ||
            timestamp - _readyCandidateSince >= TimeSpan.FromMilliseconds(150));
        IsCharged = confirmedReady;
        var startFlash = _rechargeStarted is null && (signal.UseFlash ||
            (!signal.PixelEvidence && _bottomBorderBaseline is { } baseline &&
                signal.BottomBorderThickness > baseline + 0.012f));
        if (_rechargeStarted is null && startFlash && State is AbilityState.Ready or AbilityState.Complete)
        {
            _rechargeStarted = timestamp;
            _completionCandidateSince = null;
            ChargeProgress = 0;
            TrackingWarning = null;
            MeasurementDiscarded = false;
            _recovering = false;
            _lastProgress = null;
            _lastProgressAt = null;
            _progressSamples = 0;
            _estimate.Reset();
        }

        if (_rechargeStarted is { } started)
        {
            CurrentDuration = timestamp - started;
            if (signal.CooldownLine && signal.ChargeProgress is { } progress && float.IsFinite(progress) && progress is >= 0 and <= 1)
            {
                if (_lastProgress is { } previous && progress < previous - 0.08f)
                {
                    MarkSignalLost(timestamp);
                    return false;
                }

                if (_recovering)
                {
                    _estimate.Reset();
                }

                _estimate.Observe(timestamp - started, progress);
                _lastProgress = progress;
                _lastProgressAt = timestamp;
                _progressSamples++;
                ChargeProgress = progress;
                _recovering = false;
                TrackingWarning = null;
                _completionCandidateSince = null;
            }
            else if (_progressSamples >= 2 &&
                (_recovering || _lastProgress is < 0.85f ||
                    (_lastProgressAt is { } lastSeen && timestamp - lastSeen > TimeSpan.FromMilliseconds(500))))
            {
                if (_recovering && confirmedReady && !signal.CooldownLine &&
                    (_lastProgress is < 0.8f || _lastProgressAt is { } seen &&
                        timestamp - seen > TimeSpan.FromSeconds(2)))
                {
                    TrackingWarning = "HUD returned charged; endpoint was obscured. Measurement discarded.";
                    MeasurementDiscarded = true;
                    _estimate.Reset();
                    _rechargeStarted = null;
                    CurrentDuration = null;
                    ChargeProgress = null;
                    State = AbilityState.Ready;
                    return false;
                }

                if (_lastProgress is < 0.8f || _lastProgressAt is { } lastVisible &&
                    timestamp - lastVisible > TimeSpan.FromSeconds(2))
                {
                    MarkSignalLost(timestamp);
                    return false;
                }
            }

            // Pixel visibility loss is not an endpoint; retain the first explicit completion cue.
            if (signal.CooldownLine || startFlash)
            {
                _completionCandidateSince = null;
            }
            else if (!signal.Charged && (!signal.PixelEvidence || signal.UseFlash))
            {
                _completionCandidateSince ??= timestamp;
            }

            if (signal.PixelEvidence && signal.Charged && _lastProgress is >= 0.8f)
            {
                _completionCandidateSince ??= timestamp;
            }

            if (confirmedReady && !startFlash && (!signal.PixelEvidence || _lastProgress is >= 0.8f) &&
                _completionCandidateSince is { } blank &&
                (_lastProgressAt is null || timestamp - _lastProgressAt <= TimeSpan.FromSeconds(2)))
            {
                LastDuration = blank - started;
                CurrentDuration = null;
                _rechargeStarted = null;
                _completionCandidateSince = null;
                _bottomBorderBaseline = signal.BottomBorderThickness;
                _recovering = false;
                TrackingWarning = null;
                State = AbilityState.Complete;
                ChargeProgress = 1;
                _lastProgress = null;
                _lastProgressAt = null;
                _progressSamples = 0;
                _estimate.Reset();
                return true;
            }

            CurrentDuration = timestamp - started;
            State = AbilityState.Recharging;
        }
        else if (signal.Charged)
        {
            _bottomBorderBaseline = signal.BottomBorderThickness;
            if (State != AbilityState.Complete)
            {
                State = AbilityState.Ready;
            }
        }
        else if (State is AbilityState.Unknown or AbilityState.Ready or AbilityState.Complete)
        {
            State = AbilityState.Consumed;
        }

        return false;
    }

    public void MarkSignalLost(DateTime timestamp)
    {
        if (_rechargeStarted is { } started)
        {
            CurrentDuration = timestamp - started;
            State = AbilityState.Recharging;
            _recovering = true;
            _completionCandidateSince = null;
            IsCharged = false;
            _estimate.Reset();
            TrackingWarning = "HUD signal lost; keeping timer running while waiting for charge progress.";
        }
    }
}

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
    public bool RegionAutoDetected { get; set; }
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
    public InputBinding? OverlayToggleBinding { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public bool CheckForUpdatesOnStartup { get; set; } = true;
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
    bool PixelEvidence = false,
    bool Blank = false,
    float? FillLuminance = null,
    TwinReading? Twin = null);

internal enum MeasurementSource
{
    Event,
    EventRefinedByModel,
    Model
}

/// <summary>Running medians of each side strip's top and bottom slot borders.</summary>
internal sealed class SlotBorderLearner
{
    private const int Capacity = 151;
    private readonly float[][] _values = [new float[Capacity], new float[Capacity], new float[Capacity], new float[Capacity]];
    private readonly int[] _counts = new int[4];
    private readonly int[] _next = new int[4];
    private int _sinceMedian;
    private TwinSideGuide? _left;
    private TwinSideGuide? _right;

    public void Reset()
    {
        Array.Clear(_counts);
        Array.Clear(_next);
        _sinceMedian = 0;
        _left = null;
        _right = null;
    }

    public void Observe(TwinReading reading)
    {
        Add(0, reading.Left.Top);
        Add(1, reading.Left.Bottom);
        Add(2, reading.Right.Top);
        Add(3, reading.Right.Bottom);
        if (++_sinceMedian < 16 && _left is not null)
        {
            return;
        }

        _sinceMedian = 0;
        if (_counts.Any(count => count < 15))
        {
            return;
        }

        var leftTop = Median(0);
        var leftBottom = Median(1);
        var rightTop = Median(2);
        var rightBottom = Median(3);
        if (leftBottom - leftTop > 8 && rightBottom - rightTop > 8)
        {
            _left = new TwinSideGuide(leftTop, leftBottom);
            _right = new TwinSideGuide(rightTop, rightBottom);
        }
    }

    public bool TryGet(out TwinSideGuide left, out TwinSideGuide right)
    {
        left = _left ?? default;
        right = _right ?? default;
        return _left is not null && _right is not null;
    }

    private void Add(int series, float? value)
    {
        if (value is not { } row)
        {
            return;
        }

        _values[series][_next[series]] = row;
        _next[series] = (_next[series] + 1) % Capacity;
        _counts[series] = Math.Min(Capacity, _counts[series] + 1);
    }

    private float Median(int series)
    {
        Span<float> copy = stackalloc float[_counts[series]];
        _values[series].AsSpan(0, _counts[series]).CopyTo(copy);
        copy.Sort();
        return copy[copy.Length / 2];
    }
}

internal sealed class AbilitySlotTracker
{
    private DateTime? _rechargeStarted;
    private DateTime? _completionCandidateSince;
    private float? _bottomBorderBaseline;
    private bool _recovering;
    private DateTime? _lastProgressAt;
    private float? _lastProgress;
    private int _progressSamples;
    private float? _readyHue;
    private Color? _readyColor;
    private DateTime? _readyCandidateSince;
    private DateTime? _useObservedAt;
    private float? _consumedBorderBaseline;
    private readonly Queue<(DateTime Time, float Luminance)> _recentFill = new();
    private float? _previousFillLuminance;
    private DateTime? _softEndCandidate;
    private DateTime? _softEndPreviousFrame;
    private bool _dipping;
    private readonly RechargeModel _model = new();
    private readonly SlotBorderLearner _borders = new();
    private DateTime? _lastBoundaryAt;
    private DateTime? _lastModelSampleAt;
    private DateTime? _cycleUseAt;
    private DateTime? _lastObservedAt;
    private DateTime? _startPreviousFrame;
    private DateTime? _completionPreviousFrame;
    private double _startShiftSeconds;
    private DateTime? _lastReadyAt;

    // Maps border-relative boundary position to linear recharge progress. Measured on reference footage: the
    // boundary leaves just below the bottom border and settles just under the top border.
    internal const float ProgressGain = 0.982f;
    internal const float ProgressOffset = 0.0264f;
    private const double EndGapForModel = 0.1;

    public AbilityKind Kind { get; }
    public AbilityState State { get; private set; } = AbilityState.Unknown;
    public bool IsCharged { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    public TimeSpan? LastUncertainty { get; private set; }
    public MeasurementSource? LastSource { get; private set; }
    public TimeSpan? CurrentDuration { get; private set; }
    public float? ChargeProgress { get; private set; }
    public float? DisplayProgress { get; private set; }
    public string? TrackingWarning { get; private set; }
    public bool MeasurementDiscarded { get; private set; }
    public TimeSpan? EstimatedDuration { get; private set; }
    public TimeSpan? EstimateUncertainty { get; private set; }
    public double? ModelStartOffsetSeconds => _model.SecondsAt(0);

    public TwinGuide? Guide(DateTime timestamp)
    {
        if (!_borders.TryGet(out var left, out var right))
        {
            return null;
        }

        if (_rechargeStarted is { } started && _lastModelSampleAt is { } seen &&
            timestamp - seen < TimeSpan.FromSeconds(1.5))
        {
            var seconds = (timestamp - started).TotalSeconds;
            if (_model.Predict(seconds) is { } predicted && _model.Tolerance(seconds) is { } tolerance)
            {
                return new TwinGuide(left, right, (float)((predicted - ProgressOffset) / ProgressGain),
                    (float)(tolerance / ProgressGain));
            }
        }

        return new TwinGuide(left, right);
    }

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
        ResetModel();
        _borders.Reset();
        _cycleUseAt = null;
        _lastObservedAt = null;
        _startPreviousFrame = null;
        _completionPreviousFrame = null;
        _readyHue = null;
        _readyColor = null;
        _readyCandidateSince = null;
        _useObservedAt = null;
        _consumedBorderBaseline = null;
        _recentFill.Clear();
        _previousFillLuminance = null;
        _softEndCandidate = null;
        _softEndPreviousFrame = null;
        _dipping = false;
        _lastReadyAt = null;
        IsCharged = false;
        CurrentDuration = null;
        State = AbilityState.Unknown;
    }

    public bool Observe(AbilitySignal signal, DateTime timestamp)
    {
        var previousObservation = _lastObservedAt;
        _lastObservedAt = timestamp;
        if (signal.Twin is { } twinReading && (_rechargeStarted is null || !_borders.TryGet(out _, out _)))
        {
            _borders.Observe(twinReading);
        }

        // The endpoint shows as a brief fill-luminance dip; compare with a short lookback so a lower capture
        // rate that lands mid-dip still sees it.
        var fillDisappeared = false;
        var fillDipped = false;
        while (_recentFill.Count > 0 && (_recentFill.Count > 8 ||
            timestamp - _recentFill.Peek().Time > TimeSpan.FromMilliseconds(120)))
        {
            _recentFill.Dequeue();
        }

        if (signal.FillLuminance is { } luminance)
        {
            foreach (var (_, seenLuminance) in _recentFill)
            {
                fillDipped |= seenLuminance - luminance > 0.09f;
            }

            fillDisappeared = _previousFillLuminance is { } previousLuminance && previousLuminance - luminance > 0.12f;
            _recentFill.Enqueue((timestamp, luminance));
        }

        _previousFillLuminance = signal.FillLuminance;

        if (signal.FillHue is { } hue)
        {
            if (_readyHue is { } readyHue)
            {
                var difference = Math.Abs(hue - readyHue);
                signal = signal with { Charged = signal.Charged && Math.Min(difference, 360 - difference) < 25 };
            }
            else if (_useObservedAt is not null)
            {
                State = AbilityState.Consumed;
                if (timestamp - _useObservedAt > TimeSpan.FromSeconds(10))
                {
                    TrackingWarning = "Ability use seen, but recharge-start border pulse was not captured. Reset detection with the ability charged.";
                }
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
        if (confirmedReady && _rechargeStarted is null)
        {
            _lastReadyAt = timestamp;
        }

        if (signal.PixelEvidence && _rechargeStarted is null && _useObservedAt is null &&
            (signal.UseFlash && State is AbilityState.Ready or AbilityState.Complete ||
                signal.Blank && _lastReadyAt is { } lastReady && timestamp - lastReady < TimeSpan.FromSeconds(1.5)))
        {
            _useObservedAt = timestamp;
            _consumedBorderBaseline = null;
            State = AbilityState.Consumed;
        }

        var startFlash = _rechargeStarted is null && (signal.PixelEvidence
            ? _useObservedAt is not null && !signal.UseFlash && (_consumedBorderBaseline ?? _bottomBorderBaseline) is { } consumed &&
                signal.BottomBorderThickness > consumed + 0.012f
            : _bottomBorderBaseline is { } baseline && signal.BottomBorderThickness > baseline + 0.012f);
        if (_useObservedAt is not null && signal.Blank && !signal.UseFlash && !startFlash)
        {
            _consumedBorderBaseline = Math.Min(_consumedBorderBaseline ?? signal.BottomBorderThickness,
                signal.BottomBorderThickness);
        }

        if (_rechargeStarted is null && startFlash &&
            (_useObservedAt is not null || State is AbilityState.Ready or AbilityState.Complete))
        {
            _cycleUseAt = _useObservedAt;
            _useObservedAt = null;
            _consumedBorderBaseline = null;
            _rechargeStarted = timestamp;
            _startPreviousFrame = previousObservation;
            _softEndCandidate = null;
            _completionCandidateSince = null;
            ChargeProgress = 0;
            TrackingWarning = null;
            MeasurementDiscarded = false;
            _recovering = false;
            _lastProgress = null;
            _lastProgressAt = null;
            _progressSamples = 0;
            ResetModel();
        }

        if (_rechargeStarted is { } started)
        {
            var modelAccepted = ObserveModel(signal, timestamp, started);
            CurrentDuration = Elapsed(timestamp, started);
            if (modelAccepted)
            {
                _lastProgress = DisplayProgress;
                _lastProgressAt = timestamp;
                _progressSamples++;
                ChargeProgress = signal.CooldownLine ? signal.ChargeProgress ?? DisplayProgress : DisplayProgress;
                _recovering = false;
                TrackingWarning = null;
                _completionCandidateSince = null;
            }
            else if (signal.CooldownLine && signal.ChargeProgress is { } progress && float.IsFinite(progress) && progress is >= 0 and <= 1)
            {
                if (!_model.IsReady && _lastProgress is { } previous && progress < previous - 0.08f)
                {
                    MarkSignalLost(timestamp);
                    return false;
                }

                _lastProgress = _model.IsReady ? Math.Max(_lastProgress ?? 0, progress) : progress;
                _lastProgressAt = timestamp;
                _progressSamples++;
                ChargeProgress = progress;
                if (!_model.IsReady)
                {
                    DisplayProgress = progress;
                }

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
                    if (TryCompleteFromModel(signal, started, timestamp))
                    {
                        return true;
                    }

                    TrackingWarning = "HUD returned charged; endpoint was obscured. Measurement discarded.";
                    MeasurementDiscarded = true;
                    ResetModel();
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
            if (signal.CooldownLine || startFlash ||
                _completionCandidateSince is { } staleCandidate && !signal.Charged &&
                timestamp - staleCandidate > TimeSpan.FromSeconds(0.4))
            {
                _completionCandidateSince = null;
            }
            else if (!signal.Charged && (!signal.PixelEvidence ||
                (signal.Blank || fillDisappeared) && _lastProgress is >= 0.8f))
            {
                SetCompletionCandidate(timestamp, previousObservation);
            }

            if (signal.PixelEvidence && !signal.Charged && fillDipped && _lastProgress is >= 0.8f &&
                !_dipping)
            {
                _softEndCandidate = timestamp;
                _softEndPreviousFrame = previousObservation;
            }

            _dipping = signal.PixelEvidence && !signal.Charged && fillDipped;

            if (signal.PixelEvidence && signal.Charged && _lastProgress is >= 0.8f)
            {
                // A weaker dip shortly before the ready color is the endpoint seen between captures.
                if (_completionCandidateSince is null && _softEndCandidate is { } dip &&
                    timestamp - dip <= TimeSpan.FromSeconds(0.35))
                {
                    SetCompletionCandidate(dip, _softEndPreviousFrame);
                }

                SetCompletionCandidate(timestamp, previousObservation);
            }

            if (confirmedReady && !startFlash && (!signal.PixelEvidence || _lastProgress is >= 0.8f) &&
                _completionCandidateSince is { } blank &&
                (_lastProgressAt is null || timestamp - _lastProgressAt <= TimeSpan.FromSeconds(2)))
            {
                FinishFromEvent(signal, started, blank);
                return true;
            }

            CurrentDuration = Elapsed(timestamp, started);
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
            CurrentDuration = Elapsed(timestamp, started);
            State = AbilityState.Recharging;
            _recovering = true;
            _completionCandidateSince = null;
            IsCharged = false;
            TrackingWarning = "HUD signal lost; keeping timer running while waiting for charge progress.";
        }
    }

    private void ResetModel()
    {
        _model.Reset();
        _lastBoundaryAt = null;
        _lastModelSampleAt = null;
        _startShiftSeconds = 0;
        DisplayProgress = null;
        EstimatedDuration = null;
        EstimateUncertainty = null;
    }

    private TimeSpan Elapsed(DateTime timestamp, DateTime started) =>
        timestamp - started + TimeSpan.FromSeconds(_startShiftSeconds);

    private void SetCompletionCandidate(DateTime timestamp, DateTime? previousObservation)
    {
        if (_completionCandidateSince is null)
        {
            _completionCandidateSince = timestamp;
            _completionPreviousFrame = previousObservation;
        }
    }

    private float? TwinProgress(TwinReading twin, double seconds)
    {
        if (!_borders.TryGet(out var leftBorders, out var rightBorders))
        {
            return null;
        }

        var left = HudTwin.RawProgress(twin.Left.Line, leftBorders) * ProgressGain + ProgressOffset;
        var right = HudTwin.RawProgress(twin.Right.Line, rightBorders) * ProgressGain + ProgressOffset;
        if (left is { } l && right is { } r)
        {
            if (Math.Abs(l - r) <= 0.05f)
            {
                return (l + r) / 2;
            }

            if (_model.Predict(seconds) is not { } predicted)
            {
                return null;
            }

            return Math.Abs(l - predicted) < Math.Abs(r - predicted) ? l : r;
        }

        return left ?? right;
    }

    private bool ObserveModel(AbilitySignal signal, DateTime timestamp, DateTime started)
    {
        var seconds = (timestamp - started).TotalSeconds;
        var progress = signal.Twin is { } twin ? TwinProgress(twin, seconds) : signal.CooldownLine ? signal.ChargeProgress : null;
        if (signal.Twin is { } seen && (seen.Left.Line is not null || seen.Right.Line is not null) || signal.CooldownLine)
        {
            _lastBoundaryAt = timestamp;
        }

        var accepted = false;
        if (progress is { } p && float.IsFinite(p) && p is >= 0.03f and <= 0.97f)
        {
            var consistent = _model.Predict(seconds) is not { } predicted ||
                Math.Abs(p - predicted) <= (_model.Tolerance(seconds) ?? 1);
            var stale = _lastModelSampleAt is not { } last || timestamp - last > TimeSpan.FromSeconds(3);
            if (consistent || stale)
            {
                _model.Add(seconds, p);
                _lastModelSampleAt = timestamp;
                accepted = consistent || !_model.IsReady;
            }
        }

        UpdateEstimate(timestamp, started);
        if (_model.IsReady && _model.Predict(seconds) is { } display)
        {
            DisplayProgress = (float)Math.Clamp(display, 0, 1);
        }
        else if (accepted)
        {
            DisplayProgress = progress;
        }

        return accepted;
    }

    private void UpdateEstimate(DateTime timestamp, DateTime started)
    {
        _startShiftSeconds = StartShift(started);
        if (_model.SecondsAt(1) is { } end && _model.SecondsUncertaintyAt(1) is { } uncertainty)
        {
            EstimatedDuration = TimeSpan.FromSeconds(Math.Max(0, end + _startShiftSeconds));
            EstimateUncertainty = TimeSpan.FromSeconds(uncertainty);
        }
        else
        {
            EstimatedDuration = null;
            EstimateUncertainty = null;
        }
    }

    // A start event observed late (for example, HUD obscured when the ability was used) shows up as the fitted
    // line reaching zero progress before the recorded start. Move the start back, bounded by the observed use.
    private double StartShift(DateTime started)
    {
        if (_model.ObservedMaximumProgress < 0.15 || _model.SecondsAt(0) is not { } zero ||
            _model.SecondsUncertaintyAt(0) is not { } sigma || zero >= -Math.Max(0.25, 3 * sigma))
        {
            return 0;
        }

        var limit = _cycleUseAt is { } use ? Math.Max(0, (started - use).TotalSeconds) : double.MaxValue;
        return Math.Min(-zero, limit);
    }

    private void Complete(AbilitySignal signal, TimeSpan duration, TimeSpan uncertainty, MeasurementSource source)
    {
        LastDuration = duration;
        LastUncertainty = uncertainty;
        LastSource = source;
        CurrentDuration = null;
        _rechargeStarted = null;
        _completionCandidateSince = null;
        _completionPreviousFrame = null;
        _startPreviousFrame = null;
        _softEndCandidate = null;
        _dipping = false;
        _cycleUseAt = null;
        _bottomBorderBaseline = signal.BottomBorderThickness;
        _recovering = false;
        TrackingWarning = null;
        MeasurementDiscarded = false;
        State = AbilityState.Complete;
        ChargeProgress = 1;
        _lastProgress = null;
        _lastProgressAt = null;
        _progressSamples = 0;
        ResetModel();
        DisplayProgress = 1;
    }

    private void FinishFromEvent(AbilitySignal signal, DateTime started, DateTime blank)
    {
        var shift = TimeSpan.FromSeconds(_startShiftSeconds);
        var eventSeconds = (blank - started).TotalSeconds;
        var endGap = _completionPreviousFrame is { } before ? (blank - before).TotalSeconds : 0;
        var startGap = _startPreviousFrame is { } beforeStart ? (started - beforeStart).TotalSeconds : 0;
        if (endGap > EndGapForModel && _model.IsReady && _model.SecondsAt(1) is { } modelEnd &&
            _model.SecondsUncertaintyAt(1) is { } modelSigma && modelSigma < endGap)
        {
            var lower = eventSeconds - endGap;
            var refined = Math.Clamp(modelEnd, lower, eventSeconds);
            Complete(signal, TimeSpan.FromSeconds(refined) + shift,
                TimeSpan.FromSeconds(Math.Max(modelSigma, startGap / 2)), MeasurementSource.EventRefinedByModel);
            return;
        }

        var sigma = Math.Sqrt(endGap * endGap + startGap * startGap) / 2;
        if (_startShiftSeconds > 0 && _model.SecondsUncertaintyAt(0) is { } startSigma)
        {
            sigma = Math.Sqrt(sigma * sigma + startSigma * startSigma);
        }

        Complete(signal, blank - started + shift, TimeSpan.FromSeconds(sigma), MeasurementSource.Event);
    }

    private bool TryCompleteFromModel(AbilitySignal signal, DateTime started, DateTime timestamp)
    {
        if (!_model.IsReady || _model.ObservedMaximumProgress < 0.5 || _model.SecondsAt(1) is not { } end ||
            _model.SecondsUncertaintyAt(1) is not { } sigma || sigma > 0.5)
        {
            return false;
        }

        var lower = _lastBoundaryAt is { } boundary ? (boundary - started).TotalSeconds : 0;
        var seconds = Math.Clamp(end, lower, (timestamp - started).TotalSeconds);
        Complete(signal, TimeSpan.FromSeconds(seconds + _startShiftSeconds), TimeSpan.FromSeconds(sigma),
            MeasurementSource.Model);
        return true;
    }
}

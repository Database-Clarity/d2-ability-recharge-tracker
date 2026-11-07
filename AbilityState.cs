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
    // Legacy single ability-use binding, expanded to each ability while settings are loaded.
    public InputBinding? AbilityUseBinding { get; set; }
    public Dictionary<AbilityKind, InputBinding> AbilityUseBindings { get; set; } = [];
    public InputBinding? RepeatTestBinding { get; set; }
    /// <summary>Shortcuts that track only one ability (pressing again tracks all).</summary>
    public Dictionary<AbilityKind, InputBinding> SoloBindings { get; set; } = [];
    /// <summary>When set, only this ability is captured and tracked.</summary>
    public AbilityKind? SoloAbility { get; set; }
    public bool PauseShortcutCleared { get; set; }
    public bool OverlayShortcutCleared { get; set; }
    public InputBinding? OverlayToggleBinding { get; set; }
    public bool OverlayVisible { get; set; } = true;
    public bool OverlayTwoLines { get; set; } = true;
    /// <summary>Custom overlay top-left in screen pixels; null uses <see cref="OverlayAnchor"/>.</summary>
    public Point? OverlayLocation { get; set; }
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public string? LastChangelogVersion { get; set; }
    public int CaptureRate { get; set; } = 120;
    public AbilityKind TestAbility { get; set; } = AbilityKind.Grenade;
    public int TestCycles { get; set; } = 5;
    public decimal TestDelaySeconds { get; set; } = 1;
    public decimal TestHoldSeconds { get; set; } = 0.12m;
    public string OverlayAnchor { get; set; } = "Bottom right";

    public void MigrateAbilityUseBinding()
    {
        AbilityUseBindings ??= [];
        if (AbilityUseBinding is not { } legacyBinding) return;
        foreach (var ability in Enum.GetValues<AbilityKind>())
            AbilityUseBindings.TryAdd(ability, legacyBinding);
        AbilityUseBinding = null;
    }
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
    TwinReading? Twin = null,
    float? FillSaturation = null);

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
    private readonly List<FillSample> _fillHistory = [];
    private DateTime? _firstStartAt;
    private float _pulseMaximum;
    private float? _pulseDipMinimum;
    private DateTime? _episodeOnset;
    private float? _postChargeBorderMinimum;
    private bool _recastFlashSeen;
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

    // The gray recharge bar vanishes before the charged fill is confirmed, so the end is found by looking back.
    private const double EndLookback = 1.5;
    private const int FadeLookbackFrames = 8;
    private const float GrayBarDropThreshold = 0.09f;
    private const float GrayBarFadeFraction = 0.7f;
    private const float TintOnsetThreshold = 0.03f;
    private const float TintStepThreshold = 0.02f;
    private const double MinimumRechargeSeconds = 1;
    private const float RecastBarStep = 0.025f;
    // Ability-use animations pulse the border before the real recharge-start bar, after a dip back to thin.
    private const double PulseWindowSeconds = 1.5;
    private const double PulseDipSeconds = 0.25;
    private const float PulseStep = 0.012f;
    private const float ThinBorder = 0.02f;
    private const float BarBorder = 0.028f;

    private readonly record struct FillSample(DateTime Time, DateTime? Previous, float Luminance, float Saturation);

    public AbilityKind Kind { get; }
    public AbilityState State { get; private set; } = AbilityState.Unknown;
    public bool IsCharged { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    /// <summary>How far the last measurement moved the observed start earlier (a late start flash).</summary>
    public TimeSpan LastStartShift { get; private set; }
    public TimeSpan? LastUncertainty { get; private set; }
    public MeasurementSource? LastSource { get; private set; }
    /// <summary>Total recharge time fitted from samples between 10% and 90% charge.</summary>
    public TimeSpan? LastRegressionDuration { get; private set; }
    public TimeSpan? LastRegressionUncertainty { get; private set; }
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
        _fillHistory.Clear();
        _firstStartAt = null;
        _pulseDipMinimum = null;
        _episodeOnset = null;
        _postChargeBorderMinimum = null;
        _recastFlashSeen = false;
        _pendingRecast = null;
        _lastReadyAt = null;
        IsCharged = false;
        CurrentDuration = null;
        State = AbilityState.Unknown;
    }

    public bool Observe(AbilitySignal signal, DateTime timestamp)
    {
        TrackRecast(signal, timestamp);
        var completed = ObserveCore(signal, timestamp);
        return ConfirmRecast(signal, timestamp) || completed;
    }

    /// <summary>
    /// An ability recast right after the charge returned (too soon for the charged fill to be confirmed) shows as a
    /// new bottom-bar flash while the old recharge is still open. The close is provisional: the gray bar's end must
    /// be found before that brief charge, and the slot must then keep recharging (never confirmed charged) for a
    /// second. A completion animation looks alike at first, but the slot goes on to stay charged, so it is left to
    /// the normal completion.
    /// </summary>
    private void TrackRecast(AbilitySignal signal, DateTime timestamp)
    {
        if (_pendingRecast is not null || !signal.PixelEvidence || _rechargeStarted is not { } started ||
            _episodeOnset is not { } onset || _postChargeBorderMinimum is not { } minimum || !_recastFlashSeen ||
            signal.BottomBorderThickness < minimum + RecastBarStep ||
            !(LastDuration is { } expected ? timestamp - started >= expected * 0.8 : _lastProgress is >= 0.8f) ||
            !TryFindGrayBarEnd(onset, out var end, out var previous, requireCue: true))
        {
            return;
        }

        _pendingRecast = new PendingRecast(started, timestamp, _lastObservedAt, signal.BottomBorderThickness, end,
            previous);
    }

    private bool ConfirmRecast(AbilitySignal signal, DateTime timestamp)
    {
        if (_pendingRecast is not { } pending)
        {
            return false;
        }

        if (_rechargeStarted != pending.Started || IsCharged)
        {
            _pendingRecast = null;
            return false;
        }

        if (timestamp - pending.Flash < RecastConfirmation)
        {
            return false;
        }

        _pendingRecast = null;
        _completionPreviousFrame = pending.EndPrevious;
        FinishFromEvent(signal, pending.Started, pending.End);
        _useObservedAt = null;
        BeginRecharge(pending.Flash, pending.FlashPrevious, pending.Border);
        _cycleUseAt = null;
        State = AbilityState.Recharging;
        CurrentDuration = Elapsed(timestamp, pending.Flash);
        return true;
    }

    private readonly record struct PendingRecast(DateTime Started, DateTime Flash, DateTime? FlashPrevious,
        float Border, DateTime End, DateTime? EndPrevious);
    private PendingRecast? _pendingRecast;
    private static readonly TimeSpan RecastConfirmation = TimeSpan.FromSeconds(1);

    private void BeginRecharge(DateTime timestamp, DateTime? previousObservation, float border)
    {
        _consumedBorderBaseline = null;
        _rechargeStarted = timestamp;
        _firstStartAt = timestamp;
        _pulseMaximum = border;
        _pulseDipMinimum = null;
        _episodeOnset = null;
        _postChargeBorderMinimum = null;
        _recastFlashSeen = false;
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

    private bool ObserveCore(AbilitySignal signal, DateTime timestamp)
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
        if (signal.PixelEvidence && signal.FillLuminance is { } fillLuminance && signal.FillSaturation is { } fillSaturation)
        {
            _fillHistory.Add(new FillSample(timestamp, previousObservation, fillLuminance, fillSaturation));
            var oldest = timestamp - TimeSpan.FromSeconds(EndLookback + 0.5);
            var stale = _fillHistory.FindLastIndex(sample => sample.Time < oldest);
            if (stale >= 0) { _fillHistory.RemoveRange(0, stale + 1); }
        }

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
        // A briefly charged-looking fill while the bar was last seen well short of full is scenery, not the end.
        var implausiblyEarly = _rechargeStarted is not null && signal.PixelEvidence && _lastProgress is < 0.6f &&
            _lastProgressAt is { } progressSeenAt && timestamp - progressSeenAt < TimeSpan.FromSeconds(2) &&
            timestamp - _readyCandidateSince < TimeSpan.FromSeconds(0.75) &&
            (LastDuration is not { } previousDuration || timestamp - _rechargeStarted < previousDuration / 2);
        var confirmedReady = signal.Charged && !implausiblyEarly && (!signal.PixelEvidence ||
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
            BeginRecharge(timestamp, previousObservation, signal.BottomBorderThickness);
        }

        if (_rechargeStarted is { } started)
        {
            if (signal.PixelEvidence)
            {
                TrackStartPulse(signal, timestamp, previousObservation);
                started = _rechargeStarted ?? started;
                TrackChargedEpisode(signal, started);
            }

            if (signal.PixelEvidence && confirmedReady && !startFlash && _readyCandidateSince is { } onset &&
                (onset - started).TotalSeconds >= MinimumRechargeSeconds &&
                TryFindGrayBarEnd(onset, out var barEnd, out var barEndPrevious))
            {
                _completionPreviousFrame = barEndPrevious;
                FinishFromEvent(signal, started, barEnd);
                return true;
            }

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

    /// <summary>
    /// Finds when the gray recharge bar disappeared from the recent fill history, looking back from the charged
    /// fill's onset: the last frame where fill luminance mostly finished falling from its gray plateau, or, when the
    /// scene hides that, where the neutral gray first took on the charged tint and stayed tinted until the onset.
    /// Falls back to the onset itself. The caller only asks once the charged fill is confirmed, so scene changes
    /// earlier in a recharge are not mistaken for the end.
    /// </summary>
    private bool TryFindGrayBarEnd(DateTime onset, out DateTime end, out DateTime? previous, bool requireCue = false)
    {
        end = onset;
        previous = null;
        var window = _fillHistory.Where(sample => sample.Time >= onset - TimeSpan.FromSeconds(EndLookback) &&
            sample.Time <= onset).ToList();
        if (window.Count == 0) { return false; }
        var cue = -1;
        for (var i = 1; i < window.Count; i++)
        {
            var plateau = 0f;
            for (var back = Math.Max(0, i - FadeLookbackFrames); back < i; back++)
            {
                plateau = Math.Max(plateau, window[back].Luminance);
            }

            // The gray bar stays gone until the charged fill appears; a dip that rebounds is scenery.
            if (window[i].Luminance < plateau - GrayBarDropThreshold &&
                window[i - 1].Luminance >= plateau - GrayBarDropThreshold &&
                window.Skip(i).All(sample => sample.Luminance < plateau - GrayBarDropThreshold)) { cue = i; }
        }

        if (cue >= 0)
        {
            var plateau = 0f;
            for (var back = Math.Max(0, cue - FadeLookbackFrames); back < cue; back++)
            {
                plateau = Math.Max(plateau, window[back].Luminance);
            }

            var floor = window.Skip(cue).Min(sample => sample.Luminance);
            var target = plateau - GrayBarFadeFraction * (plateau - floor);
            while (cue < window.Count - 1 && window[cue].Luminance > target) { cue++; }
        }

        // The neutral gray taking on a tint that then lasts until the charged onset (a passing tint, such as a red
        // screen flash, does not last). Tint is a fallback: it can appear while the gray bar is still fading,
        // so it must not override an observed luminance disappearance.
        var tint = window.Count - 1;
        var neutral = window.Min(sample => sample.Saturation) + TintOnsetThreshold;
        if (window[tint].Saturation >= neutral)
        {
            while (tint > 0 && window[tint - 1].Saturation >= neutral) { tint--; }
            if (tint > 0 && window[tint].Saturation - window[tint - 1].Saturation >= TintStepThreshold &&
                cue < 0)
            {
                cue = tint;
            }
        }

        if (cue < 0 && requireCue) { return false; }
        if (cue < 0) { cue = window.Count - 1; }

        end = window[cue].Time;
        if (cue > 0) { previous = window[cue - 1].Time; }
        else if (_fillHistory.FindLastIndex(sample => sample.Time < window[0].Time) is var before and >= 0)
        {
            previous = _fillHistory[before].Time;
        }

        return true;
    }

    // A thickening that thins back and then rises again just after the start was the use pulse; the later rise is
    // the recharge-start bar.
    private void TrackStartPulse(AbilitySignal signal, DateTime timestamp, DateTime? previousObservation)
    {
        if (_firstStartAt is not { } first || (timestamp - first).TotalSeconds > PulseWindowSeconds)
        {
            return;
        }

        var border = signal.BottomBorderThickness;
        if (_pulseDipMinimum is not { } dip)
        {
            _pulseMaximum = Math.Max(_pulseMaximum, border);
            // Reference pulses return to thin within 0.17–0.22 s; real bars do so at 0.27 s or later.
            // This timing guard has little margin: do not broaden it to handle sustained animation pulses.
            if (border <= _pulseMaximum - PulseStep && border <= ThinBorder &&
                (timestamp - first).TotalSeconds <= PulseDipSeconds) { _pulseDipMinimum = border; }
            return;
        }

        _pulseDipMinimum = Math.Min(dip, border);
        if (border >= _pulseDipMinimum + PulseStep && border >= BarBorder)
        {
            _rechargeStarted = timestamp;
            _startPreviousFrame = previousObservation;
            _pulseMaximum = border;
            _pulseDipMinimum = null;
            ResetModel();
            _lastProgress = null;
            _lastProgressAt = null;
            _progressSamples = 0;
        }
    }

    // Remembers a charged fill that appeared mid-recharge and the thinnest border seen since it left.
    private void TrackChargedEpisode(AbilitySignal signal, DateTime started)
    {
        if (signal.Charged && _readyCandidateSince is { } onset && (onset - started).TotalSeconds >= MinimumRechargeSeconds)
        {
            _episodeOnset = onset;
            _postChargeBorderMinimum = null;
            _recastFlashSeen = false;
        }
        else if (_episodeOnset is not null && !signal.Charged)
        {
            _postChargeBorderMinimum = Math.Min(_postChargeBorderMinimum ?? signal.BottomBorderThickness,
                signal.BottomBorderThickness);
            _recastFlashSeen |= signal.UseFlash;
        }
    }

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
        LastStartShift = TimeSpan.FromSeconds(_startShiftSeconds);
        // The inverse slope measures a full charge without depending on either event timestamp.
        if (_model.FinalRegression() is { } regression)
        {
            LastRegressionDuration = regression.Duration;
            LastRegressionUncertainty = regression.Uncertainty;
        }
        else
        {
            LastRegressionDuration = null;
            LastRegressionUncertainty = null;
        }

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

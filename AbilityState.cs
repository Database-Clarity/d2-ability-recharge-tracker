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
    public InputBinding? MarkChargedBinding { get; set; }
    public bool MarkChargedShortcutCleared { get; set; }
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
    /// <summary>Where HUD recordings are saved; null uses the default under Documents.</summary>
    public string? RecordingFolder { get; set; }
    /// <summary>Record the HUD for the whole of each repeat test.</summary>
    public bool RecordRepeatTests { get; set; }
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
    float? FillSaturation = null,
    float? BottomBarOpacity = null);

internal enum MeasurementSource
{
    Event,
    EventRefinedByModel,
    Model,
    /// <summary>Tracking began mid-recharge: the start is extrapolated from the gray bar's progress line.</summary>
    Estimated
}

/// <summary>
/// Running estimates of each side strip's top and bottom slot borders: the densest 1-pixel cluster of recent readings.
/// A border reads within a fraction of a pixel; while a recharge is low (or nearly full) the gray bar's moving boundary
/// is often read in its place, spread over several pixels, and occasional misreads land anywhere. A median follows
/// the boundary when it dominates (tracking started early in a recharge was biased by about 0.1 of progress).
/// </summary>
internal sealed class SlotBorderLearner
{
    // About 30 s at 50 FPS: long enough that a border the gray bar's boundary covered for a few seconds still wins.
    private const int Capacity = 1501;
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

        var leftTop = Border(0);
        var leftBottom = Border(1);
        var rightTop = Border(2);
        var rightBottom = Border(3);
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

    private const float ClusterWidth = 1;

    private float Border(int series)
    {
        Span<float> copy = stackalloc float[_counts[series]];
        _values[series].AsSpan(0, _counts[series]).CopyTo(copy);
        copy.Sort();
        var (bestStart, bestCount, start) = (0, 0, 0);
        for (var end = 0; end < copy.Length; end++)
        {
            while (copy[end] - copy[start] > ClusterWidth) { start++; }
            if (end - start + 1 > bestCount) { (bestStart, bestCount) = (start, end - start + 1); }
        }

        return copy[bestStart + bestCount / 2];
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
    private readonly List<(float? Hue, Color? Color, bool Confirmed)> _readyReferences = [];
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
    // No ability recharge shown with a gray bar completes this fast; an earlier charged look is scenery.
    private const double MinimumRechargeSeconds = 3;
    private const float RecastBarStep = 0.025f;
    // Ability-use animations pulse the border before the real recharge-start bar, after a dip back to thin.
    private const double PulseWindowSeconds = 1.5;
    private const double PulseDipSeconds = 0.25;
    private const float PulseStep = 0.012f;
    private const float ThinBorder = 0.02f;
    private const float BarBorder = 0.028f;
    // Bar opacity (see AbilitySignalDetector.ReadBottomBarOpacity): the start bar measures about 0.55-0.65 and the thin
    // border 0.15-0.2. Hysteresis keeps a bar that wavers near the threshold from producing a second rising edge.
    private const float BarOpacityOn = 0.5f;
    private const float BarOpacityOff = 0.4f;
    private bool _barHigh;
    private float _candidateFloor;
    // A step this large is well above frame-to-frame noise on a steady bar (about ±0.05).
    private const float CandidateRise = 0.12f;
    private bool _readyLost;
    private bool _matchedConfirmed;
    // The charged look was accepted only because the progress line says the bar is full: the end needs a gray-bar cue.
    private bool _acceptedByPhase;
    private DateTime? _flashSince;
    private DateTime _flashStarted;
    private DateTime? _flashEndedAt;
    private static readonly TimeSpan FlashGap = TimeSpan.FromSeconds(0.1);
    private bool _flashQualifies;
    private bool _flashFollowsCharge;
    private static readonly TimeSpan MaxUseFlash = TimeSpan.FromSeconds(0.45);
    private DateTime? _lastConfirmedReadyAt;
    private static readonly TimeSpan UseToBarLimit = TimeSpan.FromSeconds(4);
    private readonly List<(DateTime Time, DateTime? Previous)> _startCandidates = [];
    // Threshold crossings among the candidates: the latest is the start until the fitted line can choose.
    private DateTime _latestCrossing;

    private readonly record struct FillSample(DateTime Time, DateTime? Previous, float Luminance, float Saturation);

    public AbilityKind Kind { get; }
    public AbilityState State { get; private set; } = AbilityState.Unknown;
    public bool IsCharged { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    /// <summary>How far the last measurement moved the observed start earlier (a late start flash).</summary>
    public TimeSpan LastStartShift { get; private set; }
    /// <summary>When the last completed measurement's recharge started (after any start shift).</summary>
    public DateTime? LastStartedAt { get; private set; }
    /// <summary>The running recharge's best start estimate (after any start shift).</summary>
    internal DateTime? RechargeOrigin => _rechargeStarted;
    public DateTime? CurrentStartedAt => _rechargeStarted is { } started ? started - TimeSpan.FromSeconds(_startShiftSeconds) : null;
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

    // UI snapshots only read scalar display properties; detection collections remain private.
    internal AbilitySlotTracker DisplaySnapshot() => (AbilitySlotTracker)MemberwiseClone();

    public AbilitySlotTracker(AbilityKind kind)
    {
        Kind = kind;
    }

    public void ResetCycle()
    {
        _manualLearning = false;
        _manualLearningSince = null;
        _manualSamples.Clear();
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
        _readyReferences.Clear();
        _runSince = null;
        _runLast = null;
        _candidateSince = null;
        _blankBorderBaseline = null;
        _lastBlankAt = null;
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
        _lastConfirmedReadyAt = null;
        _flashSince = null;
        _barHigh = false;
        _readyLost = false;
        _startCandidates.Clear();
        IsCharged = false;
        CurrentDuration = null;
        State = AbilityState.Unknown;
    }

    private DateTime? _manualLearningSince;
    private bool _manualLearning;
    internal bool LearningChargedReference => _manualLearning;
    private readonly List<AbilitySignal> _manualSamples = [];

    public void MarkCharged()
    {
        ResetCycle();
        _estimated = false;
        _estimateModel = null;
        _estimateRefitAt = null;
        _estimateSigma = null;
        _phaseSamples.Clear();
        _phaseRaw.Clear();
        _phaseLines.Clear();
        _rawLines.Clear();
        _matchedConfirmed = _acceptedByPhase = false;
        _flashQualifies = _flashFollowsCharge = false;
        _flashEndedAt = null;
        _blankSince = null;
        LastDuration = null;
        LastStartedAt = null;
        LastUncertainty = null;
        LastSource = null;
        LastRegressionDuration = null;
        LastRegressionUncertainty = null;
        LastStartShift = TimeSpan.Zero;
        _candidateHue = _anchorHue = _runHue = null;
        _candidateColor = _anchorColor = _runColor = null;
        _manualLearning = true;
        State = AbilityState.Ready;
        IsCharged = true;
    }

    public bool Observe(AbilitySignal signal, DateTime timestamp, bool fresh = true)
    {
        if (!fresh) return false;
        if (_manualLearning)
        {
            _manualLearningSince ??= timestamp;
            _manualSamples.Add(signal);
            if (timestamp - _manualLearningSince >= TimeSpan.FromSeconds(0.3))
            {
                // Channel medians reject transient flashes. Unwrap hues around the first sample at the red seam.
                static float Median(IEnumerable<float> values) { var sorted = values.Order().ToArray(); return sorted[sorted.Length / 2]; }
                var hues = _manualSamples.Where(s => s.FillHue is not null).Select(s => s.FillHue!.Value).ToArray();
                float? hue = hues.Length == 0 ? null : (hues[0] + Median(hues.Select(h => (h - hues[0] + 540) % 360 - 180)) + 360) % 360;
                var colors = _manualSamples.Where(s => s.FillColor is not null).Select(s => s.FillColor!.Value).ToArray();
                Color? color = colors.Length == 0 ? null : Color.FromArgb((int)Median(colors.Select(c => (float)c.R)),
                    (int)Median(colors.Select(c => (float)c.G)), (int)Median(colors.Select(c => (float)c.B)));
                _readyReferences.Add((hue, color, true));
                _manualLearning = false;
                _manualSamples.Clear();
                _lastReadyAt = _lastConfirmedReadyAt = timestamp;
            }
            return false;
        }
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
        var finished = FinishFromEvent(signal, pending.Started, pending.End);
        _useObservedAt = null;
        BeginRecharge(pending.Flash, pending.FlashPrevious, pending.Border);
        _cycleUseAt = null;
        State = AbilityState.Recharging;
        CurrentDuration = Elapsed(timestamp, pending.Flash);
        return finished;
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
        _estimated = false;
        _phaseSamples.Clear();
        _phaseRaw.Clear();
        _rawLines.Clear();
        _estimateSigma = null;
        _estimateModel = null;
        _estimateRefitAt = null;
        _startCandidates.Clear();
        _startCandidates.Add((timestamp, previousObservation));
        _latestCrossing = timestamp;
        _readyLost = false;
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

    private const double LearnSeconds = 0.5;
    private const double RelearnSeconds = 1.5;
    private const int MaxReferences = 4;
    private float? _candidateHue;
    private Color? _candidateColor;
    private DateTime? _candidateSince;
    private float? _anchorHue;
    private Color? _anchorColor;
    private float? _blankBorderBaseline;
    private DateTime? _lastBlankAt;
    private DateTime? _blankSince;

    // The latest steady charged-looking run (raw reading, before the reference gate).
    private DateTime? _runSince;
    private DateTime? _runLast;
    private float? _runHue;
    private Color? _runColor;

    private void TrackChargedRun(AbilitySignal signal, DateTime timestamp)
    {
        if (!signal.PixelEvidence || !signal.Charged || signal.FillHue is null && signal.FillColor is null) { return; }
        var continues = _runLast is { } last && timestamp - last <= TimeSpan.FromSeconds(0.2) &&
            (signal.FillHue is not { } hue || _runHue is not { } runHue || HueDistance(hue, runHue) < 25) &&
            (signal.FillColor is not { } color || _runColor is not { } runColor || ColorsClose(color, runColor, 30));
        if (!continues)
        {
            _runSince = timestamp;
            _runHue = signal.FillHue;
            _runColor = signal.FillColor;
        }

        _runLast = timestamp;
    }

    /// <summary>
    /// A use flash right after a steady charged look: the slot was charged, whatever the learned references say.
    /// Scenery can hold a charged-looking colour, but it does not then flash the slot white.
    /// </summary>
    private bool ChargedJustBefore(DateTime timestamp) => _runSince is { } since && _runLast is { } last &&
        last - since >= TimeSpan.FromSeconds(0.3) && timestamp - last <= TimeSpan.FromSeconds(0.5);

    /// <summary>The colour that preceded a use is a ready colour, even if it differs from the learned references.</summary>
    private void ConfirmReference()
    {
        var known = _readyReferences.Exists(reference =>
            (reference.Hue is not { } h || _runHue is not { } runHue || HueDistance(h, runHue) < 25) &&
            (reference.Color is not { } c || _runColor is not { } runColor || ColorsClose(c, runColor, 30)));
        if (known) { return; }
        if (_readyReferences.Count >= MaxReferences) { _readyReferences.RemoveAt(0); }
        _readyReferences.Add((_runHue, _runColor, true));
    }

    private static float HueDistance(float a, float b)
    {
        var difference = Math.Abs(a - b);
        return Math.Min(difference, 360 - difference);
    }

    private static bool ColorsClose(Color a, Color b, int tolerance) =>
        Math.Abs(a.R - b.R) < tolerance && Math.Abs(a.G - b.G) < tolerance && Math.Abs(a.B - b.B) < tolerance;

    /// <summary>
    /// Gates the charged reading to the learned ready colour. The reference is only learned from a charged reading that
    /// stays steady, so one bad frame (stale capture, loading screen, scenery) cannot latch a wrong colour. A steady
    /// charged reading of a different colour replaces the reference, but only while no recharge is running.
    /// </summary>
    private AbilitySignal ApplyReadyReference(AbilitySignal signal, DateTime timestamp)
    {
        var hue = signal.FillHue;
        var color = signal.FillColor;
        _matchedConfirmed = false;
        _acceptedByPhase = false;
        if (!signal.Charged || hue is null && color is null)
        {
            _candidateSince = null;
            return signal;
        }

        var hasReference = _readyReferences.Count > 0;
        // During an estimated recharge, colours learned only from steadiness may be scenery seen at startup.
        var matches = _readyReferences.Exists(reference => (!_estimated || reference.Confirmed) &&
            (reference.Hue is not { } readyHue || hue is not { } h || HueDistance(h, readyHue) < 25) &&
            (reference.Color is not { } readyColor || color is not { } c || ColorsClose(c, readyColor, 30)));
        if (matches && hasReference)
        {
            _candidateSince = null;
            _matchedConfirmed = _readyReferences.Exists(reference => reference.Confirmed &&
                (reference.Hue is not { } readyHue || hue is not { } h || HueDistance(h, readyHue) < 25) &&
                (reference.Color is not { } readyColor || color is not { } c || ColorsClose(c, readyColor, 30)));
            return signal;
        }

        // Tracking began mid-recharge, so a scenery colour may have been learned (or confirmed) as the ready colour. Once the
        // well-fitted progress line says the gray bar is full, the charged look is the ready colour. Waiting for the
        // predicted full time is safe: the gray-bar end is then found by looking back (up to 1.5 s).
        if (_estimated && _rechargeStarted is { } running &&
            _estimateModel is { ObservedMaximumProgress: >= 0.8 } refit && refit.SecondsAt(1) is { } fullAt &&
            refit.SecondsUncertaintyAt(1) is { } fullSigma &&
            (timestamp - running).TotalSeconds >= fullAt - Math.Max(0.5, 2 * fullSigma))
        {
            _candidateSince = null;
            _acceptedByPhase = true;
            return signal;
        }

        // Likewise for a fully observed recharge once its fitted line reaches full: the ready colour can differ from
        // every learned one (a scenery colour learned at startup, a changed subclass).
        // The look must have held 0.3 s: a quick recast shows the charged fill only briefly and is handled as a recast.
        if (!_estimated && _rechargeStarted is { } observed && _model.ObservedMaximumProgress >= 0.8 &&
            _runSince is { } chargedSince && timestamp - chargedSince >= TimeSpan.FromSeconds(0.3) &&
            _model.SecondsAt(1) is { } observedFull &&
            (timestamp - observed).TotalSeconds >= observedFull - 0.25)
        {
            _candidateSince = null;
            _acceptedByPhase = true;
            return signal;
        }

        if (_estimated && _rechargeStarted is not null)
        {
            // Nothing confirmed this colour, and the progress line does not say the bar is full yet.
            _candidateSince = null;
            return signal with { Charged = false };
        }

        var mayLearn = _rechargeStarted is null && _useObservedAt is null &&
            (!hasReference || State is AbilityState.Consumed or AbilityState.Unknown);
        if (!mayLearn)
        {
            _candidateSince = null;
            return hasReference ? signal with { Charged = false } : signal;
        }

        // Steady means consecutive readings stay close, so a slow drift still counts but a flicker restarts the clock.
        var steady = _candidateSince is not null && (hue is null || _candidateHue is not { } ch || HueDistance(hue.Value, ch) < 25) &&
            (color is null || _candidateColor is not { } cc || ColorsClose(color.Value, cc, 30));
        if (!steady)
        {
            _candidateSince = timestamp;
            _anchorHue = hue;
            _anchorColor = color;
        }

        _candidateHue = hue;
        _candidateColor = color;
        if (timestamp - _candidateSince >= TimeSpan.FromSeconds(hasReference ? RelearnSeconds : LearnSeconds))
        {
            // A first reference is the start of the steady run; a replacement is the current reading.
            // The charged look can legitimately vary (animation, subclass), so earlier references stay accepted.
            if (_readyReferences.Count >= MaxReferences)
            {
                var oldest = _readyReferences.FindIndex(reference => !reference.Confirmed);
                _readyReferences.RemoveAt(Math.Max(0, oldest));
            }

            _readyReferences.Add(hasReference ? (hue, color, false) : (_anchorHue, _anchorColor, false));
            _candidateSince = null;
            return signal;
        }

        return hasReference ? signal with { Charged = false } : signal;
    }

    private bool ObserveCore(AbilitySignal signal, DateTime timestamp)
    {
        var previousObservation = _lastObservedAt;
        _lastObservedAt = timestamp;
        if (signal.Twin is { } twinReading && (_rechargeStarted is null || _estimated || !_borders.TryGet(out _, out _)))
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

        // A rising edge of the start bar: opacity reaches the bar level after having been clearly below it.
        var barRising = false;
        if (signal.PixelEvidence && signal.BottomBarOpacity is { } barOpacity)
        {
            var isBar = barOpacity >= BarOpacityOn && !signal.UseFlash;
            barRising = isBar && !_barHigh;
            if (isBar) { _barHigh = true; }
            else if (barOpacity < BarOpacityOff || signal.UseFlash) { _barHigh = false; }
        }

        // A startup estimate must not depend on its regression becoming ready. A sustained charged
        // look after a visible near-full bar, with a real gray-bar endpoint, is independent evidence.
        if (_estimated && signal.Charged && !signal.CooldownLine &&
            _runSince is { } readySince && _runLast is { } lastRun && timestamp - lastRun <= TimeSpan.FromSeconds(0.2) &&
            timestamp - readySince >= TimeSpan.FromSeconds(0.3) &&
            (TryFindGrayBarEnd(readySince, out _, out _, requireCue: true) ||
                timestamp - readySince >= TimeSpan.FromSeconds(1) &&
                (_lastModelSampleAt is null || timestamp - _lastModelSampleAt >= TimeSpan.FromSeconds(3))))
        {
            DropEstimate();
            _readyReferences.Clear();
        }
        TrackChargedRun(signal, timestamp);
        if (_estimated && _rechargeStarted is not null && (_estimateRefitAt is not { } refitAt || timestamp - refitAt >= TimeSpan.FromSeconds(1)))
        {
            _estimateRefitAt = timestamp;
            var shift = _startShiftSeconds;
            var sigma = _estimateSigma;
            if (!RefitEstimate()) { (_startShiftSeconds, _estimateSigma) = (shift, sigma); }
        }

        var useObservedBeforeReference = _useObservedAt is not null && _readyReferences.Count == 0 && signal.FillHue is not null;
        if (useObservedBeforeReference)
        {
            State = AbilityState.Consumed;
            if (timestamp - _useObservedAt > TimeSpan.FromSeconds(10))
            {
                TrackingWarning = "Ability use seen, but recharge-start border pulse was not captured. Use Mark charged with the ability charged.";
            }
        }
        else
        {
            signal = ApplyReadyReference(signal, timestamp);
        }

        if (signal.Charged) { _readyCandidateSince ??= timestamp; }
        else { _readyCandidateSince = null; }
        // A briefly charged-looking fill while the bar was last seen well short of full is scenery, not the end.
        // While the gray bar is still being seen well short of full and the fitted progress line agrees, a charged look
        // of any length is scenery: the gray bar cannot vanish halfway.
        var implausiblyEarly = _rechargeStarted is { } running && signal.PixelEvidence && _lastProgress is < 0.6f &&
            _lastProgressAt is { } progressSeenAt && timestamp - progressSeenAt < TimeSpan.FromSeconds(2) &&
            (timestamp - _readyCandidateSince < TimeSpan.FromSeconds(0.75) &&
                (LastDuration is not { } previousDuration || timestamp - running < previousDuration / 2) ||
                _model.Predict((timestamp - running).TotalSeconds) is < 0.6);
        var confirmedReady = signal.Charged && !implausiblyEarly && (!signal.PixelEvidence ||
            timestamp - _readyCandidateSince >= TimeSpan.FromMilliseconds(150));
        IsCharged = confirmedReady;
        // A charged look that has not yet held long enough to become the reference (a stale frame) is not evidence of readiness.
        if (confirmedReady && _rechargeStarted is null && (_readyReferences.Count > 0 || signal.FillHue is null && signal.FillColor is null))
        {
            _lastReadyAt = timestamp;
            if (_matchedConfirmed) { _lastConfirmedReadyAt = timestamp; }
        }

        // An estimated recharge running well past its fitted full time ended unseen. Report nothing and follow the
        // slot again. (A white flash near full is the completion animation, not a sign of anything.)
        if (_estimated && _rechargeStarted is { } estimatedStart && (_estimateModel ?? _model).SecondsAt(1) is { } predictedFull &&
            (timestamp - estimatedStart).TotalSeconds > predictedFull + EstimateOverrunSeconds)
        {
            DropEstimate();
        }

        // A use flashes the slot white briefly (0.03-0.31 s on reference footage); 0.6 s of white was a bright scene.
        // The flash is judged when it ends, from the slot's state when it began.
        var flashEnded = false;
        if (signal.PixelEvidence && signal.UseFlash && _flashSince is null)
        {
            // White again right after a flash: a flickering bright scene, not a use.
            var flicker = _flashEndedAt is { } previousEnd && timestamp - previousEnd <= FlashGap;
            if (flicker && _rechargeStarted is null && _useObservedAt == _flashStarted) { _useObservedAt = null; }
            _flashSince = timestamp;
            _flashFollowsCharge = ChargedJustBefore(timestamp);
            _flashQualifies = !flicker &&
                (State is AbilityState.Ready or AbilityState.Complete || _readyLost || _flashFollowsCharge);
        }
        else if (signal.PixelEvidence && !signal.UseFlash && _flashSince is { } flashStart)
        {
            flashEnded = _flashQualifies && timestamp - flashStart <= MaxUseFlash;
            _flashStarted = flashStart;
            _flashEndedAt = timestamp;
            _flashSince = null;
        }

        if (signal.PixelEvidence && _rechargeStarted is null && _useObservedAt is null &&
            (flashEnded || signal.Blank && _lastConfirmedReadyAt is { } lastReady && timestamp - lastReady < TimeSpan.FromSeconds(1.5)))
        {
            _useObservedAt = flashEnded ? _flashStarted : timestamp;
            if (flashEnded && _flashFollowsCharge) { ConfirmReference(); }
            _consumedBorderBaseline = null;
            _readyLost = false;
            State = AbilityState.Consumed;
        }

        // The start bar follows a use within about 2.6 s on reference footage. An older "use" was scenery (or a use whose
        // bar was hidden); keeping it would let any later bar-like edge start a false recharge.
        if (_useObservedAt is { } staleUse && _rechargeStarted is null && timestamp - staleUse > UseToBarLimit)
        {
            _useObservedAt = null;
            TrackingWarning = "Ability use seen, but the recharge-start bar was not. Waiting for the next recharge.";
        }

        // Without an observed use (tracking began while consumed, or the use was missed), a blank fill followed by the
        // same border thickening still marks a real recharge start. Blank must have been seen just before the flash.
        var recoverable = signal.PixelEvidence && _rechargeStarted is null && _useObservedAt is null &&
            State is AbilityState.Consumed or AbilityState.Unknown;
        var recoveredStart = false;
        if (recoverable && !signal.Charged)
        {
            // A visible cooldown line means a recharge is already running, so there is no start left to catch.
            if (signal.CooldownLine || signal.ChargeProgress is not null)
            {
                _readyLost = false;
                _blankBorderBaseline = null;
                _lastBlankAt = null;
            }
            else if (signal.Blank && !signal.UseFlash)
            {
                if (_lastBlankAt is not { } lastBlank || timestamp - lastBlank > TimeSpan.FromSeconds(3))
                {
                    _blankBorderBaseline = null;
                    _blankSince = timestamp;
                }

                _lastBlankAt = timestamp;
            }

            recoveredStart = !signal.UseFlash && !signal.CooldownLine && signal.ChargeProgress is null &&
                _lastBlankAt is { } blankAt && _blankSince is { } since &&
                timestamp - blankAt < TimeSpan.FromSeconds(1) && timestamp - since >= TimeSpan.FromSeconds(0.5) &&
                (signal.BottomBarOpacity is null
                    ? _blankBorderBaseline is { } blankBaseline && signal.BottomBorderThickness > blankBaseline + 0.012f
                    : barRising);
            if (!recoveredStart && signal.Blank && !signal.UseFlash && !signal.CooldownLine && signal.ChargeProgress is null)
            {
                _blankBorderBaseline = Math.Min(_blankBorderBaseline ?? signal.BottomBorderThickness, signal.BottomBorderThickness);
            }
        }

        var startFlash = _rechargeStarted is null && (signal.PixelEvidence
            ? recoveredStart || _useObservedAt is { } usedAt && (signal.BottomBarOpacity is null
                ? !signal.UseFlash && (_consumedBorderBaseline ?? _bottomBorderBaseline) is { } consumed &&
                    signal.BottomBorderThickness > consumed + 0.012f
                : barRising && usedAt < timestamp)
            : _bottomBorderBaseline is { } baseline && signal.BottomBorderThickness > baseline + 0.012f);
        if (_useObservedAt is not null && signal.Blank && !signal.UseFlash && !startFlash)
        {
            _consumedBorderBaseline = Math.Min(_consumedBorderBaseline ?? signal.BottomBorderThickness,
                signal.BottomBorderThickness);
        }

        if (_rechargeStarted is null && startFlash &&
            (_useObservedAt is not null || recoveredStart || State is AbilityState.Ready or AbilityState.Complete))
        {
            // Without an observed use, the blank before the bar is the earliest the use can have been.
            _cycleUseAt = _useObservedAt ?? (recoveredStart ? _blankSince : null);
            _useObservedAt = null;
            BeginRecharge(timestamp, previousObservation, signal.BottomBorderThickness);
            _candidateFloor = signal.BottomBarOpacity ?? 0;
        }
        else if (_rechargeStarted is null)
        {
            TrackPhase(signal, timestamp, confirmedReady);
        }

        if (_rechargeStarted is { } started)
        {
            if (signal.PixelEvidence)
            {
                if (signal.BottomBarOpacity is null) { TrackStartPulse(signal, timestamp, previousObservation); }
                else if (!startFlash && _firstStartAt is { } first && signal.BottomBarOpacity is { } opacity &&
                    (timestamp - first).TotalSeconds <= PulseWindowSeconds)
                {
                    // Any clear step up in bar opacity is a candidate, not only a threshold crossing: a use pulse can
                    // fade straight into the real bar. The fitted line's zero picks among them.
                    if (barRising || !signal.UseFlash && opacity >= BarOpacityOff && opacity - _candidateFloor >= CandidateRise)
                    {
                        _startCandidates.Add((timestamp, previousObservation));
                        _candidateFloor = opacity;
                        if (barRising) { _latestCrossing = timestamp; }
                    }
                    else
                    {
                        _candidateFloor = Math.Min(_candidateFloor, signal.UseFlash ? 0 : opacity);
                    }
                }

                started = _rechargeStarted ?? started;
                TrackChargedEpisode(signal, started, timestamp);
            }

            if (signal.PixelEvidence && confirmedReady && !startFlash && _readyCandidateSince is { } onset &&
                (onset - started).TotalSeconds >= MinimumRechargeSeconds &&
                TryFindGrayBarEnd(onset, out var barEnd, out var barEndPrevious, requireCue: _estimated || _acceptedByPhase))
            {
                _completionPreviousFrame = barEndPrevious;
                return FinishFromEvent(signal, started, barEnd);
            }

            // An estimated recharge now charged for half a second at its fitted full time, with the gray bar's end
            // not seen: it is over, but its end time is unknown. Report nothing so the next use is caught.
            if (_estimated && _acceptedByPhase && confirmedReady && _readyCandidateSince is { } heldSince &&
                timestamp - heldSince >= TimeSpan.FromSeconds(0.5))
            {
                DropEstimate();
                return false;
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
                // While the fitted line says the bar is still far from full, a charged look is scenery, not an
                // obscured endpoint.
                if (_recovering && confirmedReady && !signal.CooldownLine &&
                    !(_model.Predict((timestamp - started).TotalSeconds) is < 0.8) &&
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
                return FinishFromEvent(signal, started, blank);
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
            // A held (reference-matched) ready look faded without a use (scenery washing out the fill): a later use flash is still a use.
            _readyLost = State is AbilityState.Ready or AbilityState.Complete && _lastReadyAt is { } confirmedAt &&
                timestamp - confirmedAt < TimeSpan.FromSeconds(1.5);
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

    /// <summary>True while the running recharge was picked up mid-cycle and its start is being estimated.</summary>
    public bool Estimating => _estimated && _rechargeStarted is not null;
    private bool _estimated;
    private readonly List<(DateTime Time, float Progress)> _phaseSamples = [];
    private readonly List<(DateTime Time, float? Left, float? Right)> _phaseRaw = [];
    private readonly List<(double Seconds, float? Left, float? Right)> _phaseLines = [];
    // Raw boundary rows of an estimated recharge, re-read against the final borders when it completes.
    private readonly List<(double Seconds, float? Left, float? Right)> _rawLines = [];

    /// <summary>
    /// Refits an estimated recharge from its raw boundary rows using the borders learned by the end of the cycle:
    /// when tracking starts early in a recharge, the boundary hugs the bottom border and is first read as it, so
    /// progress computed live is biased. The start becomes the refitted line's zero.
    /// </summary>
    private bool RefitEstimate()
    {
        _estimateModel = null;
        if (!_borders.TryGet(out var left, out var right)) { return false; }
        var model = new RechargeModel();
        var minimum = 1.0;
        foreach (var (seconds, leftLine, rightLine) in _rawLines)
        {
            var l = HudTwin.RawProgress(leftLine, left) * ProgressGain + ProgressOffset;
            var r = HudTwin.RawProgress(rightLine, right) * ProgressGain + ProgressOffset;
            float? progress = l is { } a && r is { } b ? Math.Abs(a - b) <= 0.05f ? (a + b) / 2 : null : l ?? r;
            if (progress is { } p && p is >= 0.03f and <= 0.97f)
            {
                model.Add(seconds, p);
                minimum = Math.Min(minimum, p);
            }
        }

        // Too little of the cycle seen (under a tenth of the bar), or a start too uncertain to be useful (over 5% of
        // the cycle), gives no estimate.
        if (model.ObservedMaximumProgress - minimum < MinimumObservedShare || model.SecondsAt(0) is not { } zero ||
            model.SecondsUncertaintyAt(0) is not { } sigma || model.SecondsAt(1) is not { } full ||
            sigma > MaximumRelativeUncertainty * (full - zero))
        {
            return false;
        }

        _startShiftSeconds = -zero;
        _estimateSigma = sigma;
        _estimateModel = model;
        return true;
    }

    // The latest refit of an estimated recharge (refreshed about once a second), for its live start and full time.
    private RechargeModel? _estimateModel;
    private DateTime? _estimateRefitAt;

    private const double MinimumObservedShare = 0.1;
    private const double MaximumRelativeUncertainty = 0.05;
    private const double EstimateOverrunSeconds = 5;

    private void DropEstimate()
    {
        TrackingWarning = "Tracking began too late in this recharge to estimate it; measuring from the next use.";
        MeasurementDiscarded = true;
        ResetModel();
        _rechargeStarted = null;
        _estimated = false;
        _completionCandidateSince = null;
        _completionPreviousFrame = null;
        CurrentDuration = null;
        ChargeProgress = null;
        _lastProgress = null;
        _lastProgressAt = null;
        _progressSamples = 0;
        State = AbilityState.Ready;
    }

    private double? _estimateSigma;
    // A recharge in progress shows a progress line rising steadily: at least 2 s of agreeing readings, rising at a
    // rate between a 300 s and a 3 s recharge, within 0.015 of a straight line (a static icon edge does not rise).
    private const double PhaseSeconds = 2;
    private const int PhaseMinimumSamples = 20;
    private const double PhaseMinimumRate = 1.0 / 300;
    private const double PhaseMaximumRate = 1.0 / 3;
    private const double PhaseMaximumResidual = 0.015;

    /// <summary>
    /// With no recharge running and no use seen, a steadily rising progress line means tracking began (or resumed)
    /// partway through a recharge. Its start is then estimated from the fitted line, and the result is marked as
    /// estimated rather than measured.
    /// </summary>
    private void TrackPhase(AbilitySignal signal, DateTime timestamp, bool confirmedReady)
    {
        if (!signal.PixelEvidence || _useObservedAt is not null || confirmedReady ||
            State is not (AbilityState.Unknown or AbilityState.Consumed))
        {
            _phaseSamples.Clear();
            _phaseRaw.Clear();
            return;
        }

        if (signal.Charged || signal.Twin is not { } twin || TwinProgress(twin, 0) is not { } progress ||
            progress is < 0.03f or > 0.97f)
        {
            return;
        }

        _phaseSamples.Add((timestamp, progress));
        _phaseSamples.RemoveAll(sample => timestamp - sample.Time > TimeSpan.FromSeconds(2 * PhaseSeconds));
        var origin = _phaseSamples[0].Time;
        _phaseRaw.Add((timestamp, twin.Left.Line, twin.Right.Line));
        _phaseRaw.RemoveAll(sample => sample.Time < origin);
        _phaseLines.Clear();
        foreach (var (time, left, right) in _phaseRaw) { _phaseLines.Add(((time - origin).TotalSeconds, left, right)); }
        if (_phaseSamples.Count < PhaseMinimumSamples || (timestamp - origin).TotalSeconds < PhaseSeconds) { return; }
        double n = _phaseSamples.Count, sx = 0, sy = 0, sxx = 0, sxy = 0;
        foreach (var (time, value) in _phaseSamples)
        {
            var x = (time - origin).TotalSeconds;
            sx += x;
            sy += value;
            sxx += x * x;
            sxy += x * value;
        }

        var slope = (n * sxy - sx * sy) / (n * sxx - sx * sx);
        var intercept = (sy - slope * sx) / n;
        var residual = Math.Sqrt(_phaseSamples.Sum(sample =>
            Math.Pow(sample.Progress - (intercept + slope * (sample.Time - origin).TotalSeconds), 2)) / n);
        if (slope is < PhaseMinimumRate or > PhaseMaximumRate || residual > PhaseMaximumResidual) { return; }

        var samples = _phaseSamples.ToArray();
        BeginRecharge(origin, null, signal.BottomBorderThickness);
        _rawLines.Clear();
        _rawLines.AddRange(_phaseLines);
        _estimated = true;
        _cycleUseAt = null;
        foreach (var (time, value) in samples) { _model.Add((time - origin).TotalSeconds, value); }
        _lastProgress = progress;
        _lastProgressAt = timestamp;
        _lastModelSampleAt = timestamp;
        _progressSamples = samples.Length;
        ChargeProgress = progress;
        TrackingWarning = "Tracking began mid-recharge; this cycle's duration will be estimated.";
        State = AbilityState.Recharging;
        CurrentDuration = Elapsed(timestamp, origin);
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
    private void TrackChargedEpisode(AbilitySignal signal, DateTime started, DateTime timestamp)
    {
        // During an estimated recharge the ready colour may be unknown: a raw charged look once the fitted line is
        // nearly full counts too, so a quick recast right after the unseen-start cycle is still caught.
        var rawCharged = _estimated && _runLast == timestamp && _runSince is { } runSince &&
            (_estimateModel ?? _model).Predict((timestamp - started).TotalSeconds) is >= 0.9;
        if (rawCharged && (_runSince!.Value - started).TotalSeconds >= MinimumRechargeSeconds)
        {
            if (_episodeOnset != _runSince)
            {
                _episodeOnset = _runSince;
                _postChargeBorderMinimum = null;
                _recastFlashSeen = false;
            }
        }
        else if (signal.Charged && _readyCandidateSince is { } onset && (onset - started).TotalSeconds >= MinimumRechargeSeconds)
        {
            _episodeOnset = onset;
            _postChargeBorderMinimum = null;
            _recastFlashSeen = false;
        }
        else if (_episodeOnset is not null && !signal.Charged && !rawCharged)
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
        if (_estimated && signal.Twin is { } raw && (raw.Left.Line is not null || raw.Right.Line is not null))
        {
            _rawLines.Add((seconds, raw.Left.Line, raw.Right.Line));
        }

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

    /// <summary>
    /// Seconds to move the start earlier (positive) or later (negative) than the first start-bar edge.
    /// Ability-use animations and scenery can flash the border before the real start bar, so every bar edge shortly
    /// after the first is a candidate. The real one is where the gray bar began filling: the fitted progress line's
    /// zero. Until the fit has enough progress, the latest edge is used. A start event observed late (for example,
    /// HUD obscured when the ability was used) shows up as the line reaching zero well before the first edge; then
    /// the start moves back, bounded by the observed use.
    /// </summary>
    private double StartShift(DateTime started)
    {
        if (_estimated)
        {
            // Mid-recharge: the start is where the (refitted) progress line reaches zero.
            return (_estimateModel ?? _model).SecondsAt(0) is { } origin ? -origin : 0;
        }


        (DateTime Time, DateTime? Previous) chosen = _startCandidates.Count > 0
            ? _startCandidates.Find(candidate => candidate.Time == _latestCrossing) is { Time.Ticks: > 0 } crossing ? crossing : _startCandidates[0]
            : (started, _startPreviousFrame);
        double Shift((DateTime Time, DateTime? Previous) candidate)
        {
            _startPreviousFrame = candidate.Previous;
            return -(candidate.Time - started).TotalSeconds;
        }

        if (_model.ObservedMaximumProgress < 0.15 || _model.SecondsAt(0) is not { } zero ||
            _model.SecondsUncertaintyAt(0) is not { } sigma)
        {
            return Shift(chosen);
        }

        if (zero < -Math.Max(0.25, 3 * sigma))
        {
            Shift(_startCandidates.Count > 0 ? _startCandidates[0] : chosen);
            var limit = _cycleUseAt is { } use ? Math.Max(0, (started - use).TotalSeconds) : double.MaxValue;
            return Math.Min(-zero, limit);
        }

        foreach (var candidate in _startCandidates)
        {
            if (Math.Abs((candidate.Time - started).TotalSeconds - zero) <
                Math.Abs((chosen.Time - started).TotalSeconds - zero))
            {
                chosen = candidate;
            }
        }

        return Shift(chosen);
    }

    private void Complete(AbilitySignal signal, TimeSpan duration, TimeSpan uncertainty, MeasurementSource source)
    {
        LastStartShift = TimeSpan.FromSeconds(_startShiftSeconds);
        LastStartedAt = CurrentStartedAt;
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
        LastSource = _estimated ? MeasurementSource.Estimated : source;
        _estimated = false;
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

    /// <summary>Closes the recharge at <paramref name="blank"/>; false when an estimate had too little to go on.</summary>
    private bool FinishFromEvent(AbilitySignal signal, DateTime started, DateTime blank)
    {
        if (_estimated && !RefitEstimate())
        {
            DropEstimate();
            return false;
        }

        if (_estimated && signal.Charged && (signal.FillHue is not null || signal.FillColor is not null))
        {
            // The colour that appeared as the fitted bar filled and the gray bar vanished is the ready colour.
            (_runHue, _runColor) = (signal.FillHue, signal.FillColor);
            ConfirmReference();
        }

        var shift = TimeSpan.FromSeconds(_startShiftSeconds);
        var eventSeconds = (blank - started).TotalSeconds;
        var endGap = _completionPreviousFrame is { } before ? (blank - before).TotalSeconds : 0;
        var effectiveStart = started - TimeSpan.FromSeconds(Math.Min(0, _startShiftSeconds));
        var startGap = _startPreviousFrame is { } beforeStart ? (effectiveStart - beforeStart).TotalSeconds : 0;
        if (endGap > EndGapForModel && _model.IsReady && _model.SecondsAt(1) is { } modelEnd &&
            _model.SecondsUncertaintyAt(1) is { } modelSigma && modelSigma < endGap)
        {
            var lower = eventSeconds - endGap;
            var refined = Math.Clamp(modelEnd, lower, eventSeconds);
            Complete(signal, TimeSpan.FromSeconds(refined) + shift,
                TimeSpan.FromSeconds(Math.Max(modelSigma, startGap / 2)), MeasurementSource.EventRefinedByModel);
            return true;
        }

        var sigma = Math.Sqrt(endGap * endGap + startGap * startGap) / 2;
        if (_startShiftSeconds > 0 && (_estimateSigma ?? _model.SecondsUncertaintyAt(0)) is { } startSigma)
        {
            sigma = Math.Sqrt(sigma * sigma + startSigma * startSigma);
        }

        Complete(signal, blank - started + shift, TimeSpan.FromSeconds(sigma), MeasurementSource.Event);
        return true;
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

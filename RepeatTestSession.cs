namespace D2AbilityTracker;

internal enum RepeatTestPhase
{
    Idle,
    AwaitingInitialUse,
    WaitingForRecharge,
    WaitingForNextUse,
    AwaitingRechargeAfterAutomaticUse,
    Completed,
    Stopped,
    Failed
}

internal sealed class RepeatTestSession
{
    private DateTime? _nextUseAt;
    private DateTime? _automaticUseDeadline;
    private readonly List<TimeSpan> _durations = [];
    private readonly List<TimeSpan> _regressionDurations = [];
    private readonly List<MeasurementHistoryEntry> _validatedCycles = [];
    private MeasurementHistoryEntry? _validatedSummary;
    public Guid RunId { get; private set; }

    public AbilityKind? Ability { get; private set; }
    public InputBinding? UseBinding { get; private set; }
    public int TargetCycles { get; private set; }
    public int CompletedCycles => _durations.Count;
    public RepeatTestPhase Phase { get; private set; } = RepeatTestPhase.Idle;
    public bool IsActive => Phase is RepeatTestPhase.AwaitingInitialUse or
        RepeatTestPhase.WaitingForRecharge or
        RepeatTestPhase.WaitingForNextUse or
        RepeatTestPhase.AwaitingRechargeAfterAutomaticUse;
    public TimeSpan? AverageDuration => _durations.Count == 0
        ? null
        : TimeSpan.FromTicks((long)_durations.Average(duration => duration.Ticks));
    /// <summary>Mean of the per-cycle linear-regression totals, for cycles where the fill could be fitted.</summary>
    public TimeSpan? AverageRegressionDuration => _regressionDurations.Count == 0
        ? null
        : TimeSpan.FromTicks((long)_regressionDurations.Average(duration => duration.Ticks));

    public string Status(DateTime now)
    {
        return Phase switch
        {
            RepeatTestPhase.Idle => "Choose a configured ability, record its use input, and start a test.",
            RepeatTestPhase.AwaitingInitialUse => $"Cycle 1/{TargetCycles} · use the ability once to start.",
            RepeatTestPhase.WaitingForRecharge => $"Cycle {CompletedCycles + 1}/{TargetCycles} · measuring recharge.",
            RepeatTestPhase.WaitingForNextUse when _nextUseAt is { } nextUse && nextUse > now =>
                $"Recharge {CompletedCycles}/{TargetCycles} complete · auto-use in {(nextUse - now).TotalSeconds:F1}s.",
            RepeatTestPhase.WaitingForNextUse =>
                $"Recharge {CompletedCycles}/{TargetCycles} complete · show the recharged HUD in Destiny 2 to continue.",
            RepeatTestPhase.AwaitingRechargeAfterAutomaticUse =>
                $"Sent ability input · waiting for the next cooldown (cycle {CompletedCycles + 1}/{TargetCycles}).",
            RepeatTestPhase.Completed =>
                $"Finished {CompletedCycles}/{TargetCycles} cycles · average {FormatDuration(AverageDuration)}{UncertaintySummary()}{RegressionSummary()}.",
            RepeatTestPhase.Stopped => "Test stopped.",
            RepeatTestPhase.Failed => $"Test stopped: {FailureReason}",
            _ => "Test ready."
        };
    }

    public string? FailureReason { get; private set; }

    public void Start(AbilityKind ability, int targetCycles, TimeSpan delay, InputBinding useBinding)
    {
        if (targetCycles is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(targetCycles), "Choose between 1 and 100 recharge cycles.");
        }

        if (delay < TimeSpan.Zero || delay > TimeSpan.FromSeconds(30))
        {
            throw new ArgumentOutOfRangeException(nameof(delay), "The post-recharge delay must be between zero and 30 seconds.");
        }

        Ability = ability;
        TargetCycles = targetCycles;
        UseBinding = useBinding;
        _durations.Clear();
        _regressionDurations.Clear();
        _validatedCycles.Clear();
        _validatedSummary = null;
        RunId = Guid.NewGuid();
        _nextUseAt = null;
        _automaticUseDeadline = null;
        FailureReason = null;
        Phase = RepeatTestPhase.AwaitingInitialUse;
        PostRechargeDelay = delay;
    }

    public TimeSpan PostRechargeDelay { get; private set; }

    public void ObserveState(AbilityKind ability, AbilityState state, DateTime timestamp)
    {
        if (ability != Ability)
        {
            return;
        }

        if (Phase == RepeatTestPhase.AwaitingInitialUse && IsAbilityUsed(state))
        {
            Phase = RepeatTestPhase.WaitingForRecharge;
        }
        else if (Phase == RepeatTestPhase.AwaitingRechargeAfterAutomaticUse && IsAbilityUsed(state))
        {
            Phase = RepeatTestPhase.WaitingForRecharge;
            _automaticUseDeadline = null;
        }

        if (Phase == RepeatTestPhase.AwaitingRechargeAfterAutomaticUse &&
            _automaticUseDeadline is { } deadline && timestamp >= deadline)
        {
            Fail("No cooldown appeared after the automatic ability input.");
        }
    }

    public void RecordRecharge(AbilityKind ability, TimeSpan duration, DateTime timestamp, TimeSpan? regression = null,
        MeasurementHistoryEntry? validated = null)
    {
        if (ability != Ability || Phase != RepeatTestPhase.WaitingForRecharge || duration <= TimeSpan.Zero)
        {
            return;
        }

        _durations.Add(duration);
        if (regression is { } fitted && fitted > TimeSpan.Zero) _regressionDurations.Add(fitted);
        if (validated is not null && validated.Ability == ability && validated.DurationSeconds == duration.TotalSeconds)
            _validatedCycles.Add(validated);
        if (CompletedCycles >= TargetCycles)
        {
            Phase = RepeatTestPhase.Completed;
            if (_validatedCycles.Count == TargetCycles)
                _validatedSummary = RepeatMeasurementSummary.Create(RunId, _validatedCycles, timestamp);
            return;
        }

        _nextUseAt = timestamp + PostRechargeDelay;
        Phase = RepeatTestPhase.WaitingForNextUse;
    }

    public bool TryGetDueUse(DateTime timestamp, bool gameIsForeground, out InputBinding binding)
    {
        binding = default;
        if (Phase != RepeatTestPhase.WaitingForNextUse ||
            _nextUseAt is not { } nextUse || timestamp < nextUse ||
            UseBinding is not { } configuredBinding)
        {
            return false;
        }

        if (!gameIsForeground)
        {
            return false;
        }

        binding = configuredBinding;
        _nextUseAt = null;
        _automaticUseDeadline = timestamp + TimeSpan.FromSeconds(8);
        Phase = RepeatTestPhase.AwaitingRechargeAfterAutomaticUse;
        return true;
    }

    public bool IsUseDue(DateTime timestamp) =>
        Phase == RepeatTestPhase.WaitingForNextUse &&
        _nextUseAt is { } nextUse &&
        timestamp >= nextUse;

    public void Stop()
    {
        if (IsActive)
        {
            Phase = RepeatTestPhase.Stopped;
            _nextUseAt = null;
            _automaticUseDeadline = null;
        }
    }

    public void Fail(string reason)
    {
        FailureReason = reason;
        Phase = RepeatTestPhase.Failed;
        _nextUseAt = null;
        _automaticUseDeadline = null;
    }

    private static bool IsAbilityUsed(AbilityState state) =>
        state == AbilityState.Recharging;

    private string RegressionSummary() =>
        AverageRegressionDuration is { } fitted
            ? $" · regression {FormatDuration(fitted)}{RegressionUncertaintySummary()} ({_regressionDurations.Count}/{CompletedCycles})"
            : string.Empty;

    private string UncertaintySummary() => ValidatedSummary is { } summary
        ? $" ± {summary.UncertaintySeconds:F3} s"
        : " · aggregate not validated";

    private string RegressionUncertaintySummary() => ValidatedSummary is { RegressionUncertaintySeconds: { } spread }
        ? $" ± {spread:F3} s" : string.Empty;

    public MeasurementHistoryEntry? ValidatedSummary =>
        Phase == RepeatTestPhase.Completed ? _validatedSummary : null;

    private static string FormatDuration(TimeSpan? duration) =>
        DurationDisplay.Format(duration);
}

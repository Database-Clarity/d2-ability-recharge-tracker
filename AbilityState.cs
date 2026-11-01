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
    public string OverlayAnchor { get; set; } = "Bottom right";
}

internal readonly record struct AbilitySignal(bool Charged, bool CooldownLine, float ChargedBackgroundRatio, float CooldownLineRatio);

internal sealed class AbilitySlotTracker
{
    private DateTime? _lineCandidateSince;
    private int _lineCandidateSamples;
    private DateTime? _rechargeStarted;
    private DateTime? _completionCandidateSince;
    private bool _hasSeenRechargeLine;

    public AbilityKind Kind { get; }
    public AbilityState State { get; private set; } = AbilityState.Unknown;
    public TimeSpan? LastDuration { get; private set; }
    public TimeSpan? CurrentDuration { get; private set; }

    public AbilitySlotTracker(AbilityKind kind)
    {
        Kind = kind;
    }

    public void ResetCycle()
    {
        _lineCandidateSince = null;
        _lineCandidateSamples = 0;
        _rechargeStarted = null;
        _completionCandidateSince = null;
        _hasSeenRechargeLine = false;
        CurrentDuration = null;
        State = AbilityState.Unknown;
    }

    public bool Observe(AbilitySignal signal, DateTime timestamp)
    {
        var completed = false;
        if (signal.CooldownLine && !signal.Charged)
        {
            _completionCandidateSince = null;
            _lineCandidateSince ??= timestamp;
            _lineCandidateSamples++;
            if (!_hasSeenRechargeLine && _lineCandidateSamples >= 2)
            {
                _rechargeStarted = _lineCandidateSince;
                _hasSeenRechargeLine = true;
                State = AbilityState.Recharging;
            }
        }
        else
        {
            _lineCandidateSince = null;
            _lineCandidateSamples = 0;
            if (_hasSeenRechargeLine)
            {
                _completionCandidateSince ??= timestamp;
                if (signal.Charged)
                {
                    LastDuration = _completionCandidateSince.Value - _rechargeStarted!.Value;
                    CurrentDuration = null;
                    _rechargeStarted = null;
                    _completionCandidateSince = null;
                    _hasSeenRechargeLine = false;
                    State = AbilityState.Complete;
                    completed = true;
                }
            }
        }

        if (_hasSeenRechargeLine)
        {
            CurrentDuration = timestamp - _rechargeStarted!.Value;
            State = AbilityState.Recharging;
        }
        else if (signal.Charged)
        {
            if (State != AbilityState.Complete)
            {
                State = AbilityState.Ready;
            }
        }
        else if (State is AbilityState.Unknown or AbilityState.Ready or AbilityState.Complete)
        {
            State = AbilityState.Consumed;
        }

        return completed;
    }
}

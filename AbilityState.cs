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
    float BottomBorderThickness = 0);

internal sealed class AbilitySlotTracker
{
    private DateTime? _rechargeStarted;
    private DateTime? _completionCandidateSince;
    private float? _bottomBorderBaseline;

    public AbilityKind Kind { get; }
    public AbilityState State { get; private set; } = AbilityState.Unknown;
    public bool IsCharged { get; private set; }
    public TimeSpan? LastDuration { get; private set; }
    public TimeSpan? CurrentDuration { get; private set; }

    public AbilitySlotTracker(AbilityKind kind)
    {
        Kind = kind;
    }

    public void ResetCycle()
    {
        _rechargeStarted = null;
        _completionCandidateSince = null;
        _bottomBorderBaseline = null;
        IsCharged = false;
        CurrentDuration = null;
        State = AbilityState.Unknown;
    }

    public bool Observe(AbilitySignal signal, DateTime timestamp)
    {
        IsCharged = signal.Charged;
        var startFlash = _bottomBorderBaseline is { } baseline &&
            signal.BottomBorderThickness > baseline + 0.012f;
        if (_rechargeStarted is null && startFlash)
        {
            _rechargeStarted = timestamp;
            _completionCandidateSince = null;
        }

        if (_rechargeStarted is { } started)
        {
            // Start flashes can precede the ready fill disappearing; only a later blank frame is an endpoint.
            if (signal.CooldownLine || startFlash)
            {
                _completionCandidateSince = null;
            }
            else if (!signal.Charged)
            {
                _completionCandidateSince ??= timestamp;
            }

            if (signal.Charged && !startFlash && _completionCandidateSince is { } blank)
            {
                LastDuration = blank - started;
                CurrentDuration = null;
                _rechargeStarted = null;
                _completionCandidateSince = null;
                _bottomBorderBaseline = signal.BottomBorderThickness;
                State = AbilityState.Complete;
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
}

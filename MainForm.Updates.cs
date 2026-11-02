namespace D2AbilityTracker;

internal enum UpdatePhase
{
    Idle,
    Checking,
    UpToDate,
    Available,
    Failed,
    Downloading,
    Installing
}

// One place that renders update state: the Settings › About controls and the navigation-row update pill.
internal sealed partial class MainForm
{
    private UpdatePhase _updatePhase;

    internal UpdatePhase CurrentUpdatePhase => _updatePhase;

    private void ShowUpdateState(UpdatePhase phase, UpdateRelease? release = null, string? error = null, double progress = 0)
    {
        if (IsDisposed) return;
        _updatePhase = phase;
        var version = UpdateChecker.IsReleaseBuild ? $"v{UpdateChecker.CurrentVersion.ToString(3)}" : "Development build";
        var tag = release?.Tag ?? string.Empty;
        var tip = phase switch
        {
            UpdatePhase.Checking => "Checking GitHub for the latest release.",
            UpdatePhase.Failed => $"Couldn't check for updates: {error}",
            UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Installing when release is not null =>
                _storagePaths.IsPortable
                    ? $"{tag} is available. Download its portable ZIP and replace the executable; keep your data files."
                    : $"{tag} is available. Click to download and install it.",
            UpdatePhase.UpToDate => $"You're on the latest version (v{UpdateChecker.CurrentVersion.ToString(3)}).",
            _ => "Check GitHub for a newer release."
        };

        var busy = phase is UpdatePhase.Checking or UpdatePhase.Downloading or UpdatePhase.Installing;
        _updateButton.Enabled = !busy;
        _updateButton.Text = phase switch
        {
            UpdatePhase.Checking => "Checking…",
            UpdatePhase.Available => $"Update to {tag}…",
            UpdatePhase.Failed => "Retry update check",
            UpdatePhase.Downloading => $"Downloading… {progress:P0}",
            UpdatePhase.Installing => "Installing…",
            _ => "Check for updates"
        };
        _updateButton.ForeColor = phase switch
        {
            UpdatePhase.Failed => TrackerTheme.Orange,
            UpdatePhase.Available => TrackerTheme.Accent,
            _ => TrackerTheme.Text
        };
        _updateButton.Font = phase == UpdatePhase.Available ? new Font(Font, FontStyle.Bold) : Font;
        _toolTip.SetToolTip(_updateButton, tip);

        // The main screen only advertises an update; checks and failures stay in Settings › About.
        var showPill = release is not null && phase is UpdatePhase.Available or UpdatePhase.Downloading or UpdatePhase.Installing;
        _updatePill.Text = phase switch
        {
            UpdatePhase.Downloading => $"Downloading… {progress:P0}",
            UpdatePhase.Installing => "Installing…",
            _ => $"Update to {tag}"
        };
        _updatePill.Enabled = phase == UpdatePhase.Available;
        _toolTip.SetToolTip(_updatePill, tip);
        if (_updatePill.Visible != showPill)
        {
            _updatePill.Visible = showPill;
            _updatePill.Parent?.PerformLayout();
        }

        _updateStatus.Text = phase switch
        {
            UpdatePhase.Checking => $"{version} · checking for updates…",
            UpdatePhase.UpToDate => $"{version} · up to date",
            UpdatePhase.Available => $"{version} · {tag} is available",
            UpdatePhase.Failed => $"{version} · couldn't check: {error}",
            UpdatePhase.Downloading => $"{version} · downloading {tag}…",
            UpdatePhase.Installing => $"{version} · installing {tag}…",
            _ => UpdateChecker.IsReleaseBuild && _settings.CheckForUpdatesOnStartup
                ? $"{version} · checks for updates on startup"
                : version
        };
        _updateStatus.ForeColor = phase == UpdatePhase.Failed ? TrackerTheme.Orange : TrackerTheme.Muted;
        _toolTip.SetToolTip(_updateStatus, _updateStatus.Text);
    }
}

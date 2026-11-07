using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace D2AbilityTracker;

internal sealed partial class MainForm : AppWindow
{
    private static readonly AbilityKind[] AbilityKinds = Enum.GetValues<AbilityKind>();
    private readonly TrackerStoragePaths _storagePaths = TrackerStoragePaths.Current;
    private readonly string _settingsPath = TrackerStoragePaths.Current.SettingsPath;
    private readonly string _updateSourceVersionPath = TrackerStoragePaths.Current.UpdateSourceVersionPath;
    private readonly TrackerSettings _settings;
    private readonly OverlayWindow _overlay = new();
    private readonly CancellationTokenSource _captureCancellation = new();
    private readonly object _captureSync = new();
    private readonly GlobalInputListener _inputListener = new();
    private readonly Thread _captureThread;
    private readonly StatusBanner _statusBanner = new();
    private readonly StatusPill _statusPill = new();
    private readonly Button _toggleButton = new();
    private readonly Button _pauseBindingButton = new();
    private readonly Button _abilityUseBindingButton = new();
    private readonly Button _testToggleButton = new();
    private readonly Label _testBlockedReason = new();
    private readonly Button _repeatTestBindingButton = new();
    private readonly Button _recordHudButton = new();
    private readonly Button _overlayBindingButton = new();
    private readonly Button _updateButton = new();
    private readonly PillButton _updatePill = new();
    private readonly Label _updateStatus = new();
    private readonly Button _changelogButton = new();
    private readonly FlatNumericUpDown _captureRatePicker = new();
    private readonly FlatComboBox _anchorPicker = new();
    private readonly Label _measuredCaptureRate = new();
    private string? _lastDisplayDeviceName;
    private AbilityKind? _soloBeforeTest;
    private readonly FlatComboBox _testAbilityPicker = new();
    private readonly FlatNumericUpDown _testCyclesPicker = new();
    private readonly FlatNumericUpDown _testDelayPicker = new();
    private readonly FlatNumericUpDown _testHoldPicker = new();
    private readonly Label _testStatus = new();
    private readonly Label _hudRecordingStatus = new();
    private readonly Button _captureFolderButton = new();
    private string? _lastRecordingPath;
    private readonly RepeatTestSession _repeatTest = new();
    private Task _repeatInputTask = Task.CompletedTask;
    private readonly Dictionary<AbilityKind, AbilitySlotTracker> _trackers = [];
    private readonly Dictionary<AbilityKind, Label> _statusLabels = [];
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<AbilityKind, string> _captureErrors = [];
    private readonly Queue<CaptureBatch> _retiredCaptureBatches = [];
    private CaptureBatch[] _captureBatches = [];
    private volatile bool _monitoring;
    private volatile bool _refreshCaptureRegionsRequested;
    private BindingCaptureTarget? _bindingCaptureTarget;
    private bool _inputListenerAvailable;
    private double _captureRate;
    private long _lastCapturedFrame;
    private string? _captureLoopError;
    private long _monitoringStarted;
    private bool _statusBannerShown;
    private volatile string _captureBackend = "GDI";
    private string? _inputListenerError;
    private int _targetCaptureRate;
    private int _uiRefreshPending;
    private bool _selectingRegion;
    private ScreenCapture.GameWindowTarget? _currentGameWindow;
    private AbilityHudRecorder? _hudRecorder;
    private ScreenCaptureSurface? _hudRecordingSurface;
    private Task<AbilityHudRecordingResult>? _hudRecordingTask;
    private bool _closingAfterHudRecording;
    private UpdateRelease? _availableUpdate;
    private bool _updateBusy;
    private bool _changelogBusy;
    private bool _startupChangelogPending;
    private Version _lastChangelogVersion = UpdateChecker.CurrentVersion;
    private Panel? _mainViewport;
    private readonly List<(NavigationButton Button, Control Page)> _pageLinks = [];
    private Control? _selectedPage;
    private Control? _lastCompactPage;
    private Control? _settingsPage;
    private Action? _fitWindowToDisplay;
    private ChangelogView? _changelogView;

    public MainForm()
    {
        _settings = LoadSettings();
        InitializeChangelogVersion();
        EnsureSlotSettings();
        Text = UpdateChecker.IsReleaseBuild
            ? $"D2 Ability Recharge Tracker v{UpdateChecker.CurrentVersion.ToString(3)}"
            : "D2 Ability Recharge Tracker · development";
        _targetCaptureRate = Math.Clamp(_settings.CaptureRate, 30, 240);
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);
        InitializeMeasurementHistory();
        BuildControls();
        InitializeOverlayInteractions();
        OverlayMoveModeChanged += _ => SyncOverlayMoveToggle();
        Icon = AppIcon.Load();
        ShowOverlayIfEnabled();
        UpdateOverlay();
        UpdateStatus();
        _inputListener.BindingPressed += OnGlobalBindingPressed;
        _inputListener.Failed += error => QueueInputAction(() =>
        {
            _inputListenerAvailable = false;
            _inputListenerError = error;
            StopRepeatabilityTest();
        });
        try
        {
            _inputListener.Start();
            _inputListenerAvailable = true;
        }
        catch (Exception exception)
        {
            _inputListenerError = exception.Message;
        }

        SetMonitoring(true);
        Shown += async (_, _) =>
        {
            var updateCheck = UpdateChecker.IsReleaseBuild && _settings.CheckForUpdatesOnStartup
                ? CheckForUpdateAsync(interactive: false)
                : Task.FromResult<UpdateRelease?>(null);
            if (_startupChangelogPending)
            {
                await OpenChangelogAsync(startup: true);
            }

            await updateCheck;
        };
        FormClosing += async (_, args) =>
        {
            Task inputTask;
            lock (_captureSync)
            {
                _monitoring = false;
                _repeatTest.Stop();
                inputTask = _repeatInputTask;
            }
            if (!inputTask.IsCompleted)
            {
                args.Cancel = true;
                await inputTask;
                Close();
                return;
            }
            if (_hudRecordingTask is not { } recordingTask)
            {
                return;
            }

            if (_closingAfterHudRecording)
            {
                args.Cancel = !recordingTask.IsCompleted;
                return;
            }

            args.Cancel = true;
            _closingAfterHudRecording = true;
            if (!recordingTask.IsCompleted)
            {
                _hudRecorder?.Stop();
            }

            _recordHudButton.Enabled = false;
            try
            {
                await recordingTask;
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, $"Couldn't finish the HUD recording before closing: {exception.Message}", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
            }

            Close();
        };
        FormClosed += (_, _) =>
        {
            _captureCancellation.Cancel();
            _inputListener.Dispose();
            _overlay.Close();
        };
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "D2 ability HUD capture"
        };
        _captureThread.Start();
    }

    private readonly CheckBox _overlayMoveToggle = new();
    private const int GridWidth = 640;
    private const int StatusPillWidth = 210;
    private const int HistoryHeight = 500;
    private const int HistoryMinimumHeight = 460;
    private const string HudRecordingHint = "Saves the tracked slots at 60 FPS for bug reports.";

    private static Label GridLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TrackerTheme.Muted,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 0, 3, 0)
    };

    private static TableLayoutPanel Grid(params int[] widths)
    {
        var grid = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = widths.Length,
            Anchor = AnchorStyles.Left,
            Margin = Padding.Empty
        };
        foreach (var width in widths)
        {
            grid.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, width));
        }
        return grid;
    }

    private static void Place(TableLayoutPanel grid, Control control, int column, int row, int span = 1, bool stretch = true)
    {
        if (control is NumericUpDown)
        {
            control = new InputFrame(control);
        }
        if (stretch)
        {
            control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        }
        if (control is Button or CheckBox { Appearance: Appearance.Button })
        {
            control.Height = TrackerTheme.ControlHeight;
        }
        control.Margin = new Padding(3, 3, 5, 3);
        grid.Controls.Add(control, column, row);
        if (span > 1)
        {
            grid.SetColumnSpan(control, span);
        }
    }

    private static Panel Section(string title, Control content)
    {
        content.Anchor = AnchorStyles.Top | AnchorStyles.Left;
        var card = new Panel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = Padding.Empty,
            Margin = new Padding(0, 0, 0, 10)
        };
        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 2,
            Margin = Padding.Empty
        };
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = GridLabel(title);
        TrackerTheme.SectionHeading(heading);
        heading.Margin = new Padding(3, 4, 3, 6);
        stack.Controls.Add(heading);
        stack.Controls.Add(content);
        card.Controls.Add(stack);
        return card;
    }

    private void SyncOverlayMoveToggle()
    {
        _overlayMoveToggle.Checked = _overlayMoveMode;
    }

    private void BuildControls()
    {
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        _toolTip.BackColor = TrackerTheme.Surface;
        _toolTip.ForeColor = TrackerTheme.Text;

        _updateButton.Name = "CheckForUpdates";
        _updateButton.Click += (_, _) => CheckOrInstallUpdate();
        _updatePill.Name = "UpdateAvailable";
        _updatePill.Click += (_, _) => CheckOrInstallUpdate();
        _changelogButton.Text = "Changelog";
        _changelogButton.Click += async (_, _) => await OpenChangelogAsync(startup: false);

        var detection = Grid(112, 200);
        _captureRatePicker.Minimum = 30;
        _captureRatePicker.Maximum = 240;
        _captureRatePicker.Value = _targetCaptureRate;
        _captureRatePicker.ValueChanged += (_, _) =>
        {
            Volatile.Write(ref _targetCaptureRate, decimal.ToInt32(_captureRatePicker.Value));
            _settings.CaptureRate = _targetCaptureRate;
            SaveSettings();
        };
        _toolTip.SetToolTip(_captureRatePicker, "Capture attempts per second. 60–120 Hz is plenty; lower it if capture affects gameplay.");
        _measuredCaptureRate.Name = "MeasuredCaptureRate";
        _measuredCaptureRate.AutoSize = false;
        _measuredCaptureRate.AutoEllipsis = true;
        _measuredCaptureRate.TextAlign = ContentAlignment.MiddleLeft;
        _measuredCaptureRate.ForeColor = TrackerTheme.Muted;
        _toolTip.SetToolTip(_measuredCaptureRate, "Frames actually captured per second and the capture backend in use.");
        Place(detection, GridLabel("Capture rate (Hz)"), 0, 0);
        _captureRatePicker.Width = 96;
        Place(detection, _captureRatePicker, 1, 0, stretch: false);
        Place(detection, GridLabel("Measured"), 0, 1);
        Place(detection, _measuredCaptureRate, 1, 1);

        var shortcuts = Grid(130, 182);
        _markChargedBindingButton.Name = "MarkChargedShortcut";
        _markChargedBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.MarkCharged);
        _pauseBindingButton.Name = "PauseShortcut";
        _pauseBindingButton.Click += (_, _) => TogglePauseBindingCapture();
        _toolTip.SetToolTip(_pauseBindingButton, "Global pause / resume detection shortcut.");
        _overlayBindingButton.Name = "OverlayShortcut";
        _overlayBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.ToggleOverlay);
        _toolTip.SetToolTip(_overlayBindingButton, "Global show / hide overlay shortcut.");
        _repeatTestBindingButton.Name = "RepeatTestShortcut";
        _repeatTestBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.RepeatTest);
        _toolTip.SetToolTip(_repeatTestBindingButton, "Global start / stop repeat test shortcut.");
        foreach (var target in new[] { BindingCaptureTarget.MarkCharged, BindingCaptureTarget.Pause, BindingCaptureTarget.ToggleOverlay, BindingCaptureTarget.RepeatTest })
        {
            UpdateBindingButtonText(target);
        }
        var shortcutHint = new Label
        {
            Name = "ShortcutHint",
            Text = "Click a shortcut, then press a key. Esc clears it.",
            AutoSize = false,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            ForeColor = TrackerTheme.Muted
        };
        Place(shortcuts, GridLabel("Mark charged"), 0, 3);
        Place(shortcuts, _markChargedBindingButton, 1, 3);
        Place(shortcuts, GridLabel("Pause / resume"), 0, 0);
        Place(shortcuts, _pauseBindingButton, 1, 0);
        Place(shortcuts, GridLabel("Show / hide overlay"), 0, 1);
        Place(shortcuts, _overlayBindingButton, 1, 1);
        Place(shortcuts, GridLabel("Start / stop test"), 0, 2);
        Place(shortcuts, _repeatTestBindingButton, 1, 2);
        Place(shortcuts, shortcutHint, 0, 4, 2);

        var test = Grid(150, 190, 120, 180);
        _testAbilityPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        var abilityChoices = AbilityKinds.Select(kind => new AbilityChoice(kind, KindName(kind) == "Class" ? "Class ability" : KindName(kind))).ToArray();
        _testAbilityPicker.Items.AddRange(abilityChoices);
        _testAbilityPicker.SelectedItem = abilityChoices.FirstOrDefault(choice => choice.Kind == _settings.TestAbility);
        _testAbilityPicker.SelectedIndexChanged += (_, _) =>
        {
            if (_testAbilityPicker.SelectedItem is AbilityChoice choice)
            {
                _settings.TestAbility = choice.Kind;
                UpdateBindingButtonText(BindingCaptureTarget.AbilityUse);
                SaveSettings();
                UpdateStatus();
            }
        };
        _testCyclesPicker.Minimum = 1;
        _testCyclesPicker.Maximum = 100;
        _testCyclesPicker.Value = Math.Clamp(_settings.TestCycles, 1, 100);
        _testCyclesPicker.ValueChanged += (_, _) =>
        {
            _settings.TestCycles = decimal.ToInt32(_testCyclesPicker.Value);
            SaveSettings();
        };
        _testDelayPicker.DecimalPlaces = 1;
        _testDelayPicker.Increment = 0.1m;
        _testDelayPicker.Minimum = 0;
        _testDelayPicker.Maximum = 30;
        _testDelayPicker.Value = Math.Clamp(_settings.TestDelaySeconds, 0, 30);
        _testDelayPicker.ValueChanged += (_, _) =>
        {
            _settings.TestDelaySeconds = _testDelayPicker.Value;
            SaveSettings();
        };
        UpdateBindingButtonText(BindingCaptureTarget.AbilityUse);
        _testHoldPicker.DecimalPlaces = 2;
        _testHoldPicker.Increment = 0.05m;
        _testHoldPicker.Minimum = 0.01m;
        _testHoldPicker.Maximum = 30;
        _testHoldPicker.Value = Math.Clamp(_settings.TestHoldSeconds, 0.01m, 30);
        _testHoldPicker.AccessibleName = "Ability input hold time in seconds";
        _testHoldPicker.ValueChanged += (_, _) =>
        {
            _settings.TestHoldSeconds = _testHoldPicker.Value;
            SaveSettings();
        };
        _toolTip.SetToolTip(_testHoldPicker, "How long to hold the ability key or mouse button. Wheel bindings are single impulses.");
        _abilityUseBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.AbilityUse);
        _toolTip.SetToolTip(_abilityUseBindingButton, "The in-game key or mouse button that uses the ability; sent between cycles. Esc clears it while recording.");
        _testToggleButton.Name = "RepeatTestToggle";
        _testToggleButton.Text = "Start test";
        _testToggleButton.Click += (_, _) =>
        {
            if (_repeatTest.IsActive) StopRepeatabilityTest();
            else StartRepeatabilityTest();
        };
        _testBlockedReason.Name = "RepeatTestBlockedReason";
        _testBlockedReason.AutoSize = false;
        _testBlockedReason.AutoEllipsis = true;
        _testBlockedReason.TextAlign = ContentAlignment.MiddleRight;
        _testBlockedReason.ForeColor = TrackerTheme.Muted;
        _testStatus.AutoSize = false;
        _testStatus.AutoEllipsis = false;
        _testStatus.ForeColor = TrackerTheme.Muted;
        _testStatus.Height = 72;
        Place(test, GridLabel("Ability"), 0, 0);
        Place(test, _testAbilityPicker, 1, 0);
        Place(test, GridLabel("Cycles"), 2, 0);
        Place(test, _testCyclesPicker, 3, 0);
        Place(test, GridLabel("Ability use input"), 0, 1);
        Place(test, _abilityUseBindingButton, 1, 1);
        Place(test, GridLabel("Hold input for (s)"), 2, 1);
        Place(test, _testHoldPicker, 3, 1);
        Place(test, GridLabel("Delay after recharge (s)"), 0, 2);
        Place(test, _testDelayPicker, 1, 2);
        Place(test, _testBlockedReason, 0, 3, 3);
        Place(test, _testToggleButton, 3, 3);
        var testResult = new Panel { Height = 72 };
        _testStatus.Dock = DockStyle.Fill;
        var copyTest = new CopyResultButton("Copy repeat-test result", () => $"{KindName(_repeatTest.Ability ?? _settings.TestAbility)}: {_testStatus.Text}")
        {
            Dock = DockStyle.Top,
            Height = 30
        };
        _toolTip.SetToolTip(copyTest, "Copy repeat-test mean, uncertainty and regression");
        var copyTestPanel = new Panel { Dock = DockStyle.Right, Width = 30 };
        copyTestPanel.Controls.Add(copyTest);
        testResult.Controls.Add(_testStatus);
        testResult.Controls.Add(copyTestPanel);
        Place(test, testResult, 0, 4, 4);
        foreach (var control in new Control[] { _testAbilityPicker, _abilityUseBindingButton })
        {
            control.Margin = new Padding(3, 3, 29, 3);
        }

        var overlay = Grid(128, 190);
        _anchorPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _anchorPicker.Items.AddRange(["Top left", "Top right", "Bottom left", "Bottom right"]);
        _anchorPicker.SelectedItem = _settings.OverlayAnchor;
        if (_anchorPicker.SelectedIndex < 0)
        {
            _anchorPicker.SelectedItem = "Bottom right";
        }

        _anchorPicker.SelectedIndexChanged += (_, _) =>
        {
            _settings.OverlayAnchor = _anchorPicker.SelectedItem?.ToString() ?? "Bottom right";
            UpdateOverlay();
            SaveSettings();
        };
        var linePicker = new FlatComboBox();
        linePicker.Items.AddRange(["1 line per ability", "2 lines per ability"]);
        linePicker.SelectedIndex = _settings.OverlayTwoLines ? 1 : 0;
        linePicker.SelectedIndexChanged += (_, _) => SetOverlayTwoLines(linePicker.SelectedIndex == 1);
        _overlayMoveToggle.Text = "Move overlay";
        _overlayMoveToggle.Appearance = Appearance.Button;
        _overlayMoveToggle.TextAlign = ContentAlignment.MiddleCenter;
        SyncOverlayMoveToggle();
        _overlayMoveToggle.Click += (_, _) => SetOverlayMoveMode(_overlayMoveToggle.Checked);
        _toolTip.SetToolTip(_overlayMoveToggle, "Unlock the overlay to drag it. Turn off to restore click-through.");
        Place(overlay, GridLabel("Anchor"), 0, 0);
        Place(overlay, _anchorPicker, 1, 0);
        Place(overlay, GridLabel("Lines per ability"), 0, 1);
        Place(overlay, linePicker, 1, 1);
        Place(overlay, GridLabel("Position"), 0, 2);
        Place(overlay, _overlayMoveToggle, 1, 2);
        var troubleshooting = Grid(150, 168);
        _recordHudButton.Text = "Record HUD";
        _recordHudButton.Click += (_, _) => ToggleHudRecording();
        _toolTip.SetToolTip(_recordHudButton, "Record the tracked ability slots at 60 FPS for troubleshooting.");
        _captureFolderButton.Text = "Open recordings folder";
        _captureFolderButton.Click += (_, _) => OpenCaptureFolder();
        _toolTip.SetToolTip(_captureFolderButton, Path.GetDirectoryName(AbilityHudRecorder.DefaultPath(DateTime.Now)));
        _hudRecordingStatus.AutoSize = false;
        _hudRecordingStatus.Height = 30;
        _hudRecordingStatus.AutoEllipsis = true;
        _hudRecordingStatus.TextAlign = ContentAlignment.MiddleLeft;
        _hudRecordingStatus.Text = HudRecordingHint;
        _hudRecordingStatus.ForeColor = TrackerTheme.Muted;
        Place(troubleshooting, _recordHudButton, 0, 0);
        Place(troubleshooting, _captureFolderButton, 1, 0);
        Place(troubleshooting, _hudRecordingStatus, 0, 1, 2);

        var about = Grid(150, 172);
        _updateStatus.AutoSize = false;
        _updateStatus.Height = 30;
        _updateStatus.AutoEllipsis = true;
        _updateStatus.TextAlign = ContentAlignment.MiddleLeft;
        _updateStatus.ForeColor = TrackerTheme.Muted;
        Place(about, _updateButton, 0, 0);
        Place(about, _changelogButton, 1, 0);
        Place(about, _updateStatus, 0, 1, 2);
        foreach (var grid in new[] { detection, shortcuts, overlay, troubleshooting, about })
        {
            for (var row = 0; row < 4; row++)
            {
                grid.RowStyles.Add(new RowStyle(SizeType.Absolute, 36));
            }
        }

        _statusPill.Name = "CaptureStatus";
        _statusPill.AccessibleName = "Capture status";
        _statusPill.Anchor = AnchorStyles.Right;
        _statusPill.Margin = new Padding(3, 3, 5, 3);
        _updatePill.Anchor = AnchorStyles.Right;
        _updatePill.Margin = new Padding(3, 3, 3, 3);
        _updatePill.Visible = false;

        var root = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Padding = new Padding(12, 2, 12, 10),
            Location = Point.Empty,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var navigation = new FlowLayoutPanel
        {
            Name = "PageNavigation",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Anchor = AnchorStyles.Left,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        var navigationRow = new TableLayoutPanel
        {
            Name = "MainNavigation",
            ColumnCount = 4,
            RowCount = 1,
            Width = GridWidth,
            Height = TrackerTheme.ControlHeight + 6,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        navigationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigationRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        navigationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigationRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        navigationRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        navigationRow.Controls.Add(navigation, 0, 0);
        navigationRow.Controls.Add(_updatePill, 2, 0);
        navigationRow.Controls.Add(_statusPill, 3, 0);
        navigationRow.Layout += (_, _) => FitStatusPill(navigationRow, navigation);
        var pages = new Panel
        {
            Name = "Pages",
            Width = GridWidth,
            Height = 300,
            Margin = new Padding(0, 6, 0, 0)
        };
        void AddPage(string title, Control content)
        {
            content.Visible = _pageLinks.Count == 0;
            _selectedPage ??= content;
            _lastCompactPage ??= content;
            pages.Controls.Add(content);
            var link = new NavigationButton { Text = title, AutoSize = true, Height = TrackerTheme.ControlHeight, Margin = new Padding(0, 0, 4, 0) };
            link.Click += (_, _) =>
            {
                if (content == _historyView) SetHistoryVisible(true);
                else SelectPage(content);
            };
            _pageLinks.Add((link, content));
            navigation.Controls.Add(link);
        }

        AddPage("Tracking", BuildTrackingPage());
        var testPage = Grid(GridWidth);
        testPage.Dock = DockStyle.Top;
        testPage.Controls.Add(test);
        AddPage("Repeat test", testPage);
        var historyView = CreateHistoryView();
        historyView.Dock = DockStyle.Fill;
        AddPage("History", historyView);
        var settingsPage = Grid(GridWidth);
        settingsPage.Dock = DockStyle.Top;
        var settingsColumns = Grid(318, GridWidth - 318);
        Control[][] columns =
        [
            [Section("Capture", detection), Section("Shortcuts", shortcuts)],
            [Section("Overlay", overlay), Section("Troubleshooting", troubleshooting), Section("About", about)]
        ];
        for (var column = 0; column < columns.Length; column++)
        {
            var stack = Grid(column == 0 ? 318 : GridWidth - 318);
            stack.Anchor = AnchorStyles.Top | AnchorStyles.Left;
            for (var index = 0; index < columns[column].Length; index++)
            {
                columns[column][index].Anchor = AnchorStyles.Top | AnchorStyles.Left;
                stack.Controls.Add(columns[column][index], 0, index);
            }
            settingsColumns.Controls.Add(stack, column, 0);
        }
        settingsPage.Controls.Add(settingsColumns);
        AddPage("Settings", settingsPage);
        _settingsPage = settingsPage;
        root.Controls.Add(navigationRow);
        root.Controls.Add(pages);
        AutoSize = false;
        var viewport = new Panel { Dock = DockStyle.Fill, AutoScroll = true };
        _mainViewport = viewport;
        _fitWindowToDisplay = FitWindowToDisplay;
        viewport.Controls.Add(root);
        Controls.Add(viewport);
        TrackerTheme.Apply(root);
        foreach (var item in _pageLinks)
        {
            item.Button.BackColor = TrackerTheme.Background;
            item.Button.FlatAppearance.BorderSize = 0;
            item.Button.SetSelected(item.Page == _selectedPage);
        }
        ApplyTrackingButtonTiers();
        TrackerTheme.Primary(_testToggleButton);
        ShowUpdateState(UpdatePhase.Idle);
        UpdateOverlayToggleText();
        void FitWindowToDisplay()
        {
            if (_changelogView is not null) return;
            var scale = DeviceDpi / 96f;
            var workArea = Screen.FromControl(this).WorkingArea;
            var chrome = Size - ClientSize;
            var available = new Size(Math.Max(320, workArea.Width - chrome.Width - 24),
                Math.Max(240, workArea.Height - chrome.Height - 24));
            if (_selectedPage == historyView)
            {
                // History is a full page with its own explicit height, limited to the work area.
                pages.Height = (int)(HistoryHeight * scale);
                var overflow = root.GetPreferredSize(Size.Empty).Height + Padding.Vertical - available.Height;
                if (overflow > 0)
                    pages.Height = Math.Max((int)(HistoryMinimumHeight * scale), pages.Height - overflow);
            }
            else
            {
                // Compact pages share one height that reserves room for the Tracking banner,
                // so status changes and page switches never resize the window.
                pages.Height = Math.Max(TrackingPageHeight(),
                    Math.Max(testPage.GetPreferredSize(Size.Empty).Height, settingsPage.GetPreferredSize(Size.Empty).Height));
            }
            var desired = root.GetPreferredSize(Size.Empty);
            var height = Math.Min(desired.Height + Padding.Vertical, available.Height);
            var width = Math.Min(desired.Width + (height < desired.Height ? SystemInformation.VerticalScrollBarWidth : 0), available.Width);
            ClientSize = new Size(width + Padding.Horizontal, height);
            if (Visible && WindowState == FormWindowState.Normal && Bottom > workArea.Bottom)
                Top = Math.Max(workArea.Top, workArea.Bottom - Height);
        }
        Shown += (_, _) => FitWindowToDisplay();
        DpiChanged += (_, _) => BeginInvoke((Action)FitWindowToDisplay);
        FitWindowToDisplay();
    }

    private void SelectPage(Control page)
    {
        if (page != _historyView)
        {
            _lastCompactPage = page;
        }

        _selectedPage = page;
        foreach (var (button, content) in _pageLinks)
        {
            content.Visible = content == page;
            button.SetSelected(content == page);
        }

        _fitWindowToDisplay?.Invoke();
    }

    /// <summary>Keeps the capture pill right-aligned; it ellipsizes only when the update pill needs the room.</summary>
    private void FitStatusPill(Control navigationRow, Control navigation)
    {
        var available = navigationRow.ClientSize.Width - navigation.Width - navigation.Margin.Horizontal -
            _statusPill.Margin.Horizontal - (_updatePill.Visible ? _updatePill.Width + _updatePill.Margin.Horizontal : 0);
        var maximum = new Size(Math.Max(60, available), Math.Max(1, navigationRow.ClientSize.Height - _statusPill.Margin.Vertical));
        if (_statusPill.MaximumSize != maximum)
        {
            _statusPill.MaximumSize = maximum;
        }
    }

    private async void ToggleHudRecording()
    {
        if (_hudRecorder is { } activeRecording)
        {
            activeRecording.Stop();
            _recordHudButton.Enabled = false;
            _recordHudButton.Text = "Finishing…";
            _stopRecordingButton.Enabled = false;
            _recordingChip.Text = "Finishing HUD recording…";
            _hudRecordingStatus.Text = "Finishing the recording and writing its timestamp index…";
            return;
        }

        AbilityHudRecorder recorder;
        try
        {
            Rectangle bounds;
            AbilityHudRecordingSlot[] recordedSlots;
            lock (_captureSync)
            {
                var areas = AbilityKinds
                    .Where(IsTracked)
                    .Where(kind => GetSlot(kind).Region is not null)
                    .Select(kind =>
                    {
                        var region = ResolveCaptureRegion(GetSlot(kind), GetSlot(kind).Region!.Value);
                        return (Kind: kind, Region: region, Monitor: Screen.FromRectangle(region).Bounds);
                    })
                    .ToArray();
                bounds = AbilityHudRecorder.GetCaptureBounds(areas.Select(area => (area.Region, area.Monitor)));
                recordedSlots = areas.Select(area => AbilityHudRecordingSlot.For(area.Kind, area.Region, area.Monitor)).ToArray();
            }

            Task<AbilityHudRecordingResult>? recordingTask = null;
            recorder = new AbilityHudRecorder(bounds, Screen.FromRectangle(bounds).DeviceName,
                AbilityHudRecorder.DefaultPath(DateTime.Now),
                progress => QueueHudRecordingProgress(progress, recordingTask), recordedSlots);
            recordingTask = recorder.Completion;
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException)
        {
            MessageBox.Show(this, $"Couldn't start the HUD recording: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        lock (_captureSync)
        {
            _hudRecorder = recorder;
        }

        _hudRecordingTask = recorder.Completion;
        if (!_monitoring)
        {
            SetMonitoring(true);
        }

        _recordHudButton.Text = "Stop recording";
        _hudRecordingStatus.Text = $"Recording the HUD at {AbilityHudRecorder.FramesPerSecond} FPS…";
        _recordingChip.Text = "Recording HUD · 00:00 · 0 frames";
        _stopRecordingButton.Enabled = true;
        ShowRecordingIndicator(true);
        _overlay.Hide();
        try
        {
            var result = await recorder.Completion;
            if (!_closingAfterHudRecording)
            {
                var dropped = result.DroppedFrames > 0 ? $", {result.DroppedFrames} dropped" : string.Empty;
                _hudRecordingStatus.Text =
                    $"{result.StopReason ?? "Saved"}: {result.Frames} frames{dropped}, {result.Bytes / 1_000_000d:F1} MB";
                SetLastRecordingPath(result.Path);
            }
        }
        catch (Exception exception)
        {
            if (!_closingAfterHudRecording)
            {
                _hudRecordingStatus.Text = "Recording failed.";
                MessageBox.Show(this, $"Couldn't record the ability HUD: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        finally
        {
            lock (_captureSync)
            {
                if (ReferenceEquals(_hudRecorder, recorder))
                {
                    _hudRecorder = null;
                }
            }

            recorder.Dispose();
            if (!_closingAfterHudRecording)
            {
                _hudRecordingTask = null;
                _recordHudButton.Enabled = true;
                _recordHudButton.Text = "Record HUD";
                ShowRecordingIndicator(false);
                ShowOverlayIfEnabled();
                UpdateOverlay();
            }
        }
    }

    internal void SetLastRecordingPath(string path)
    {
        _lastRecordingPath = path;
        _toolTip.SetToolTip(_captureFolderButton, $"Latest recording: {path}");
    }

    private void OpenCaptureFolder()
    {
        var path = Path.GetDirectoryName(_lastRecordingPath ?? AbilityHudRecorder.DefaultPath(DateTime.Now))!;
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Couldn't show {path}: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void QueueHudRecordingProgress(AbilityHudRecordingProgress progress, Task<AbilityHudRecordingResult>? recording)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (recording is null || !ReferenceEquals(_hudRecordingTask, recording))
                {
                    return;
                }

                var dropped = progress.DroppedFrames > 0 ? $" · {progress.DroppedFrames} dropped" : string.Empty;
                _hudRecordingStatus.Text =
                    $"Recording HUD · {progress.Elapsed:mm\\:ss} · {progress.Frames} frames{dropped} · {progress.BytesWritten / 1_000_000d:F1} MB";
                if (_stopRecordingButton.Enabled)
                {
                    _recordingChip.Text = $"Recording HUD · {progress.Elapsed:mm\\:ss} · {progress.Frames} frames";
                }
            }));
        }
        catch (InvalidOperationException) when (IsDisposed)
        {
        }
    }

    private string RecordingTrackerState()
    {
        var builder = new StringBuilder();
        foreach (var kind in AbilityKinds)
        {
            if (builder.Length > 0)
            {
                builder.Append(',');
            }

            if (IsTracked(kind) && _trackers.TryGetValue(kind, out var tracker))
            {
                builder.Append(tracker.State).Append(',')
                    .Append(tracker.DisplayProgress?.ToString("F4", CultureInfo.InvariantCulture));
            }
            else
            {
                builder.Append(',');
            }
        }

        return builder.ToString();
    }

    private void CaptureHudRecordingFrame(IntPtr desktopDeviceContext, DesktopDuplicationCapture duplication, DateTime tickTimestamp)
    {
        var recorder = _hudRecorder;
        if (recorder is null || !recorder.IsDue())
        {
            if (recorder is null && _hudRecordingSurface is not null)
            {
                _hudRecordingSurface.Dispose();
                _hudRecordingSurface = null;
            }

            return;
        }

        try
        {
            var bounds = recorder.Bounds;
            if (_hudRecordingSurface is not { } surface || surface.Width != bounds.Width || surface.Height != bounds.Height)
            {
                _hudRecordingSurface?.Dispose();
                surface = new ScreenCaptureSurface(bounds.Width, bounds.Height, desktopDeviceContext);
                _hudRecordingSurface = surface;
            }

            // A GDI copy or an unchanged staged copy is not fresh: right after capture starts it can be a stale image.
            var fresh = false;
            if (duplication.TryCapture(recorder.DeviceName, bounds, surface, out var timestamp))
            {
                fresh = duplication.LastCaptureWasNewFrame;
            }
            else
            {
                surface.Capture(desktopDeviceContext, bounds);
                timestamp = DateTime.UtcNow;
            }

            recorder.Submit(surface.Pixels, surface.Stride, timestamp, RecordingTrackerState(), fresh);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException)
        {
            recorder.Stop($"Stopped: {exception.Message}");
        }
    }
    private void ResetSelections()
    {
        var result = MessageBox.Show(
            this,
            "Clear the saved screen areas for grenade, melee, and class ability? You can select them again at any time. Shortcuts will be kept.",
            Text,
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning);
        if (result != DialogResult.Yes)
        {
            return;
        }

        if (_repeatTest.IsActive)
        {
            StopRepeatabilityTest();
        }

        lock (_captureSync)
        {
            foreach (var slot in _settings.Slots)
            {
                slot.Region = null;
                slot.MonitorBounds = null;
                slot.MonitorDeviceName = null;
                slot.GameWindowBounds = null;
                slot.RegionAutoDetected = false;
                _trackers[slot.Kind].ResetCycle();
            }

            _settings.Region = null;
            _settings.RegionMonitorBounds = null;
            _settings.RegionMonitorDeviceName = null;
            _currentGameWindow = null;
        }

        if (_monitoring) RefreshCaptureRegions();
        SaveSettings();
        UpdateStatus();
        UpdateOverlay();
    }

    private static DisplayChoice[] DisplayChoices() => Screen.AllScreens.Select((screen, index) => new DisplayChoice(
        screen.DeviceName,
        screen.Bounds,
        $"Display {index + 1}  ·  {screen.Bounds.Width} × {screen.Bounds.Height}{(screen.Primary ? "  ·  primary" : string.Empty)}")).ToArray();

    private void SelectRegion(AbilityKind kind)
    {
        if (_selectingRegion)
        {
            return;
        }

        var target = ScreenCapture.FindDestiny2Window();
        DisplayChoice? display;
        Rectangle sourceBounds;
        string sourceDescription;
        if (target is not null)
        {
            var monitor = Screen.AllScreens.FirstOrDefault(
                screen => screen.DeviceName.Equals(target.DeviceName, StringComparison.OrdinalIgnoreCase));
            display = monitor is null
                ? null
                : new DisplayChoice(monitor.DeviceName, monitor.Bounds, $"Display · {monitor.Bounds.Width} × {monitor.Bounds.Height} · {monitor.DeviceName}");
            sourceBounds = target.Bounds;
            sourceDescription = "the Destiny 2 window";
            if (display is not null)
            {
                _lastDisplayDeviceName = display.DeviceName;
            }
        }
        else
        {
            using var picker = new DisplayPickerDialog(DisplayChoices(), _lastDisplayDeviceName);
            if (picker.ShowDialog(this) != DialogResult.OK || picker.SelectedDisplay is not { } fallbackDisplay)
            {
                return;
            }

            _lastDisplayDeviceName = fallbackDisplay.DeviceName;

            display = fallbackDisplay;
            sourceBounds = fallbackDisplay.Bounds;
            sourceDescription = "the selected display";
        }

        if (display is null)
        {
            MessageBox.Show(this, "Windows could not identify the display containing Destiny 2.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var wasMonitoring = _monitoring;
        _selectingRegion = true;
        SetMonitoring(false);
        try
        {
            Hide();
            _overlay.Hide();
            Thread.Sleep(150);
            using var snapshot = ScreenCapture.Capture(sourceBounds);
            var suggestions = AbilitySlotRegionFinder.Find(snapshot, sourceBounds);
            var slot = GetSlot(kind);
            Rectangle? initialSelection = null;
            if (slot.Region is { } savedRegion &&
                (!slot.RegionAutoDetected || suggestions.Length != AbilityKinds.Length) &&
                slot.MonitorBounds == display.Bounds &&
                string.Equals(slot.MonitorDeviceName, display.DeviceName, StringComparison.OrdinalIgnoreCase))
            {
                initialSelection = Rectangle.Intersect(savedRegion, sourceBounds);
            }
            else if (suggestions.Length == AbilityKinds.Length)
            {
                initialSelection = suggestions[Array.IndexOf(AbilityKinds, kind)];
            }

            var pickerDescription = suggestions.Length == AbilityKinds.Length
                ? $"{sourceDescription} · ability slots auto-detected; refine the highlighted selection if needed"
                : sourceDescription;
            using var picker = new RegionPicker(snapshot, sourceBounds, display.Bounds, pickerDescription, initialSelection);
            if (picker.ShowDialog() == DialogResult.OK && picker.SelectedRegion is { } region)
            {
                slot.Region = region;
                slot.MonitorBounds = display.Bounds;
                slot.MonitorDeviceName = display.DeviceName;
                slot.GameWindowBounds = target?.Bounds;
                slot.RegionAutoDetected = false;
                _trackers[kind].ResetCycle();
                SaveSettings();
                UpdateStatus();
            }
        }

        catch (Exception exception)
        {
            MessageBox.Show(this, $"Couldn't open the screen selector: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _selectingRegion = false;
            Show();
            if (!HeadlessUi.Enabled) Activate();
            ShowOverlayIfEnabled();
            UpdateOverlay();
            if (wasMonitoring)
            {
                SetMonitoring(true);
            }
        }
    }

    private void ShowSelectionGuide()
    {
        using var guide = new SelectionGuide();
        guide.ShowDialog(this);
    }

    private void CaptureLoop()
    {
        var cancellationToken = _captureCancellation.Token;
        var uiUpdateInterval = Stopwatch.Frequency / 10;
        var rateWindowStart = Stopwatch.GetTimestamp();
        var lastUiUpdate = rateWindowStart;
        var capturedFrames = 0;
        var nextFrame = Stopwatch.GetTimestamp();
        var overload = new CaptureOverloadBackoff();
        try
        {
            using var desktop = ScreenCapture.OpenDesktop();
            using var duplication = new DesktopDuplicationCapture();
            using var wait = new PrecisionWait();
            _highResolutionCaptureTimer = wait.IsHighResolution;
            while (!cancellationToken.IsCancellationRequested)
            {
                DisposeRetiredCaptureBatches();
                if (!_monitoring)
                {
                    _captureRate = 0;
                    capturedFrames = 0;
                    rateWindowStart = Stopwatch.GetTimestamp();
                    cancellationToken.WaitHandle.WaitOne(50);
                    nextFrame = Stopwatch.GetTimestamp();
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                if (now < nextFrame)
                {
                    wait.Wait(TimeSpan.FromSeconds((nextFrame - now) / (double)Stopwatch.Frequency), cancellationToken.WaitHandle);
                    continue;
                }

                var workStarted = Stopwatch.GetTimestamp();
                if (CaptureFrame(desktop.DeviceContext, duplication))
                {
                    capturedFrames++;
                    Interlocked.Exchange(ref _lastCapturedFrame, Stopwatch.GetTimestamp());
                }

                now = Stopwatch.GetTimestamp();
                // Keep the deadline phase and skip missed deadlines without catch-up bursts.
                nextFrame = CaptureSchedule.NextFrame(nextFrame, now, Volatile.Read(ref _targetCaptureRate), Stopwatch.Frequency);
                nextFrame = Math.Max(nextFrame, now + overload.Delay(now - workStarted, Stopwatch.Frequency / Volatile.Read(ref _targetCaptureRate)));

                if (now - rateWindowStart >= Stopwatch.Frequency)
                {
                    _captureRate = capturedFrames * (double)Stopwatch.Frequency / (now - rateWindowStart);
                    capturedFrames = 0;
                    rateWindowStart = now;
                    _refreshCaptureRegionsRequested = true;
                }

                if (now - lastUiUpdate >= uiUpdateInterval)
                {
                    lastUiUpdate = now;
                    RequestUiRefresh();
                }
            }
        }
        catch (Exception exception)
        {
            lock (_captureSync)
            {
                _captureLoopError = exception.Message;
                foreach (var batch in _captureBatches)
                {
                    foreach (var slot in batch.Slots)
                    {
                        _captureErrors[slot.Kind] = exception.Message;
                        _trackers[slot.Kind].MarkSignalLost(DateTime.UtcNow);
                    }
                }
            }

            RequestUiRefresh();
        }
        finally
        {
            lock (_captureSync)
            {
                foreach (var batch in _captureBatches)
                {
                    batch.Dispose();
                }

                while (_retiredCaptureBatches.TryDequeue(out var batch))
                {
                    batch.Dispose();
                }

                _hudRecordingSurface?.Dispose();
                _hudRecordingSurface = null;
            }
        }
    }

    private bool _highResolutionCaptureTimer;

    private bool CaptureFrame(IntPtr desktopDeviceContext, DesktopDuplicationCapture duplication)
    {
        var capturedAny = false;
        var duplicatedAll = true;
        double captureMs = 0, detectionMs = 0;
        string? fallbackReason = null;
        var recorderForDiagnostics = _hudRecorder;
        duplication.BeginTick();
        var testAbilityCaptured = false;
        var timestamp = DateTime.UtcNow;
        lock (_captureSync)
        {
            if (!_monitoring)
            {
                return false;
            }

            foreach (var batch in _captureBatches)
            {
                try
                {
                    var bounds = batch.Bounds;
                    var surface = batch.Surface;
                    if (surface is null || surface.Width != bounds.Width || surface.Height != bounds.Height)
                    {
                        batch.Dispose();
                        surface = new ScreenCaptureSurface(bounds.Width, bounds.Height, desktopDeviceContext);
                        batch.Surface = surface;
                    }

                    var captureClock = Stopwatch.GetTimestamp();
                    var duplicated = duplication.TryCapture(batch.DeviceName, bounds, surface, out timestamp);
                    if (!duplicated)
                    {
                        duplicatedAll = false;
                        fallbackReason ??= duplication.LastError ?? "No staged pixels available";
                        surface.Capture(desktopDeviceContext, bounds);
                        timestamp = DateTime.UtcNow;
                    }

                    captureMs += Stopwatch.GetElapsedTime(captureClock).TotalMilliseconds;
                    if (duplicated && !duplication.LastCaptureWasNewFrame) continue;
                    var detectionClock = Stopwatch.GetTimestamp();
                    capturedAny = true;
                    foreach (var slot in batch.Slots)
                    {
                        var region = slot.Bounds;
                        var tracker = _trackers[slot.Kind];
                        testAbilityCaptured |= _repeatTest.Ability == slot.Kind;
                        var completed = tracker.Observe(
                            AbilitySignalDetector.Read(
                                surface.Pixels,
                                surface.Stride,
                                region.Left - bounds.Left,
                                region.Top - bounds.Top,
                                region.Width,
                                region.Height,
                                slot.BottomPadding,
                                tracker.ChargeProgress,
                                tracker.ProgressSearchTolerance(timestamp),
                                slot.TopPadding,
                                tracker.Guide(timestamp)),
                            timestamp);
                        _repeatTest.ObserveState(slot.Kind, tracker.State, timestamp);
                        if (completed && tracker.LastDuration is { } duration)
                        {
                            var validated = RecordValidatedMeasurement(slot.Kind, tracker, timestamp);
                            var completingRepeat = _repeatTest.Ability == slot.Kind &&
                                _repeatTest.Phase == RepeatTestPhase.WaitingForRecharge;
                            _repeatTest.RecordRecharge(slot.Kind, duration, timestamp, tracker.LastRegressionDuration, validated);
                            if (completingRepeat && _repeatTest.ValidatedSummary is { } summary)
                                SaveHistoryEntry(summary, _repeatHistorySessionId);
                        }
                        if (tracker.MeasurementDiscarded && _repeatTest.Ability == slot.Kind && _repeatTest.IsActive)
                        {
                            _repeatTest.Fail(tracker.TrackingWarning!);
                        }

                        _captureErrors.Remove(slot.Kind);
                    }
                    detectionMs += Stopwatch.GetElapsedTime(detectionClock).TotalMilliseconds;
                }
                catch (Exception exception)
                {
                    foreach (var slot in batch.Slots)
                    {
                        _captureErrors[slot.Kind] = exception.Message;
                        _trackers[slot.Kind].MarkSignalLost(timestamp);
                    }
                }

            }

            var submitClock = Stopwatch.GetTimestamp();
            CaptureHudRecordingFrame(desktopDeviceContext, duplication, timestamp);
            recorderForDiagnostics?.RecordDiagnostics(duplicatedAll ? "DXGI" : "GDI", fallbackReason,
                _highResolutionCaptureTimer, captureMs, detectionMs, Stopwatch.GetElapsedTime(submitClock).TotalMilliseconds);

            if (testAbilityCaptured &&
                _repeatInputTask.IsCompleted &&
                _repeatTest.IsUseDue(timestamp) &&
                _repeatTest.Ability is { } testAbility &&
                !_captureErrors.ContainsKey(testAbility) &&
                _trackers[testAbility].IsCharged)
            {
                if (_repeatTest.TryGetDueUse(timestamp, InputBindingSender.IsDestiny2Foreground(), out var binding))
                {
                    _repeatInputTask = SendRepeatInputAsync(binding, _repeatTest.RunId, _repeatTest.HoldMilliseconds);
                }
            }
        }

        if (capturedAny)
        {
            _captureBackend = duplicatedAll ? "DXGI" : "GDI";
        }

        return capturedAny;
    }

    private async Task SendRepeatInputAsync(InputBinding binding, Guid runId, int holdMilliseconds)
    {
        try
        {
            if (!InputBindingSender.IsDestiny2Foreground())
                throw new InvalidOperationException("Destiny 2 lost focus before the ability input could be sent.");
            await InputBindingSender.PressAndReleaseAsync(binding, holdMilliseconds, () =>
            {
                lock (_captureSync)
                    return _monitoring && _repeatTest.IsActive && _repeatTest.RunId == runId &&
                        InputBindingSender.IsDestiny2Foreground();
            }).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            lock (_captureSync)
            {
                if (_repeatTest.RunId == runId && _repeatTest.IsActive)
                    _repeatTest.Fail(exception.Message);
            }
            RequestUiRefresh();
        }
    }

    private void RequestUiRefresh()
    {
        if (!IsHandleCreated || IsDisposed || Interlocked.Exchange(ref _uiRefreshPending, 1) != 0)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                Interlocked.Exchange(ref _uiRefreshPending, 0);
                if (!IsDisposed)
                {
                    if (_refreshCaptureRegionsRequested)
                    {
                        _refreshCaptureRegionsRequested = false;
                        if (_monitoring) RefreshCaptureRegions();
                    }

                    UpdateStatus();
                    UpdateOverlay();
                }
            }));
        }
        catch (InvalidOperationException) when (_captureCancellation.IsCancellationRequested)
        {
            Interlocked.Exchange(ref _uiRefreshPending, 0);
        }
    }

    private void UpdateStatus()
    {
        Dictionary<AbilityKind, AbilitySlotTracker> trackers;
        Dictionary<AbilityKind, string> captureErrors;
        AbilityKind[] capturedKinds;
        bool testActive;
        AbilityKind? testAbility;
        RepeatTestPhase testPhase;
        string testStatus;
        lock (_captureSync)
        {
            trackers = _trackers.ToDictionary(p => p.Key, p => p.Value.DisplaySnapshot());
            captureErrors = new(_captureErrors);
            capturedKinds = CapturedKinds();
            testActive = _repeatTest.IsActive;
            testAbility = _repeatTest.Ability;
            testPhase = _repeatTest.Phase;
            testStatus = _repeatTest.Status(DateTime.UtcNow);
        }
        {
            var errors = RouteCaptureErrors(captureErrors, capturedKinds);
            var stalled = CaptureStalled(capturedKinds.Length > 0);
            var anyRegions = _settings.Slots.Any(slot => slot.Region is not null);
            UpdateTrackingLayout(anyRegions);
            _trackSelector.SelectedIndex = _settings.SoloAbility is { } soloKind ? 1 + Array.IndexOf(AbilityKinds, soloKind) : 0;
            foreach (var kind in AbilityKinds)
            {
                var slot = GetSlot(kind);
                var tracker = trackers[kind];
                var statusLabel = _statusLabels[kind];
                var areaTip = slot.Region is { } region
                    ? $"{KindName(kind)} area · {region.Width} × {region.Height} px"
                    : $"{KindName(kind)} area not set · use Areas ▾ to select it";
                if (_toolTip.GetToolTip(_abilityNameLabels[kind]) != areaTip)
                {
                    _toolTip.SetToolTip(_abilityNameLabels[kind], areaTip);
                }

                var rowError = _monitoring ? errors.RowErrors.GetValueOrDefault(kind) : null;
                if (_toolTip.GetToolTip(statusLabel) != (rowError ?? string.Empty))
                {
                    _toolTip.SetToolTip(statusLabel, rowError);
                }

                if (!IsTracked(kind))
                {
                    statusLabel.Text = "Skipped\n";
                    statusLabel.ForeColor = TrackerTheme.Muted;
                    continue;
                }

                if (!_monitoring)
                {
                    statusLabel.Text = "Paused · " + PausedMeasurementStatus(tracker);
                    statusLabel.ForeColor = TrackerTheme.Muted;
                    continue;
                }

                if (rowError is not null)
                {
                    // Only failures specific to this slot are shown in red on its row.
                    statusLabel.Text = $"Capture failed\n{rowError.ReplaceLineEndings(" ")}";
                    statusLabel.ForeColor = TrackerTheme.Red;
                    continue;
                }

                if (captureErrors.TryGetValue(kind, out var sharedError))
                {
                    // Global failures are explained once by the banner; rows keep their last result.
                    statusLabel.Text = UnavailableMeasurementStatus(tracker,
                        sharedError == GameWindowUnavailableError ? "Waiting for game"
                            : _captureLoopError is not null ? "Capture stopped" : "Not capturing",
                        string.Empty);
                    statusLabel.ForeColor = TrackerTheme.Muted;
                    continue;
                }

                if (slot.Region is not null && stalled)
                {
                    statusLabel.Text = UnavailableMeasurementStatus(tracker);
                    statusLabel.ForeColor = TrackerTheme.Muted;
                    continue;
                }

                statusLabel.Text = MeasurementStatus(tracker, slot.Region is not null);
                statusLabel.ForeColor = tracker.State switch
                {
                    AbilityState.Consumed => TrackerTheme.Orange,
                    AbilityState.Recharging => TrackerTheme.Accent,
                    AbilityState.Complete => TrackerTheme.Green,
                    _ => TrackerTheme.Muted
                };
                if (tracker.TrackingWarning is { } warning)
                {
                    statusLabel.Text = $"{FormatDuration(tracker.CurrentDuration)}\n{warning.Replace("Mark charged", WithShortcut("Mark charged", MarkChargedBinding)).ReplaceLineEndings(" ")}";
                    statusLabel.ForeColor = TrackerTheme.Orange;
                }
                else if (tracker.State == AbilityState.Recharging && tracker.DisplayProgress is { } progress)
                {
                    statusLabel.Text = $"Recharging · {FormatDuration(tracker.CurrentDuration)} · {progress:P0}\n";
                    if (tracker.EstimatedDuration is { } estimated && tracker.EstimateUncertainty is { } spread)
                    {
                        statusLabel.Text += $"Estimated {estimated.TotalSeconds:F2} ± {spread.TotalSeconds:F2} s";
                    }
                }
            }

            _markChargedButton.Text = WithShortcut("Mark charged", MarkChargedBinding);
            _toggleButton.Text = WithShortcut(_monitoring ? "Pause detection" : "Resume detection", PauseBinding);
            _testToggleButton.Text = WithShortcut(testActive ? "Stop test" : "Start test", _settings.RepeatTestBinding);
            _measuredCaptureRate.Text = DescribeMeasuredCapture();
            UpdateOverlayToggleText();
            ShowTrackingStatus(DescribeTrackingStatus(new TrackingStatusSnapshot(
                _monitoring,
                PauseBinding,
                _captureLoopError,
                _inputListenerError,
                capturedKinds.Length > 0,
                errors,
                stalled,
                _captureBackend,
                _captureRate,
                _targetCaptureRate,
                anyRegions)));
            if (_soloBeforeTest is { } previousSolo && !testActive)
            {
                // Restore the user's Track choice once the test that narrowed it ends.
                _soloBeforeTest = null;
                if (_settings.SoloAbility == testAbility && IsHandleCreated)
                    BeginInvoke(() => SetSolo(previousSolo));
            }
            var soloNote = testActive && _soloBeforeTest is not null && testAbility is { } soloTest
                ? $"Tracking {KindName(soloTest)} only during the test · "
                : string.Empty;
            _testStatus.Text = testPhase == RepeatTestPhase.Idle
                ? "Ready · start the test, then manually use the ability once. Cycle results appear here."
                : soloNote + testStatus;
            _testAbilityPicker.Enabled = !testActive && _bindingCaptureTarget != BindingCaptureTarget.AbilityUse;
            _testCyclesPicker.Enabled = !testActive;
            _testDelayPicker.Enabled = !testActive;
            _testHoldPicker.Enabled = !testActive;
            _abilityUseBindingButton.Enabled = !testActive;
            _repeatTestBindingButton.Enabled = !testActive;
            var blockedReason = StartTestBlockedReason();
            _testToggleButton.Enabled = blockedReason is null;
            _testBlockedReason.Text = blockedReason ?? string.Empty;
            if (_toolTip.GetToolTip(_testBlockedReason) != (blockedReason ?? string.Empty))
                _toolTip.SetToolTip(_testBlockedReason, blockedReason ?? string.Empty);
        }

    }

    /// <summary>Why the repeat test can't start right now, or null when it can (or is already running).</summary>
    private string? StartTestBlockedReason()
    {
        if (_repeatTest.IsActive) return null;
        if (!_inputListenerAvailable) return "Keyboard and mouse hooks are unavailable.";
        if (_bindingCaptureTarget is not null) return "Finish recording the input first.";
        var ability = GetSelectedTestAbility();
        if (GetSlot(ability).Region is null) return $"Select the {KindName(ability)} area on Tracking first.";
        if (GetAbilityUseBinding(ability) is null) return $"Record the {KindName(ability)} ability-use input first.";
        return null;
    }

    private string DescribeMeasuredCapture()
    {
        if (!_monitoring) return "Paused";
        if (!HasRecentCapture()) return $"Waiting for frames · {_captureBackend}";
        return $"{_captureRate:F0} Hz · {_captureBackend}";
    }

    private bool HasRecentCapture() => CaptureIsRecent(Stopwatch.GetTimestamp(),
        Interlocked.Read(ref _lastCapturedFrame), Stopwatch.Frequency);

    internal static bool CaptureIsRecent(long now, long lastFrame, long frequency) =>
        lastFrame > 0 && now >= lastFrame && now - lastFrame < frequency * 2;

    private readonly Button _markChargedButton = new();
    private readonly Button _markChargedBindingButton = new();
    private InputBinding? MarkChargedBinding => _settings.MarkChargedShortcutCleared ? null : _settings.MarkChargedBinding ?? InputBinding.DefaultMarkCharged;

    private void MarkTrackedCharged()
    {
        lock (_captureSync)
        {
            foreach (var kind in AbilityKinds.Where(IsTracked)) _trackers[kind].MarkCharged();
        }
        UpdateStatus();
        UpdateOverlay();
    }

    private InputBinding? PauseBinding => _settings.PauseShortcutCleared ? null : _settings.PauseBinding ?? InputBinding.DefaultPause;

    private InputBinding? OverlayToggleBinding
    {
        get
        {
            if (_settings.OverlayToggleBinding is { } configured)
            {
                return configured;
            }

            if (_settings.OverlayShortcutCleared)
            {
                return null;
            }

            // F9 is the default only while no other shortcut already uses it.
            var fallback = new InputBinding(InputDevice.Keyboard, (int)Keys.F9, InputModifiers.None);
            var taken = Enum.GetValues<BindingCaptureTarget>()
                .Where(target => target != BindingCaptureTarget.ToggleOverlay)
                .Any(target => GetConfiguredBinding(target) == fallback);
            return taken ? null : fallback;
        }
    }

    private void ShowOverlayIfEnabled()
    {
        if (_settings.OverlayVisible)
        {
            _overlay.Show();
            UpdateOverlay();
        }
        else
        {
            _overlay.Hide();
        }
    }

    private void SetOverlayVisible(bool visible)
    {
        _settings.OverlayVisible = visible;
        SaveSettings();
        UpdateOverlayToggleText();
        if (!_selectingRegion && _hudRecordingTask is null)
        {
            ShowOverlayIfEnabled();
        }
    }

    private void UpdateOverlayToggleText()
    {
        _overlaySwitch.Checked = _settings.OverlayVisible;
        _overlaySwitch.Hint = OverlayToggleBinding?.ToString() ?? string.Empty;
        _overlaySwitch.AccessibleName = WithShortcut("Show overlay", OverlayToggleBinding);
        var tip = WithShortcut(_settings.OverlayVisible ? "Overlay shown · click to hide" : "Overlay hidden · click to show",
            OverlayToggleBinding);
        if (_toolTip.GetToolTip(_overlaySwitch) != tip)
        {
            _toolTip.SetToolTip(_overlaySwitch, tip);
        }
    }

    private async void CheckOrInstallUpdate()
    {
        if (_updateBusy)
        {
            return;
        }

        await CheckAndOfferUpdateAsync(
            () => CheckForUpdateAsync(interactive: true),
            release =>
            {
                if (!_storagePaths.IsPortable) return InstallUpdateAsync(release);
                OpenPortableReleasePage();
                return Task.CompletedTask;
            });
    }

    private void OpenPortableReleasePage()
    {
        try
        {
            Process.Start(new ProcessStartInfo($"https://github.com/{UpdateChecker.Repository}/releases/latest")
            {
                UseShellExecute = true
            });
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            MessageBox.Show(this, $"Couldn't open the release page: {exception.Message}", Text,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    internal static async Task CheckAndOfferUpdateAsync(
        Func<Task<UpdateRelease?>> checkForUpdate,
        Func<UpdateRelease, Task> offerUpdate)
    {
        if (await checkForUpdate() is { } release)
        {
            await offerUpdate(release);
        }
    }

    internal async Task<UpdateRelease?> CheckForUpdateAsync(bool interactive,
        Func<CancellationToken, Task<UpdateRelease?>>? checkForUpdate = null)
    {
        _updateBusy = true;
        _availableUpdate = null;
        ShowUpdateState(UpdatePhase.Checking);
        string? checkError = null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _availableUpdate = await (checkForUpdate ?? UpdateChecker.CheckAsync)(timeout.Token);
            if (_availableUpdate is null && interactive)
            {
                MessageBox.Show(this, $"You're on the latest version (v{UpdateChecker.CurrentVersion.ToString(3)}).", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            checkError = exception.Message;
            if (interactive)
            {
                MessageBox.Show(this, $"Couldn't check for updates: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
        finally
        {
            _updateBusy = false;
            if (!IsDisposed)
            {
                if (checkError is not null) ShowUpdateState(UpdatePhase.Failed, error: checkError);
                else if (_availableUpdate is { } release) ShowUpdateState(UpdatePhase.Available, release);
                else ShowUpdateState(UpdatePhase.UpToDate);
            }
        }

        return _availableUpdate;
    }

    private async Task InstallUpdateAsync(UpdateRelease release)
    {
        var answer = MessageBox.Show(
            this,
            $"Version {release.Tag} is available (you have v{UpdateChecker.CurrentVersion.ToString(3)}).{Environment.NewLine}{Environment.NewLine}" +
            "Download and install it now? The tracker closes during the update and restarts automatically; your settings are kept.",
            Text,
            MessageBoxButtons.YesNoCancel,
            MessageBoxIcon.Question,
            MessageBoxDefaultButton.Button1);
        if (answer != DialogResult.Yes)
        {
            return;
        }

        if (_hudRecordingTask is not null)
        {
            MessageBox.Show(this, "Stop the HUD recording before updating.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _updateBusy = true;
        ShowUpdateState(UpdatePhase.Downloading, release);
        try
        {
            var progress = new Progress<double>(fraction => ShowUpdateState(UpdatePhase.Downloading, release, progress: fraction));
            var installer = await UpdateChecker.DownloadAsync(release, progress, CancellationToken.None);
            if (!SaveSettings())
            {
                _updateBusy = false;
                ShowUpdateState(UpdatePhase.Available, release);
                return;
            }

            ShowUpdateState(UpdatePhase.Installing, release);
            UpdateChecker.LaunchInstaller(installer);
            Close();
        }
        catch (Exception exception)
        {
            _updateBusy = false;
            ShowUpdateState(UpdatePhase.Available, release);
            var openPage = MessageBox.Show(
                this,
                $"The update couldn't be installed automatically: {exception.Message}{Environment.NewLine}{Environment.NewLine}Open the release page to download it manually?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning);
            if (openPage == DialogResult.Yes)
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.PageUrl.ToString()) { UseShellExecute = true });
            }
        }
    }

    private async Task OpenChangelogAsync(bool startup)
    {
        if (_changelogBusy)
        {
            return;
        }

        _changelogBusy = true;
        _changelogButton.Enabled = false;
        _changelogButton.Text = "Loading…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            IReadOnlyList<ChangelogRelease> releases;
            var hasUnseenVersions = _lastChangelogVersion < UpdateChecker.CurrentVersion;
            if (hasUnseenVersions)
            {
                releases = await UpdateChecker.GetChangelogAsync(
                    _lastChangelogVersion,
                    UpdateChecker.CurrentVersion,
                    timeout.Token);
            }
            else
            {
                releases = await UpdateChecker.GetRecentChangelogAsync(
                    UpdateChecker.CurrentVersion,
                    maximumReleases: 1,
                    timeout.Token);
                releases = releases.Where(release => release.Version == UpdateChecker.CurrentVersion).ToArray();
            }

            if (releases.Count > 0)
            {
                var summary = hasUnseenVersions
                    ? $"Changes since v{_lastChangelogVersion.ToString(3)} · {releases.Count} release{(releases.Count == 1 ? string.Empty : "s")}"
                    : $"Changes in v{UpdateChecker.CurrentVersion.ToString(3)}";
                ShowChangelog(summary, releases);
            }

            else if (!startup)
            {
                MessageBox.Show(this, "No release notes are available for this version.", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            if (hasUnseenVersions)
            {
                var previousVersion = _lastChangelogVersion;
                _settings.LastChangelogVersion = UpdateChecker.CurrentVersion.ToString(3);
                if (SaveSettings())
                {
                    _lastChangelogVersion = UpdateChecker.CurrentVersion;
                    _startupChangelogPending = false;
                }
                else
                {
                    _settings.LastChangelogVersion = previousVersion.ToString(3);
                }
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            MessageBox.Show(this,
                $"Couldn't load the changelog: {exception.Message}{Environment.NewLine}{Environment.NewLine}" +
                "Your version history was not advanced. Try again with the Changelog button.",
                Text,
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
        finally
        {
            _changelogBusy = false;
            if (!IsDisposed)
            {
                _changelogButton.Enabled = true;
                _changelogButton.Text = "Changelog";
            }
        }
    }

    internal void ShowChangelog(string summary, IReadOnlyList<ChangelogRelease> releases)
    {
        _changelogView?.Dispose();
        var view = new ChangelogView(summary, releases);
        _changelogView = view;
        if (_mainViewport is not null) _mainViewport.Visible = false;
        Controls.Add(view);
        view.BringToFront();
        var workArea = Screen.FromControl(this).WorkingArea;
        ClientSize = new Size(ClientSize.Width, Math.Min((int)(480 * DeviceDpi / 96f), workArea.Height - 64));
        view.CloseRequested += (_, _) =>
        {
            _changelogView = null;
            Controls.Remove(view);
            view.Dispose();
            if (_mainViewport is not null) _mainViewport.Visible = true;
            _fitWindowToDisplay?.Invoke();
        };
    }

    private void TogglePauseBindingCapture()
    {
        ToggleBindingCapture(BindingCaptureTarget.Pause);
    }

    private void ToggleBindingCapture(BindingCaptureTarget target)
    {
        if (!_inputListenerAvailable)
        {
            MessageBox.Show(this, $"Global keyboard and mouse hooks are unavailable: {_inputListenerError}", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        if (_bindingCaptureTarget == target)
        {
            _inputListener.CancelCapture();
            _bindingCaptureTarget = null;
            UpdateBindingButtonText(target);
            UpdateStatus();
            return;
        }

        if (_bindingCaptureTarget is { } previousTarget)
        {
            _inputListener.CancelCapture();
            UpdateBindingButtonText(previousTarget);
        }

        _bindingCaptureTarget = target;
        var allowMouse = target == BindingCaptureTarget.AbilityUse;
        GetBindingButton(target).Text = allowMouse ? "Press a key or button…" : "Press a key…";
        UpdateStatus();
        _inputListener.BeginCapture(binding =>
        {
            if (IsDisposed || !IsHandleCreated)
            {
                return;
            }

            try
            {
                BeginInvoke((Action)(() =>
                {
                    _bindingCaptureTarget = null;
                    if (binding.IsClearRequest)
                    {
                        ClearBinding(target);
                        UpdateBindingButtonText(target);
                        SaveSettings();
                        UpdateStatus();
                        return;
                    }

                    var conflicts = Enum.GetValues<BindingCaptureTarget>()
                        .Any(other => other != target && GetConfiguredBinding(other) == binding);
                    if (conflicts)
                    {
                        MessageBox.Show(this, "Use a different input for each shortcut and ability use.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        UpdateBindingButtonText(target);
                        UpdateStatus();
                        return;
                    }

                    if (target == BindingCaptureTarget.Pause)
                    {
                        _settings.PauseBinding = binding;
                        _settings.PauseShortcutCleared = false;
                        _settings.Hotkey = binding.Device == InputDevice.Keyboard && binding.Modifiers == InputModifiers.None
                            ? ((Keys)binding.Code).ToString()
                            : binding.ToString();
                    }
                    else if (target == BindingCaptureTarget.MarkCharged)
                    {
                        _settings.MarkChargedBinding = binding;
                        _settings.MarkChargedShortcutCleared = false;
                    }
                    else if (target == BindingCaptureTarget.AbilityUse)
                    {
                        _settings.AbilityUseBindings[GetSelectedTestAbility()] = binding;
                    }
                    else if (target == BindingCaptureTarget.RepeatTest)
                    {
                        _settings.RepeatTestBinding = binding;
                    }
                    else if (target == BindingCaptureTarget.ToggleOverlay)
                    {
                        _settings.OverlayToggleBinding = binding;
                        _settings.OverlayShortcutCleared = false;
                    }

                    UpdateBindingButtonText(target);
                    SaveSettings();
                    UpdateStatus();
                }));
            }
            catch (InvalidOperationException) when (IsDisposed)
            {
            }
        }, allowMouse);
    }

    private void ClearBinding(BindingCaptureTarget target)
    {
        switch (target)
        {
            case BindingCaptureTarget.MarkCharged:
                _settings.MarkChargedBinding = null;
                _settings.MarkChargedShortcutCleared = true;
                break;
            case BindingCaptureTarget.Pause:
                _settings.PauseBinding = null;
                _settings.PauseShortcutCleared = true;
                break;
            case BindingCaptureTarget.AbilityUse:
                _settings.AbilityUseBindings.Remove(GetSelectedTestAbility());
                break;
            case BindingCaptureTarget.RepeatTest:
                _settings.RepeatTestBinding = null;
                break;
            case BindingCaptureTarget.ToggleOverlay:
                _settings.OverlayToggleBinding = null;
                _settings.OverlayShortcutCleared = true;
                break;
        }
    }

    private Button GetBindingButton(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.MarkCharged => _markChargedBindingButton,
        BindingCaptureTarget.Pause => _pauseBindingButton,
        BindingCaptureTarget.AbilityUse => _abilityUseBindingButton,
        BindingCaptureTarget.RepeatTest => _repeatTestBindingButton,
        BindingCaptureTarget.ToggleOverlay => _overlayBindingButton,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void UpdateBindingButtonText(BindingCaptureTarget target)
    {
        var button = GetBindingButton(target);
        button.Text = target == BindingCaptureTarget.AbilityUse
            ? $"{KindName(GetSelectedTestAbility())}: {GetAbilityUseBinding(GetSelectedTestAbility())?.ToString() ?? "Record input…"}"
            : GetConfiguredBinding(target)?.ToString() ?? "Record input…";
        if (target == BindingCaptureTarget.AbilityUse)
            button.AccessibleName = $"Ability-use input for {KindName(GetSelectedTestAbility())}";
    }

    private InputBinding? GetConfiguredBinding(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.MarkCharged => MarkChargedBinding,
        BindingCaptureTarget.Pause => PauseBinding,
        BindingCaptureTarget.AbilityUse => GetAbilityUseBinding(GetSelectedTestAbility()),
        BindingCaptureTarget.RepeatTest => _settings.RepeatTestBinding,
        BindingCaptureTarget.ToggleOverlay => OverlayToggleBinding,
        _ => null
    };

    private void StartRepeatabilityTest()
    {
        if (_bindingCaptureTarget is not null)
        {
            MessageBox.Show(this, "Finish recording the input binding before starting a test.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (_testAbilityPicker.SelectedItem is not AbilityChoice choice || GetSlot(choice.Kind).Region is null)
        {
            MessageBox.Show(this, "Select a HUD region for the ability you want to test.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        var ability = choice.Kind;
        if (GetAbilityUseBinding(ability) is not { } binding)
        {
            MessageBox.Show(this, $"Record the {KindName(ability)} ability-use key or mouse button first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_monitoring)
        {
            SetMonitoring(true);
        }

        lock (_captureSync)
        {
            _repeatHistorySessionId = _measurementHistory?.ActiveSessionId;
            _repeatTest.Start(
                ability,
                decimal.ToInt32(_testCyclesPicker.Value),
                TimeSpan.FromSeconds((double)_testDelayPicker.Value),
                binding,
                decimal.ToInt32(_testHoldPicker.Value * 1000));
            _settings.TestAbility = ability;
            _settings.TestCycles = decimal.ToInt32(_testCyclesPicker.Value);
            _settings.TestDelaySeconds = _testDelayPicker.Value;
            _settings.TestHoldSeconds = _testHoldPicker.Value;
        }

        if (_settings.SoloAbility is { } solo && solo != _settings.TestAbility)
        {
            _soloBeforeTest = solo;
            SetSolo(_settings.TestAbility);
        }

        SaveSettings();
        UpdateStatus();
    }

    private AbilityKind GetSelectedTestAbility() =>
        (_testAbilityPicker.SelectedItem as AbilityChoice)?.Kind ?? _settings.TestAbility;

    private InputBinding? GetAbilityUseBinding(AbilityKind ability) =>
        _settings.AbilityUseBindings.TryGetValue(ability, out var binding) ? binding : _settings.AbilityUseBinding;

    private void StopRepeatabilityTest()
    {
        lock (_captureSync)
        {
            _repeatTest.Stop();
        }

        UpdateStatus();
    }

    private void OnGlobalBindingPressed(object? sender, InputBindingEventArgs args)
    {
        // The dedicated hook thread must never wait for the UI or capture worker.
        QueueInputAction(() => HandleGlobalBinding(args.Binding));
    }

    private void QueueInputAction(Action action)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(action);
        }
        catch (InvalidOperationException) when (_captureCancellation.IsCancellationRequested || IsDisposed)
        {
        }
    }

    private void HandleGlobalBinding(InputBinding binding)
    {
        if (_selectingRegion || _bindingCaptureTarget is not null || IsDisposed)
        {
            return;
        }

        BindingCaptureTarget? action = null;
        foreach (var target in Enum.GetValues<BindingCaptureTarget>())
        {
            if (target != BindingCaptureTarget.AbilityUse && GetConfiguredBinding(target) == binding)
            {
                action = target;
                break;
            }
        }
        if (action is null)
        {
            return;
        }

        switch (action.Value)
        {
            case BindingCaptureTarget.MarkCharged:
                MarkTrackedCharged();
                break;
            case BindingCaptureTarget.Pause:
                SetMonitoring(!_monitoring);
                break;
            case BindingCaptureTarget.RepeatTest:
                if (_repeatTest.IsActive) StopRepeatabilityTest();
                else StartRepeatabilityTest();
                break;
            case BindingCaptureTarget.ToggleOverlay:
                SetOverlayVisible(!_settings.OverlayVisible);
                break;
        }
    }

    private void SetMonitoring(bool enabled)
    {
        if (_monitoring == enabled)
        {
            UpdateStatus();
            UpdateOverlay();
            return;
        }
        lock (_captureSync)
        {

            _monitoring = enabled;
            Interlocked.Exchange(ref _lastCapturedFrame, 0);
            _monitoringStarted = Stopwatch.GetTimestamp();
            if (!enabled)
            {
                _repeatTest.Stop();
            }

            if (enabled)
            {
                foreach (var tracker in _trackers.Values)
                {
                    if (!tracker.LearningChargedReference) tracker.ResetCycle();
                }

            }
        }

        if (enabled) RefreshCaptureRegions();
        UpdateStatus();
        UpdateOverlay();
    }

    private bool IsTracked(AbilityKind kind) => _settings.SoloAbility is not { } solo || solo == kind;

    internal static string PausedMeasurementStatus(AbilitySlotTracker tracker)
        => MeasurementStatus(tracker, true, paused: true);

    internal static string UnavailableMeasurementStatus(AbilitySlotTracker tracker,
        string reason = "No capture frames", string hint = "Waiting for screen capture")
    {
        if (tracker.LastDuration is not { } duration)
        {
            return $"{reason}\n{hint}";
        }

        var regression = RegressionSuffix(tracker);
        return $"{reason}\nLast {FormatDuration(duration)}{MeasurementSuffix(tracker)}" +
            (regression.Length == 0 ? string.Empty : $" · {regression}");
    }

    internal static string WithShortcut(string text, InputBinding? binding) =>
        binding is null ? text : $"{text} ({binding})";

    internal static string MeasurementStatus(AbilitySlotTracker tracker, bool hasRegion, bool paused = false)
    {
        var state = tracker.State switch
        {
            AbilityState.Recharging => $"Recharging · {FormatDuration(tracker.CurrentDuration)}{(paused ? " (stopped)" : string.Empty)}",
            AbilityState.Consumed => "Used · waiting for start",
            AbilityState.Ready => "Ready",
            AbilityState.Complete => "Complete",
            _ => hasRegion ? "Watching for start" : "Area not set"
        };
        if (tracker.LastDuration is { } duration)
        {
            state += $" · last {FormatDuration(duration)}{MeasurementSuffix(tracker)}";
        }
        return $"{state}\n{RegressionSuffix(tracker)}";
    }

    private void SetSolo(AbilityKind? kind)
    {
        lock (_captureSync)
        {
            if (_settings.SoloAbility == kind)
            {
                return;
            }

            _settings.SoloAbility = kind;
            foreach (var other in AbilityKinds)
            {
                if (!IsTracked(other))
                {
                    _trackers[other].ResetCycle();
                }
            }

        }

        RefreshCaptureRegions();
        SaveSettings();
        UpdateStatus();
        UpdateOverlay();
    }

    private void RefreshCaptureRegions()
    {
        var regionErrors = new Dictionary<AbilityKind, string>();
        _currentGameWindow = _settings.Slots.Any(slot => slot.GameWindowBounds is not null)
            ? ScreenCapture.FindDestiny2Window()
            : null;
        var groups = new Dictionary<string, List<CaptureSlot>>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < AbilityKinds.Length; index++)
        {
            var kind = AbilityKinds[index];
            var slot = GetSlot(kind);
            if (!IsTracked(kind))
            {
                regionErrors.Remove(kind);
                continue;
            }

            try
            {
                if (slot.Region is { } region)
                {
                    var resolvedRegion = ResolveCaptureRegion(slot, region);
                    var deviceName = Screen.FromRectangle(resolvedRegion).DeviceName;
                    if (!groups.TryGetValue(deviceName, out var regions))
                    {
                        regions = [];
                        groups.Add(deviceName, regions);
                    }

                    var monitorBounds = Screen.FromRectangle(resolvedRegion).Bounds;
                    var bottomPadding = Math.Min(AbilitySignalDetector.BottomPadding(resolvedRegion.Height),
                        monitorBounds.Bottom - resolvedRegion.Bottom);
                    var topPadding = Math.Min(AbilitySignalDetector.TopPadding(resolvedRegion.Height),
                        resolvedRegion.Top - monitorBounds.Top);
                    regions.Add(new CaptureSlot(kind, resolvedRegion, Math.Max(0, bottomPadding), Math.Max(0, topPadding)));
                }

                regionErrors.Remove(kind);
            }
            catch (Exception exception)
            {
                regionErrors[kind] = exception.Message;
            }
        }

        var batches = groups.SelectMany(group =>
        {
            var slots = group.Value;
            var batchesForDisplay = new List<CaptureBatch>();
            var pending = new List<CaptureSlot>();
            var bounds = Rectangle.Empty;
            long totalArea = 0;
            foreach (var slot in slots)
            {
                var captureBounds = new Rectangle(slot.Bounds.X, slot.Bounds.Y - slot.TopPadding, slot.Bounds.Width,
                    slot.Bounds.Height + slot.TopPadding + slot.BottomPadding);
                var union = pending.Count == 0 ? captureBounds : Rectangle.Union(bounds, captureBounds);
                var area = (long)captureBounds.Width * captureBounds.Height;
                if (pending.Count > 0 && (long)union.Width * union.Height > (totalArea + area) * 4)
                {
                    batchesForDisplay.Add(new CaptureBatch(group.Key, bounds, pending.ToArray()));
                    pending.Clear();
                    bounds = captureBounds;
                    totalArea = 0;
                }
                else
                {
                    bounds = union;
                }

                pending.Add(slot);
                totalArea += area;
            }

            if (pending.Count > 0)
            {
                batchesForDisplay.Add(new CaptureBatch(group.Key, bounds, pending.ToArray()));
            }

            return batchesForDisplay;
        }).ToArray();
        lock (_captureSync)
        {
            foreach (var kind in AbilityKinds)
            {
                if (regionErrors.TryGetValue(kind, out var error))
                {
                    _captureErrors[kind] = error;
                    _trackers[kind].MarkSignalLost(DateTime.UtcNow);
                }
                else _captureErrors.Remove(kind);
            }
            var unchanged = _captureBatches.Length == batches.Length &&
                _captureBatches.Zip(batches).All(pair =>
                    pair.First.Bounds == pair.Second.Bounds &&
                    pair.First.DeviceName == pair.Second.DeviceName &&
                    pair.First.Slots.SequenceEqual(pair.Second.Slots));
            if (unchanged)
            {
                return;
            }

            foreach (var batch in _captureBatches)
            {
                if (batch.Surface is not null)
                {
                    _retiredCaptureBatches.Enqueue(batch);
                }
            }

            _captureBatches = batches;
        }
    }

    private void DisposeRetiredCaptureBatches()
    {
        lock (_captureSync)
        {
            while (_retiredCaptureBatches.TryDequeue(out var batch))
            {
                batch.Dispose();
            }
        }
    }

    private Rectangle ResolveCaptureRegion(AbilitySlotSettings slot, Rectangle savedRegion)
    {
        if (slot.GameWindowBounds is { Width: > 0, Height: > 0 } savedWindow)
        {
            if (_currentGameWindow is not { } gameWindow)
            {
                throw new InvalidOperationException(GameWindowUnavailableError);
            }

            return MapRegion(savedRegion, savedWindow, gameWindow.Bounds);
        }

        if (slot.MonitorBounds is not { Width: > 0, Height: > 0 } sourceBounds)
        {
            var monitor = Screen.FromRectangle(savedRegion);
            slot.MonitorBounds = monitor.Bounds;
            slot.MonitorDeviceName = monitor.DeviceName;
            SaveSettings();
            return savedRegion;
        }

        var targetMonitor = Screen.AllScreens.FirstOrDefault(
            screen => screen.DeviceName.Equals(slot.MonitorDeviceName, StringComparison.OrdinalIgnoreCase))
            ?? Screen.PrimaryScreen
            ?? throw new InvalidOperationException("Windows did not report an available display.");
        var target = targetMonitor.Bounds;
        if (sourceBounds == target)
        {
            return savedRegion;
        }

        return MapRegion(savedRegion, sourceBounds, target);
    }

    internal static Rectangle MapRegion(Rectangle savedRegion, Rectangle sourceBounds, Rectangle target)
    {
        var left = target.Left + (int)Math.Round((savedRegion.Left - sourceBounds.Left) * (double)target.Width / sourceBounds.Width);
        var top = target.Top + (int)Math.Round((savedRegion.Top - sourceBounds.Top) * (double)target.Height / sourceBounds.Height);
        var right = target.Left + (int)Math.Round((savedRegion.Right - sourceBounds.Left) * (double)target.Width / sourceBounds.Width);
        var bottom = target.Top + (int)Math.Round((savedRegion.Bottom - sourceBounds.Top) * (double)target.Height / sourceBounds.Height);
        var mapped = Rectangle.FromLTRB(left, top, right, bottom);
        if (mapped.Width <= 0 || mapped.Height <= 0 || Rectangle.Intersect(mapped, target) != mapped)
        {
            throw new InvalidOperationException("The selected area no longer maps inside this display. Select it again at the current resolution.");
        }

        return mapped;
    }

    private TrackerSettings LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var settings = JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(_settingsPath)) ?? new TrackerSettings();
                settings.MigrateAbilityUseBinding();
                return settings;
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Couldn't load saved settings: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return new TrackerSettings();
    }

    private void InitializeChangelogVersion()
    {
        Version? installedFromVersion = null;
        var sourceMarkerExists = File.Exists(_updateSourceVersionPath);
        if (sourceMarkerExists)
        {
            try
            {
                if (UpdateChecker.TryParseTag(File.ReadAllText(_updateSourceVersionPath).Trim(), out var parsed))
                {
                    installedFromVersion = parsed;
                }
                else
                {
                    MessageBox.Show(this, "The update's source-version marker was invalid; changelog history will start from this version.",
                        Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Couldn't read the update's source-version marker: {exception.Message}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        var shouldSave = false;
        if (!UpdateChecker.TryParseTag(_settings.LastChangelogVersion ?? string.Empty, out _lastChangelogVersion))
        {
            _lastChangelogVersion = installedFromVersion ?? UpdateChecker.CurrentVersion;
            _settings.LastChangelogVersion = _lastChangelogVersion.ToString(3);
            shouldSave = true;
        }
        else if (_lastChangelogVersion > UpdateChecker.CurrentVersion)
        {
            _lastChangelogVersion = UpdateChecker.CurrentVersion;
            _settings.LastChangelogVersion = _lastChangelogVersion.ToString(3);
            shouldSave = true;
        }

        _startupChangelogPending = UpdateChecker.IsReleaseBuild && _lastChangelogVersion < UpdateChecker.CurrentVersion;
        var saved = !shouldSave || SaveSettings();
        if (sourceMarkerExists && saved)
        {
            try
            {
                File.Delete(_updateSourceVersionPath);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, $"Couldn't remove the update's source-version marker: {exception.Message}",
                    Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    private void EnsureSlotSettings()
    {
        _settings.Slots ??= [];
        _settings.SoloBindings ??= [];
        // Shortcuts are keyboard-only; drop mouse shortcuts saved by earlier versions.
        static InputBinding? KeyboardOnly(InputBinding? binding) => binding is { Device: InputDevice.Mouse } ? null : binding;
        _settings.RepeatTestBinding = KeyboardOnly(_settings.RepeatTestBinding);
        _settings.OverlayToggleBinding = KeyboardOnly(_settings.OverlayToggleBinding);
        _settings.SoloBindings.Clear();

        if (_settings.PauseBinding is { Device: InputDevice.Mouse })
        {
            _settings.PauseBinding = InputBinding.DefaultPause;
        }

        if (_settings.PauseBinding is null && !_settings.PauseShortcutCleared)
        {
            var legacyKey = Enum.TryParse<Keys>(_settings.Hotkey, true, out var parsedKey)
                ? parsedKey
                : Keys.F8;
            _settings.PauseBinding = new InputBinding(InputDevice.Keyboard, (int)legacyKey, InputModifiers.None);
        }

        foreach (var kind in Enum.GetValues<AbilityKind>())
        {
            if (_settings.Slots.All(slot => slot.Kind != kind))
            {
                _settings.Slots.Add(new AbilitySlotSettings { Kind = kind });
            }

            _trackers[kind] = new AbilitySlotTracker(kind);
        }

        if (_settings.Region is { } legacyRegion &&
            _settings.Slots.First(slot => slot.Kind == AbilityKind.Grenade).Region is null)
        {
            var grenade = _settings.Slots.First(slot => slot.Kind == AbilityKind.Grenade);
            grenade.Region = legacyRegion;
            grenade.MonitorBounds = _settings.RegionMonitorBounds;
            grenade.MonitorDeviceName = _settings.RegionMonitorDeviceName;
            SaveSettings();
        }
    }

    private AbilitySlotSettings GetSlot(AbilityKind kind) => _settings.Slots.First(slot => slot.Kind == kind);

    private bool SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Couldn't save settings: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
    }

    private static string KindName(AbilityKind kind) => kind switch
    {
        AbilityKind.Grenade => "Grenade",
        AbilityKind.Melee => "Melee",
        AbilityKind.ClassAbility => "Class ability",
        _ => kind.ToString()
    };

    private static string KindShortName(AbilityKind kind) => kind switch
    {
        AbilityKind.Grenade => "Grenade",
        AbilityKind.Melee => "Melee",
        AbilityKind.ClassAbility => "Class",
        _ => kind.ToString()
    };

    private static string RegressionSuffix(AbilitySlotTracker tracker) =>
        tracker.LastRegressionDuration is { } fitted && tracker.LastRegressionUncertainty is { } spread
            ? $"regression {fitted.TotalSeconds:F2} ± {spread.TotalSeconds:F2} s"
            : string.Empty;

    private static string MeasurementSuffix(AbilitySlotTracker tracker) =>
        (tracker.LastUncertainty, tracker.LastSource) switch
        {
            ({ } spread, MeasurementSource.Event) => $" ± {spread.TotalSeconds:F3} s",
            ({ } spread, MeasurementSource.EventRefinedByModel) => $" ± {spread.TotalSeconds:F3} s (refined)",
            ({ } spread, MeasurementSource.Model) => $" ± {spread.TotalSeconds:F2} s (endpoint inferred)",
            ({ } spread, MeasurementSource.Estimated) => $" ± {spread.TotalSeconds:F2} s (estimated: tracking began mid-recharge)",
            _ => string.Empty
        };

    private static string FormatDuration(TimeSpan? duration) =>
        DurationDisplay.Format(duration);

    private sealed record DisplayChoice(string DeviceName, Rectangle Bounds, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record AbilityChoice(AbilityKind Kind, string Label)
    {
        public override string ToString() => Label;
    }

    private enum BindingCaptureTarget
    {
        MarkCharged,
        Pause,
        AbilityUse,
        RepeatTest,
        ToggleOverlay
    }

    private sealed record CaptureSlot(AbilityKind Kind, Rectangle Bounds, int BottomPadding, int TopPadding);

    private sealed class CaptureBatch(string deviceName, Rectangle bounds, CaptureSlot[] slots) : IDisposable
    {
        public string DeviceName { get; } = deviceName;
        public Rectangle Bounds { get; } = bounds;
        public CaptureSlot[] Slots { get; } = slots;
        public ScreenCaptureSurface? Surface { get; set; }

        public void Dispose()
        {
            Surface?.Dispose();
            Surface = null;
        }
    }
}

using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Windows.Forms;

namespace D2AbilityTracker;

internal sealed class MainForm : Form
{
    private static readonly AbilityKind[] AbilityKinds = Enum.GetValues<AbilityKind>();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D2AbilityTracker",
        "settings.json");
    private readonly TrackerSettings _settings;
    private readonly OverlayWindow _overlay = new();
    private readonly CancellationTokenSource _captureCancellation = new();
    private readonly object _captureSync = new();
    private readonly GlobalInputListener _inputListener = new();
    private readonly Thread _captureThread;
    private readonly Label _stateStatus = new();
    private readonly Button _toggleButton = new();
    private readonly Button _pauseBindingButton = new();
    private readonly Button _abilityUseBindingButton = new();
    private readonly Button _startTestButton = new();
    private readonly Button _stopTestButton = new();
    private readonly Button _repeatTestBindingButton = new();
    private readonly Button _recordHudButton = new();
    private readonly Button _overlayToggleButton = new();
    private readonly Button _overlayBindingButton = new();
    private readonly Button _updateButton = new();
    private readonly NumericUpDown _captureRatePicker = new();
    private readonly ComboBox _anchorPicker = new();
    private readonly ComboBox _monitorPicker = new();
    private readonly ComboBox _testAbilityPicker = new();
    private readonly NumericUpDown _testCyclesPicker = new();
    private readonly NumericUpDown _testDelayPicker = new();
    private readonly Label _testStatus = new();
    private readonly Label _hudRecordingStatus = new();
    private readonly RepeatTestSession _repeatTest = new();
    private readonly Dictionary<AbilityKind, AbilitySlotTracker> _trackers = [];
    private readonly Dictionary<AbilityKind, Label> _regionLabels = [];
    private readonly Dictionary<AbilityKind, Label> _statusLabels = [];
    private readonly Dictionary<AbilityKind, Button> _selectButtons = [];
    private readonly Dictionary<AbilityKind, Button> _soloBindingButtons = [];
    private readonly Dictionary<AbilityKind, CheckBox> _soloToggles = [];
    private readonly ToolTip _toolTip = new();
    private readonly Dictionary<AbilityKind, string> _captureErrors = [];
    private readonly Queue<CaptureBatch> _retiredCaptureBatches = [];
    private CaptureBatch[] _captureBatches = [];
    private volatile bool _monitoring;
    private volatile bool _refreshCaptureRegionsRequested;
    private BindingCaptureTarget? _bindingCaptureTarget;
    private bool _inputListenerAvailable;
    private double _captureRate;
    private string? _captureLoopError;
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

    public MainForm()
    {
        _settings = LoadSettings();
        EnsureSlotSettings();
        Text = $"D2 Ability Recharge Tracker v{UpdateChecker.CurrentVersion.ToString(3)}";
        _targetCaptureRate = Math.Clamp(_settings.CaptureRate, 30, 240);
        ClientSize = new Size(790, 735);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);
        BuildControls();
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
            if (UpdateChecker.IsReleaseBuild && _settings.CheckForUpdatesOnStartup)
            {
                await CheckForUpdateAsync(interactive: false);
            }
        };
        FormClosing += async (_, args) =>
        {
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

    private void BuildControls()
    {
        var title = new Label
        {
            Text = "Ability recharge tracker",
            Font = new Font(Font, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16)
        };
        var intro = new Label
        {
            Text = "Starts timing on the brief bottom-border flash; ability icons and element colours are not matched.",
            AutoSize = true,
            Location = new Point(20, 44)
        };
        var monitorLabel = new Label { Text = "Display", AutoSize = true, Location = new Point(20, 79) };
        _monitorPicker.SetBounds(78, 74, 350, 28);
        _monitorPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        RefreshDisplays();
        var captureLabel = new Label { Text = "Capture target (Hz)", AutoSize = true, Location = new Point(460, 79) };
        _captureRatePicker.SetBounds(590, 74, 80, 28);
        _captureRatePicker.Minimum = 30;
        _captureRatePicker.Maximum = 240;
        _captureRatePicker.Value = _targetCaptureRate;
        _captureRatePicker.ValueChanged += (_, _) =>
        {
            Volatile.Write(ref _targetCaptureRate, decimal.ToInt32(_captureRatePicker.Value));
            _settings.CaptureRate = _targetCaptureRate;
            SaveSettings();
        };

        var help = new Label
        {
            Text = "Select the full square border tightly; pixels below it are captured automatically.",
            AutoEllipsis = true,
            Location = new Point(20, 112),
            Size = new Size(440, 22)
        };
        var selectionGuideButton = new Button
        {
            Text = "Selection guide",
            Location = new Point(465, 106),
            Size = new Size(100, 30)
        };
        selectionGuideButton.Click += (_, _) => ShowSelectionGuide();
        var resetSelectionsButton = new Button
        {
            Text = "Reset areas",
            Location = new Point(570, 106),
            Size = new Size(90, 30)
        };
        resetSelectionsButton.Click += (_, _) => ResetSelections();
        _recordHudButton.Text = "Record HUD";
        _recordHudButton.SetBounds(665, 106, 105, 30);
        _recordHudButton.Click += (_, _) => ToggleHudRecording();

        var divider = new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Width = 650, Location = new Point(20, 137) };
        foreach (var kind in AbilityKinds)
        {
            var y = 151 + (int)kind * 72;
            var name = new Label
            {
                Text = KindName(kind),
                Font = new Font(Font, FontStyle.Bold),
                AutoSize = true,
                Location = new Point(20, y + 8)
            };
            var selectButton = new Button
            {
                Text = "Select area…",
                Location = new Point(115, y + 2),
                Size = new Size(120, 32)
            };
            selectButton.Click += (_, _) => SelectRegion(kind);
            var soloToggle = new CheckBox
            {
                Text = "Solo",
                Appearance = Appearance.Button,
                TextAlign = ContentAlignment.MiddleCenter,
                Location = new Point(630, y + 2),
                Size = new Size(50, 32),
                Checked = _settings.SoloAbility == kind
            };
            _toolTip.SetToolTip(soloToggle, "Track only this ability (captures one slot for best performance). Click again to track all.");
            soloToggle.Click += (_, _) => ToggleSolo(kind);
            _soloToggles.Add(kind, soloToggle);
            var soloBindingButton = new Button
            {
                Text = _settings.SoloBindings.TryGetValue(kind, out var soloBinding) ? soloBinding.ToString() : "Solo shortcut…",
                Location = new Point(684, y + 2),
                Size = new Size(86, 32),
                AutoEllipsis = true
            };
            _toolTip.SetToolTip(soloBindingButton, "Keyboard shortcut that toggles solo tracking of this ability. Press Esc while recording to clear it.");
            soloBindingButton.Click += (_, _) => ToggleBindingCapture(SoloCaptureTarget(kind));
            _soloBindingButtons.Add(kind, soloBindingButton);
            var regionLabel = new Label
            {
                AutoEllipsis = true,
                Location = new Point(250, y + 1),
                Size = new Size(370, 22)
            };
            var statusLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = Color.DimGray,
                Location = new Point(250, y + 23),
                Size = new Size(370, 22)
            };
            _selectButtons.Add(kind, selectButton);
            _regionLabels.Add(kind, regionLabel);
            _statusLabels.Add(kind, statusLabel);
            Controls.AddRange([name, selectButton, regionLabel, statusLabel, soloToggle, soloBindingButton]);
        }

        var testGroup = new GroupBox
        {
            Text = "Repeatability test",
            Location = new Point(20, 360),
            Size = new Size(750, 215)
        };
        var testAbilityLabel = new Label { Text = "Ability", AutoSize = true, Location = new Point(15, 30) };
        _testAbilityPicker.SetBounds(75, 25, 150, 28);
        _testAbilityPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        var abilityChoices = AbilityKinds.Select(kind => new AbilityChoice(kind, KindName(kind) == "Class" ? "Class ability" : KindName(kind))).ToArray();
        _testAbilityPicker.Items.AddRange(abilityChoices);
        _testAbilityPicker.SelectedItem = abilityChoices.FirstOrDefault(choice => choice.Kind == _settings.TestAbility);
        _testAbilityPicker.SelectedIndexChanged += (_, _) =>
        {
            if (_testAbilityPicker.SelectedItem is AbilityChoice choice)
            {
                _settings.TestAbility = choice.Kind;
                SaveSettings();
                UpdateStatus();
            }
        };

        var cyclesLabel = new Label { Text = "Recharge cycles", AutoSize = true, Location = new Point(245, 30) };
        _testCyclesPicker.SetBounds(345, 25, 65, 28);
        _testCyclesPicker.Minimum = 1;
        _testCyclesPicker.Maximum = 100;
        _testCyclesPicker.Value = Math.Clamp(_settings.TestCycles, 1, 100);
        _testCyclesPicker.ValueChanged += (_, _) =>
        {
            _settings.TestCycles = decimal.ToInt32(_testCyclesPicker.Value);
            SaveSettings();
        };

        var delayLabel = new Label { Text = "Delay before reuse", AutoSize = true, Location = new Point(430, 30) };
        _testDelayPicker.SetBounds(535, 25, 70, 28);
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
        var secondsLabel = new Label { Text = "sec", AutoSize = true, Location = new Point(610, 30) };

        var abilityInputLabel = new Label { Text = "Ability use input", AutoSize = true, Location = new Point(15, 75) };
        _abilityUseBindingButton.SetBounds(120, 69, 150, 30);
        _abilityUseBindingButton.Text = _settings.AbilityUseBinding?.ToString() ?? "Record input…";
        _abilityUseBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.AbilityUse);
        _startTestButton.SetBounds(290, 68, 115, 32);
        _startTestButton.Text = "Start test";
        _startTestButton.Click += (_, _) => StartRepeatabilityTest();
        _stopTestButton.SetBounds(415, 68, 100, 32);
        _stopTestButton.Text = "Stop test";
        _stopTestButton.Click += (_, _) => StopRepeatabilityTest();
        var repeatShortcutLabel = new Label { Text = "Start / stop shortcut", AutoSize = true, Location = new Point(15, 117) };
        _repeatTestBindingButton.SetBounds(150, 110, 180, 30);
        _repeatTestBindingButton.Text = _settings.RepeatTestBinding?.ToString() ?? "Record shortcut…";
        _repeatTestBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.RepeatTest);
        _testStatus.SetBounds(15, 152, 715, 48);
        _testStatus.AutoEllipsis = true;
        _testStatus.ForeColor = Color.DimGray;
        _hudRecordingStatus.SetBounds(345, 112, 385, 30);
        _hudRecordingStatus.AutoEllipsis = true;
        _hudRecordingStatus.ForeColor = Color.DimGray;
        _hudRecordingStatus.Cursor = Cursors.Hand;
        _hudRecordingStatus.Text = "HUD recordings are saved to Documents\\D2AbilityTracker (click to open)";
        _hudRecordingStatus.Click += (_, _) => OpenHudRecordingsFolder();
        _toolTip.SetToolTip(_hudRecordingStatus, AbilityHudRecorder.DefaultFolder);
        testGroup.Controls.AddRange([
            testAbilityLabel, _testAbilityPicker, cyclesLabel, _testCyclesPicker,
            delayLabel, _testDelayPicker, secondsLabel, abilityInputLabel,
            _abilityUseBindingButton, _startTestButton, _stopTestButton, repeatShortcutLabel, _repeatTestBindingButton,
            _hudRecordingStatus, _testStatus
        ]);

        _toggleButton.SetBounds(20, 603, 185, 36);
        _toggleButton.Click += (_, _) => SetMonitoring(!_monitoring);
        var hotkeyLabel = new Label { Text = "Pause / resume", AutoSize = true, Location = new Point(224, 613) };
        _pauseBindingButton.SetBounds(335, 607, 140, 30);
        _pauseBindingButton.Text = PauseBinding?.ToString() ?? "Record input…";
        _pauseBindingButton.Click += (_, _) => TogglePauseBindingCapture();

        var anchorLabel = new Label { Text = "Overlay position", AutoSize = true, Location = new Point(495, 613) };
        _anchorPicker.SetBounds(610, 607, 150, 28);
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

        _overlayToggleButton.SetBounds(20, 648, 185, 34);
        _overlayToggleButton.Click += (_, _) => SetOverlayVisible(!_settings.OverlayVisible);
        var overlayHotkeyLabel = new Label { Text = "Show / hide overlay", AutoSize = true, Location = new Point(224, 657) };
        _overlayBindingButton.SetBounds(335, 651, 140, 30);
        _overlayBindingButton.Text = OverlayToggleBinding?.ToString() ?? "Record input…";
        _overlayBindingButton.Click += (_, _) => ToggleBindingCapture(BindingCaptureTarget.ToggleOverlay);
        _updateButton.SetBounds(610, 651, 150, 30);
        _updateButton.Text = "Check for updates";
        _updateButton.Click += (_, _) => CheckOrInstallUpdate();
        UpdateOverlayToggleText();

        _stateStatus.SetBounds(20, 697, 750, 24);
        _stateStatus.ForeColor = Color.FromArgb(90, 90, 90);
        Controls.AddRange([title, intro, monitorLabel, _monitorPicker, captureLabel, _captureRatePicker, help, selectionGuideButton, resetSelectionsButton,
            _recordHudButton, divider, testGroup, _toggleButton,
            hotkeyLabel, _pauseBindingButton, anchorLabel, _anchorPicker, _overlayToggleButton, overlayHotkeyLabel,
            _overlayBindingButton, _updateButton, _stateStatus]);
    }

    private async void ToggleHudRecording()
    {
        if (_hudRecorder is { } activeRecording)
        {
            activeRecording.Stop();
            _recordHudButton.Enabled = false;
            _recordHudButton.Text = "Finishing…";
            _hudRecordingStatus.Text = "Finishing the recording and writing its timestamp index…";
            return;
        }

        AbilityHudRecorder recorder;
        try
        {
            Rectangle bounds;
            lock (_captureSync)
            {
                var areas = AbilityKinds
                    .Where(IsTracked)
                    .Select(GetSlot)
                    .Where(slot => slot.Region is not null)
                    .Select(slot =>
                    {
                        var region = ResolveCaptureRegion(slot, slot.Region!.Value);
                        return (region, Screen.FromRectangle(region).Bounds);
                    })
                    .ToArray();
                bounds = AbilityHudRecorder.GetCaptureBounds(areas);
            }

            Task<AbilityHudRecordingResult>? recordingTask = null;
            recorder = new AbilityHudRecorder(bounds, Screen.FromRectangle(bounds).DeviceName,
                AbilityHudRecorder.DefaultPath(DateTime.Now),
                progress => QueueHudRecordingProgress(progress, recordingTask));
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
        _overlay.Hide();
        try
        {
            var result = await recorder.Completion;
            if (!_closingAfterHudRecording)
            {
                var dropped = result.DroppedFrames > 0 ? $", {result.DroppedFrames} dropped" : string.Empty;
                _hudRecordingStatus.Text =
                    $"{result.StopReason ?? "Saved"}: {result.Frames} frames{dropped}, {result.Bytes / 1_000_000d:F1} MB → {Path.GetFileName(result.Path)}";
                _toolTip.SetToolTip(_hudRecordingStatus, $"{result.Path}\nClick to open the recordings folder.");
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
                ShowOverlayIfEnabled();
                UpdateOverlay();
            }
        }
    }

    private void OpenHudRecordingsFolder()
    {
        try
        {
            Directory.CreateDirectory(AbilityHudRecorder.DefaultFolder);
            Process.Start(new ProcessStartInfo { FileName = AbilityHudRecorder.DefaultFolder, UseShellExecute = true });
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            MessageBox.Show(this, $"Couldn't open {AbilityHudRecorder.DefaultFolder}: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
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
        if (recorder is null || !recorder.IsDue(tickTimestamp))
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

            if (!duplication.TryCapture(recorder.DeviceName, bounds, surface, out var timestamp))
            {
                surface.Capture(desktopDeviceContext, bounds);
                timestamp = DateTime.UtcNow;
            }

            recorder.Submit(surface.Pixels, surface.Stride, timestamp, RecordingTrackerState());
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
            if (_monitoring)
            {
                RefreshCaptureRegions();
            }
        }

        SaveSettings();
        UpdateStatus();
        UpdateOverlay();
    }

    private void RefreshDisplays()
    {
        var displays = Screen.AllScreens.Select((screen, index) => new DisplayChoice(
            screen.DeviceName,
            screen.Bounds,
            $"Display {index + 1}  ·  {screen.Bounds.Width} × {screen.Bounds.Height}  ·  {screen.DeviceName}")).ToArray();
        _monitorPicker.Items.Clear();
        _monitorPicker.Items.AddRange(displays);
        _monitorPicker.SelectedIndex = Math.Clamp(_monitorPicker.SelectedIndex, 0, displays.Length - 1);
    }

    private void SelectRegion(AbilityKind kind)
    {
        if (_selectingRegion)
        {
            return;
        }

        RefreshDisplays();
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
                _monitorPicker.SelectedItem = _monitorPicker.Items.Cast<DisplayChoice>()
                    .FirstOrDefault(choice => choice.DeviceName.Equals(display.DeviceName, StringComparison.OrdinalIgnoreCase));
            }
        }
        else
        {
            var result = MessageBox.Show(
                this,
                "Destiny 2's window wasn't found. Show the selected display's frozen preview instead?",
                Text,
                MessageBoxButtons.YesNo,
                MessageBoxIcon.Information);
            if (result != DialogResult.Yes || _monitorPicker.SelectedItem is not DisplayChoice fallbackDisplay)
            {
                return;
            }

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
                if (suggestions.Length == AbilityKinds.Length)
                {
                    for (var index = 0; index < AbilityKinds.Length; index++)
                    {
                        var suggestedSlot = GetSlot(AbilityKinds[index]);
                        if (suggestedSlot.Region is null)
                        {
                            suggestedSlot.Region = suggestions[index];
                            suggestedSlot.MonitorBounds = display.Bounds;
                            suggestedSlot.MonitorDeviceName = display.DeviceName;
                            suggestedSlot.GameWindowBounds = target?.Bounds;
                            suggestedSlot.RegionAutoDetected = true;
                        }
                    }
                }

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
            Activate();
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
        try
        {
            using var desktop = ScreenCapture.OpenDesktop();
            using var duplication = new DesktopDuplicationCapture();
            using var wait = new PrecisionWait();
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

                var captureStarted = Stopwatch.GetTimestamp();
                if (CaptureFrame(desktop.DeviceContext, duplication))
                {
                    capturedFrames++;
                }

                now = Stopwatch.GetTimestamp();
                // Never catch up missed captures in bursts. Limit capture work to 25% duty under load.
                nextFrame = CaptureSchedule.NextFrame(captureStarted, now, Volatile.Read(ref _targetCaptureRate), Stopwatch.Frequency);

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

    private bool CaptureFrame(IntPtr desktopDeviceContext, DesktopDuplicationCapture duplication)
    {
        var capturedAny = false;
        var duplicatedAll = true;
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

                    if (!duplication.TryCapture(batch.DeviceName, bounds, surface, out timestamp))
                    {
                        duplicatedAll = false;
                        surface.Capture(desktopDeviceContext, bounds);
                        timestamp = DateTime.UtcNow;
                    }

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
                            _repeatTest.RecordRecharge(slot.Kind, duration, timestamp);
                        }
                        if (tracker.MeasurementDiscarded && _repeatTest.Ability == slot.Kind && _repeatTest.IsActive)
                        {
                            _repeatTest.Fail(tracker.TrackingWarning!);
                        }

                        _captureErrors.Remove(slot.Kind);
                    }
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

            CaptureHudRecordingFrame(desktopDeviceContext, duplication, timestamp);

            if (testAbilityCaptured &&
                _repeatTest.IsUseDue(timestamp) &&
                _repeatTest.Ability is { } testAbility &&
                !_captureErrors.ContainsKey(testAbility) &&
                _trackers[testAbility].IsCharged)
            {
                if (_repeatTest.TryGetDueUse(timestamp, InputBindingSender.IsDestiny2Foreground(), out var binding))
                {
                    try
                    {
                        InputBindingSender.PressAndRelease(binding);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or ArgumentOutOfRangeException)
                    {
                        _repeatTest.Fail(exception.Message);
                    }
                }
            }
        }

        if (capturedAny)
        {
            _captureBackend = duplicatedAll ? "DXGI" : "GDI";
        }

        return capturedAny;
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
                        lock (_captureSync)
                        {
                            if (_monitoring)
                            {
                                RefreshCaptureRegions();
                            }
                        }
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
        lock (_captureSync)
        {
            foreach (var kind in AbilityKinds)
            {
                var slot = GetSlot(kind);
                var tracker = _trackers[kind];
                _regionLabels[kind].Text = slot.Region is { } region
                    ? $"Area selected: {region.Width} × {region.Height}"
                    : "No area selected";
                if (_captureErrors.TryGetValue(kind, out var captureError))
                {
                    _statusLabels[kind].Text = $"Capture failed: {captureError}";
                    _statusLabels[kind].ForeColor = Color.Firebrick;
                    continue;
                }

                _soloToggles[kind].Checked = _settings.SoloAbility == kind;
                if (!IsTracked(kind))
                {
                    _statusLabels[kind].Text = $"Not tracked · solo: {KindName(_settings.SoloAbility!.Value)}";
                    _statusLabels[kind].ForeColor = Color.DimGray;
                    continue;
                }

                if (!_monitoring)
                {
                    _statusLabels[kind].Text = "Paused";
                    _statusLabels[kind].ForeColor = Color.DimGray;
                    continue;
                }

                _statusLabels[kind].Text = tracker.State switch
                {
                    AbilityState.Ready => $"Ready{DurationSuffix(tracker.LastDuration)}",
                    AbilityState.Consumed => "Used · waiting for the bottom-border flash",
                    AbilityState.Recharging => $"Recharging · {FormatDuration(tracker.CurrentDuration)}",
                    AbilityState.Complete => $"Recharge complete · {FormatDuration(tracker.LastDuration)}{MeasurementSuffix(tracker)}",
                    _ => slot.Region is null
                        ? "Select this ability slot to track it"
                        : slot.RegionAutoDetected ? "Auto-detected · click to refine" : "Watching slot background and bottom border…"
                };
                _statusLabels[kind].ForeColor = tracker.State switch
                {
                    AbilityState.Consumed => Color.DarkOrange,
                    AbilityState.Recharging => Color.RoyalBlue,
                    AbilityState.Complete => Color.SeaGreen,
                    _ => Color.DimGray
                };
                if (tracker.TrackingWarning is { } warning)
                {
                    _statusLabels[kind].Text = $"{FormatDuration(tracker.CurrentDuration)} · {warning}";
                    _statusLabels[kind].ForeColor = Color.DarkOrange;
                }
                else if (tracker.State == AbilityState.Recharging && tracker.DisplayProgress is { } progress)
                {
                    _statusLabels[kind].Text += $" · {progress:P0}";
                    if (tracker.EstimatedDuration is { } estimated && tracker.EstimateUncertainty is { } spread)
                    {
                        _statusLabels[kind].Text += $" · est. {estimated.TotalSeconds:F2} ± {spread.TotalSeconds:F2} s";
                    }
                }
            }

            _toggleButton.Text = _monitoring ? "Pause detection" : "Resume detection";
            _stateStatus.Text = _captureLoopError is { } captureLoopError
                ? $"Capture stopped: {captureLoopError}"
                : _inputListenerError is { } inputListenerError
                ? $"Input shortcuts unavailable: {inputListenerError}"
                : _monitoring
                ? $"Starts on the bottom-border flash · {_captureBackend} capture {_captureRate:F0}/{_targetCaptureRate} Hz (load-limited)"
                : PauseBinding is { } pause ? $"Paused · {pause} to resume" : "Paused";
            _testStatus.Text = _repeatTest.Status(DateTime.UtcNow);
            _testAbilityPicker.Enabled = !_repeatTest.IsActive;
            _testCyclesPicker.Enabled = !_repeatTest.IsActive;
            _testDelayPicker.Enabled = !_repeatTest.IsActive;
            _abilityUseBindingButton.Enabled = !_repeatTest.IsActive;
            _repeatTestBindingButton.Enabled = !_repeatTest.IsActive;
            _startTestButton.Enabled = !_repeatTest.IsActive &&
                _inputListenerAvailable &&
                _settings.AbilityUseBinding is not null &&
                    GetSlot(_settings.TestAbility).Region is not null &&
                    _bindingCaptureTarget is null;
            _stopTestButton.Enabled = _repeatTest.IsActive;
        }
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
        _overlayToggleButton.Text = _settings.OverlayVisible ? "Hide overlay" : "Show overlay";
    }

    private async void CheckOrInstallUpdate()
    {
        if (_updateBusy)
        {
            return;
        }

        if (_availableUpdate is null)
        {
            await CheckForUpdateAsync(interactive: true);
        }

        if (_availableUpdate is { } release)
        {
            await InstallUpdateAsync(release);
        }
    }

    private async Task CheckForUpdateAsync(bool interactive)
    {
        _updateBusy = true;
        _updateButton.Enabled = false;
        _updateButton.Text = "Checking…";
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            _availableUpdate = await UpdateChecker.CheckAsync(timeout.Token);
            if (_availableUpdate is null && interactive)
            {
                MessageBox.Show(this, $"You're on the latest version (v{UpdateChecker.CurrentVersion.ToString(3)}).", Text,
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
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
                _updateButton.Enabled = true;
                _updateButton.Text = _availableUpdate is { } release ? $"Update to {release.Tag}…" : "Check for updates";
                _updateButton.Font = _availableUpdate is null ? Font : new Font(Font, FontStyle.Bold);
            }
        }
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
        _updateButton.Enabled = false;
        _updateButton.Text = "Downloading…";
        try
        {
            var progress = new Progress<double>(fraction => _updateButton.Text = $"Downloading… {fraction:P0}");
            var installer = await UpdateChecker.DownloadAsync(release, progress, CancellationToken.None);
            _updateButton.Text = "Installing…";
            UpdateChecker.LaunchInstaller(installer);
            Close();
        }
        catch (Exception exception)
        {
            _updateBusy = false;
            _updateButton.Enabled = true;
            _updateButton.Text = $"Update to {release.Tag}…";
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
        GetBindingButton(target).Text = allowMouse ? "Press a key/button… (Esc clears)" : "Press a key… (Esc clears)";
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
                    else if (target == BindingCaptureTarget.AbilityUse)
                    {
                        _settings.AbilityUseBinding = binding;
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
                    else
                    {
                        _settings.SoloBindings[SoloAbility(target)] = binding;
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
            case BindingCaptureTarget.Pause:
                _settings.PauseBinding = null;
                _settings.PauseShortcutCleared = true;
                break;
            case BindingCaptureTarget.AbilityUse:
                _settings.AbilityUseBinding = null;
                break;
            case BindingCaptureTarget.RepeatTest:
                _settings.RepeatTestBinding = null;
                break;
            case BindingCaptureTarget.ToggleOverlay:
                _settings.OverlayToggleBinding = null;
                _settings.OverlayShortcutCleared = true;
                break;
            default:
                _settings.SoloBindings.Remove(SoloAbility(target));
                break;
        }
    }

    private Button GetBindingButton(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.Pause => _pauseBindingButton,
        BindingCaptureTarget.AbilityUse => _abilityUseBindingButton,
        BindingCaptureTarget.RepeatTest => _repeatTestBindingButton,
        BindingCaptureTarget.ToggleOverlay => _overlayBindingButton,
        BindingCaptureTarget.SoloGrenade or BindingCaptureTarget.SoloMelee or BindingCaptureTarget.SoloClass =>
            _soloBindingButtons[SoloAbility(target)],
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void UpdateBindingButtonText(BindingCaptureTarget target)
    {
        GetBindingButton(target).Text = GetConfiguredBinding(target)?.ToString() ?? (target is BindingCaptureTarget.SoloGrenade or BindingCaptureTarget.SoloMelee or BindingCaptureTarget.SoloClass ? "Solo shortcut…" : "Record input…");
    }

    private InputBinding? GetConfiguredBinding(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.Pause => PauseBinding,
        BindingCaptureTarget.AbilityUse => _settings.AbilityUseBinding,
        BindingCaptureTarget.RepeatTest => _settings.RepeatTestBinding,
        BindingCaptureTarget.ToggleOverlay => OverlayToggleBinding,
        _ => _settings.SoloBindings.TryGetValue(SoloAbility(target), out var binding) ? binding : null
    };

    private static BindingCaptureTarget SoloCaptureTarget(AbilityKind kind) => kind switch
    {
        AbilityKind.Grenade => BindingCaptureTarget.SoloGrenade,
        AbilityKind.Melee => BindingCaptureTarget.SoloMelee,
        AbilityKind.ClassAbility => BindingCaptureTarget.SoloClass,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static AbilityKind SoloAbility(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.SoloGrenade => AbilityKind.Grenade,
        BindingCaptureTarget.SoloMelee => AbilityKind.Melee,
        BindingCaptureTarget.SoloClass => AbilityKind.ClassAbility,
        _ => throw new ArgumentOutOfRangeException(nameof(target))
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
        if (_settings.AbilityUseBinding is not { } binding)
        {
            MessageBox.Show(this, "Record the ability-use key or mouse button first.", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        if (!_monitoring)
        {
            SetMonitoring(true);
        }

        lock (_captureSync)
        {
            _repeatTest.Start(
                ability,
                decimal.ToInt32(_testCyclesPicker.Value),
                TimeSpan.FromSeconds((double)_testDelayPicker.Value),
                binding);
            _settings.TestAbility = ability;
            _settings.TestCycles = decimal.ToInt32(_testCyclesPicker.Value);
            _settings.TestDelaySeconds = _testDelayPicker.Value;
        }

        if (_settings.SoloAbility is { } solo && solo != _settings.TestAbility)
        {
            SetSolo(_settings.TestAbility);
        }

        SaveSettings();
        UpdateStatus();
    }

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
            default:
                ToggleSolo(SoloAbility(action.Value));
                break;
        }
    }

    private void UpdateOverlay()
    {
        lock (_captureSync)
        {
            var rows = AbilityKinds.Where(IsTracked).Select(kind =>
            {
                var tracker = _trackers.GetValueOrDefault(kind);
                var slot = _settings.Slots.FirstOrDefault(item => item.Kind == kind);
                var label = KindShortName(kind);
                if (!_monitoring)
                {
                    return $"{label}  Paused{DurationSuffix(tracker?.LastDuration)}";
                }

                if (slot?.Region is null)
                {
                    return $"{label}  Select area";
                }

                if (tracker?.State == AbilityState.Recharging)
                {
                    var progressText = tracker.TrackingWarning is not null
                        ? "recovering"
                        : tracker.DisplayProgress is { } progress ? $"{progress:P0}" : "acquiring progress";
                    var estimateText = tracker.EstimatedDuration is { } estimate && tracker.EstimateUncertainty is { } spread
                        ? $"Est. total {estimate.TotalSeconds:F2} ± {spread.TotalSeconds:F2} s"
                        : "Est. total: measuring…";
                    return $"{label}  {FormatDuration(tracker.CurrentDuration)} · {progressText}\n          {estimateText}";
                }

                if (tracker?.MeasurementDiscarded == true)
                {
                    return $"{label}  Endpoint obscured · measurement discarded";
                }

                if (tracker?.State == AbilityState.Consumed && tracker.TrackingWarning is not null)
                {
                    return $"{label}  Recharge-start pulse missed · reset detection";
                }

                return tracker?.State switch
                {
                    AbilityState.Ready => $"{label}  Ready{DurationSuffix(tracker.LastDuration)}",
                    AbilityState.Consumed => $"{label}  Used · waiting",
                    AbilityState.Complete => $"{label}  Complete · {FormatDuration(tracker.LastDuration)}{MeasurementSuffix(tracker)}",
                    _ => $"{label}  Watching"
                };
            }).ToArray();

            var accent = !_monitoring
                ? Color.Gray
                : _trackers.Values.Any(tracker => tracker.State == AbilityState.Recharging)
                    ? Color.FromArgb(86, 167, 229)
                    : Color.FromArgb(89, 190, 130);
            var savedMonitor = _settings.Slots.FirstOrDefault(slot => slot.Region is not null);
            var monitor = savedMonitor?.MonitorDeviceName is { } deviceName
                ? Screen.AllScreens.FirstOrDefault(screen => screen.DeviceName.Equals(deviceName, StringComparison.OrdinalIgnoreCase))?.Bounds
                    ?? savedMonitor.MonitorBounds
                : savedMonitor?.MonitorBounds;
            _overlay.SetStatus(_monitoring ? "ABILITY RECHARGE" : "TRACKING PAUSED", string.Join(Environment.NewLine, rows), accent, monitor);
        }
    }

    private void SetMonitoring(bool enabled)
    {
        lock (_captureSync)
        {
            if (_monitoring == enabled)
            {
                UpdateStatus();
                UpdateOverlay();
                return;
            }

            _monitoring = enabled;
            if (!enabled)
            {
                _repeatTest.Stop();
            }

            foreach (var tracker in _trackers.Values)
            {
                tracker.ResetCycle();
            }

            if (enabled)
            {
                RefreshCaptureRegions();
            }
        }

        UpdateStatus();
        UpdateOverlay();
    }

    private bool IsTracked(AbilityKind kind) => _settings.SoloAbility is not { } solo || solo == kind;

    private void ToggleSolo(AbilityKind kind) => SetSolo(_settings.SoloAbility == kind ? null : kind);

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

            RefreshCaptureRegions();
        }

        SaveSettings();
        UpdateStatus();
        UpdateOverlay();
    }

    private void RefreshCaptureRegions()
    {
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
                _captureErrors.Remove(kind);
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

                _captureErrors.Remove(kind);
            }
            catch (Exception exception)
            {
                _captureErrors[kind] = exception.Message;
                _trackers[kind].MarkSignalLost(DateTime.UtcNow);
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
                throw new InvalidOperationException("Destiny 2 window is unavailable; waiting to recover the HUD.");
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
                return JsonSerializer.Deserialize<TrackerSettings>(File.ReadAllText(_settingsPath)) ?? new TrackerSettings();
            }
        }
        catch (Exception exception)
        {
            MessageBox.Show($"Couldn't load saved settings: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        return new TrackerSettings();
    }

    private void EnsureSlotSettings()
    {
        _settings.Slots ??= [];
        _settings.SoloBindings ??= [];
        // Shortcuts are keyboard-only; drop mouse shortcuts saved by earlier versions.
        static InputBinding? KeyboardOnly(InputBinding? binding) => binding is { Device: InputDevice.Mouse } ? null : binding;
        _settings.RepeatTestBinding = KeyboardOnly(_settings.RepeatTestBinding);
        _settings.OverlayToggleBinding = KeyboardOnly(_settings.OverlayToggleBinding);
        foreach (var (kind, binding) in _settings.SoloBindings.ToArray())
        {
            if (binding.Device == InputDevice.Mouse)
            {
                _settings.SoloBindings.Remove(kind);
            }
        }

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

    private void SaveSettings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            File.WriteAllText(_settingsPath, JsonSerializer.Serialize(_settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, $"Couldn't save settings: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static string KindName(AbilityKind kind) => kind switch
    {
        AbilityKind.Grenade => "Grenade",
        AbilityKind.Melee => "Melee",
        AbilityKind.ClassAbility => "Class",
        _ => kind.ToString()
    };

    private static string KindShortName(AbilityKind kind) => kind switch
    {
        AbilityKind.Grenade => "Grenade",
        AbilityKind.Melee => "Melee  ",
        AbilityKind.ClassAbility => "Class    ",
        _ => kind.ToString()
    };

    private static string DurationSuffix(TimeSpan? duration) =>
        duration is { } value ? $" · last {FormatDuration(value)}" : string.Empty;

    private static string MeasurementSuffix(AbilitySlotTracker tracker) =>
        (tracker.LastUncertainty, tracker.LastSource) switch
        {
            ({ } spread, MeasurementSource.Event) => $" ± {spread.TotalSeconds:F3} s",
            ({ } spread, MeasurementSource.EventRefinedByModel) => $" ± {spread.TotalSeconds:F3} s (refined)",
            ({ } spread, MeasurementSource.Model) => $" ± {spread.TotalSeconds:F2} s (endpoint inferred)",
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
        Pause,
        AbilityUse,
        RepeatTest,
        SoloGrenade,
        SoloMelee,
        SoloClass,
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

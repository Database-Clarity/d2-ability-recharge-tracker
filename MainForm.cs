using System.Diagnostics;
using System.Drawing;
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
    private readonly Dictionary<AbilityKind, Button> _selectionBindingButtons = [];
    private readonly Dictionary<AbilityKind, string> _captureErrors = [];
    private readonly Queue<CaptureBatch> _retiredCaptureBatches = [];
    private CaptureBatch[] _captureBatches = [];
    private volatile bool _monitoring;
    private volatile bool _refreshCaptureRegionsRequested;
    private BindingCaptureTarget? _bindingCaptureTarget;
    private bool _inputListenerAvailable;
    private double _captureRate;
    private string? _captureLoopError;
    private string? _inputListenerError;
    private int _targetCaptureRate;
    private int _uiRefreshPending;
    private bool _selectingRegion;
    private ScreenCapture.GameWindowTarget? _currentGameWindow;
    private CancellationTokenSource? _hudRecordingCancellation;
    private Task<AbilityHudRecordingResult>? _hudRecordingTask;
    private bool _closingAfterHudRecording;

    public MainForm()
    {
        _settings = LoadSettings();
        EnsureSlotSettings();
        Text = "D2 Ability Recharge Tracker";
        _targetCaptureRate = Math.Clamp(_settings.CaptureRate, 30, 240);
        ClientSize = new Size(790, 690);
        MinimumSize = new Size(790, 690);
        MaximumSize = new Size(790, 690);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);
        BuildControls();
        _overlay.Show();
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
                _hudRecordingCancellation?.Cancel();
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
            Priority = ThreadPriority.BelowNormal,
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
        _recordHudButton.Text = "Record HUD…";
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
            var selectionBindingButton = new Button
            {
                Text = _settings.SelectionBindings.TryGetValue(kind, out var selectionBinding) ? selectionBinding.ToString() : "Shortcut…",
                Location = new Point(630, y + 2),
                Size = new Size(140, 32),
                AutoEllipsis = true
            };
            selectionBindingButton.Click += (_, _) => ToggleBindingCapture(SelectionCaptureTarget(kind));
            _selectionBindingButtons.Add(kind, selectionBindingButton);
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
            Controls.AddRange([name, selectButton, regionLabel, statusLabel, selectionBindingButton]);
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
        _pauseBindingButton.Text = PauseBinding.ToString();
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

        _stateStatus.SetBounds(20, 650, 750, 24);
        _stateStatus.ForeColor = Color.FromArgb(90, 90, 90);
        Controls.AddRange([title, intro, monitorLabel, _monitorPicker, captureLabel, _captureRatePicker, help, selectionGuideButton, resetSelectionsButton,
            _recordHudButton, divider, testGroup, _toggleButton,
            hotkeyLabel, _pauseBindingButton, anchorLabel, _anchorPicker, _stateStatus]);
    }

    private async void ToggleHudRecording()
    {
        if (_hudRecordingCancellation is { } activeRecording)
        {
            activeRecording.Cancel();
            _recordHudButton.Enabled = false;
            _recordHudButton.Text = "Finishing…";
            _hudRecordingStatus.Text = "Finishing the recording and writing its timestamp index…";
            return;
        }

        Rectangle bounds;
        try
        {
            bounds = AbilityHudRecorder.GetCaptureBounds(_settings.Slots);
        }
        catch (InvalidOperationException exception)
        {
            MessageBox.Show(this, exception.Message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        using var dialog = new SaveFileDialog
        {
            Title = "Save ability HUD debug recording",
            Filter = "Ability HUD recording (*.zip)|*.zip",
            DefaultExt = "zip",
            AddExtension = true,
            FileName = $"D2Ability-HUD-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK)
        {
            return;
        }

        var cancellation = new CancellationTokenSource();
        _hudRecordingCancellation = cancellation;
        _recordHudButton.Text = "Stop recording";
        _hudRecordingStatus.Text = "Recording the selected HUD crop at 15 FPS (50 MB / 10-minute maximum)…";
        _overlay.Hide();
        var recordingTask = Task.Run(() => AbilityHudRecorder.Record(
            bounds,
            dialog.FileName,
            cancellation.Token,
            progress => QueueHudRecordingProgress(progress, cancellation)));
        _hudRecordingTask = recordingTask;

        try
        {
            var result = await recordingTask;
            if (!_closingAfterHudRecording)
            {
                var stopReason = result.SizeLimitReached
                    ? "Stopped at the 50 MB limit"
                    : result.DurationLimitReached ? "Stopped at the 10-minute limit"
                    : cancellation.IsCancellationRequested ? "Recording stopped" : "Recording complete";
                _hudRecordingStatus.Text =
                    $"{stopReason}: {result.Frames} frames, {result.Bytes / 1_000_000d:F1} MB. Upload the ZIP here.";
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
            cancellation.Dispose();
            if (!_closingAfterHudRecording)
            {
                _hudRecordingTask = null;
                _hudRecordingCancellation = null;
                _recordHudButton.Enabled = true;
                _recordHudButton.Text = "Record HUD…";
                _overlay.Show();
                UpdateOverlay();
            }
        }
    }

    private void QueueHudRecordingProgress(AbilityHudRecordingProgress progress, CancellationTokenSource recording)
    {
        if (IsDisposed || !IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
                if (!ReferenceEquals(_hudRecordingCancellation, recording))
                {
                    return;
                }

                _hudRecordingStatus.Text =
                    $"Recording HUD · {progress.Elapsed:mm\\:ss} · {progress.Frames} frames · {progress.BytesWritten / 1_000_000d:F1} MB / 50 MB";
            }));
        }
        catch (InvalidOperationException) when (IsDisposed)
        {
        }
    }

    private void ResetSelections()
    {
        var result = MessageBox.Show(
            this,
            "Clear the saved screen areas for grenade, melee, and class ability? You can select them again at any time. Selection shortcuts will be kept.",
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
            _overlay.Show();
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
                    var remainingMilliseconds = (nextFrame - now) * 1000d / Stopwatch.Frequency;
                    cancellationToken.WaitHandle.WaitOne(Math.Max(1, (int)Math.Ceiling(remainingMilliseconds)));
                    continue;
                }

                var captureStarted = Stopwatch.GetTimestamp();
                if (CaptureFrame(desktop.DeviceContext))
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
            }
        }
    }

    private bool CaptureFrame(IntPtr desktopDeviceContext)
    {
        var capturedAny = false;
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

                    surface.Capture(desktopDeviceContext, bounds);
                    timestamp = DateTime.UtcNow;
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
                                tracker.ProgressSearchTolerance(timestamp)),
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
                    AbilityState.Complete => $"Recharge complete · {FormatDuration(tracker.LastDuration)}",
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
                else if (tracker.State == AbilityState.Recharging && tracker.ChargeProgress is { } progress)
                {
                    _statusLabels[kind].Text += $" · {progress:P0}";
                    if (tracker.EstimatedDuration is { } estimated)
                    {
                        _statusLabels[kind].Text += $" · est. {estimated.TotalSeconds:F2} s";
                    }
                }
            }

            _toggleButton.Text = _monitoring ? "Pause detection" : "Resume detection";
            _stateStatus.Text = _captureLoopError is { } captureLoopError
                ? $"Capture stopped: {captureLoopError}"
                : _inputListenerError is { } inputListenerError
                ? $"Input shortcuts unavailable: {inputListenerError}"
                : _monitoring
                ? $"Starts on the bottom-border flash · capture {_captureRate:F0}/{_targetCaptureRate} Hz (load-limited)"
                : $"Paused · {PauseBinding} to resume";
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

    private InputBinding PauseBinding => _settings.PauseBinding ?? InputBinding.DefaultPause;

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
        GetBindingButton(target).Text = "Press a key/button…";
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
                    else
                    {
                        _settings.SelectionBindings[SelectionAbility(target)] = binding;
                    }

                    UpdateBindingButtonText(target);
                    SaveSettings();
                    UpdateStatus();
                }));
            }
            catch (InvalidOperationException) when (IsDisposed)
            {
            }
        });
    }

    private Button GetBindingButton(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.Pause => _pauseBindingButton,
        BindingCaptureTarget.AbilityUse => _abilityUseBindingButton,
        BindingCaptureTarget.RepeatTest => _repeatTestBindingButton,
        BindingCaptureTarget.SelectGrenade or BindingCaptureTarget.SelectMelee or BindingCaptureTarget.SelectClass =>
            _selectionBindingButtons[SelectionAbility(target)],
        _ => throw new ArgumentOutOfRangeException(nameof(target))
    };

    private void UpdateBindingButtonText(BindingCaptureTarget target)
    {
        GetBindingButton(target).Text = GetConfiguredBinding(target)?.ToString() ?? "Record input…";
    }

    private InputBinding? GetConfiguredBinding(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.Pause => PauseBinding,
        BindingCaptureTarget.AbilityUse => _settings.AbilityUseBinding,
        BindingCaptureTarget.RepeatTest => _settings.RepeatTestBinding,
        _ => _settings.SelectionBindings.TryGetValue(SelectionAbility(target), out var binding) ? binding : null
    };

    private static BindingCaptureTarget SelectionCaptureTarget(AbilityKind kind) => kind switch
    {
        AbilityKind.Grenade => BindingCaptureTarget.SelectGrenade,
        AbilityKind.Melee => BindingCaptureTarget.SelectMelee,
        AbilityKind.ClassAbility => BindingCaptureTarget.SelectClass,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static AbilityKind SelectionAbility(BindingCaptureTarget target) => target switch
    {
        BindingCaptureTarget.SelectGrenade => AbilityKind.Grenade,
        BindingCaptureTarget.SelectMelee => AbilityKind.Melee,
        BindingCaptureTarget.SelectClass => AbilityKind.ClassAbility,
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
            default:
                SelectRegion(SelectionAbility(action.Value));
                break;
        }
    }

    private void UpdateOverlay()
    {
        lock (_captureSync)
        {
            var rows = AbilityKinds.Select(kind =>
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
                        : tracker.ChargeProgress is { } progress ? $"{progress:P0}" : "acquiring progress";
                    var estimateText = tracker.EstimatedDuration is { } estimate && tracker.EstimateConfidence is { } confidence
                        ? $"Est. total {estimate.TotalSeconds:F2} s · fit confidence {confidence:P0}"
                        : "Est. total unavailable";
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
                    AbilityState.Complete => $"{label}  Complete · {FormatDuration(tracker.LastDuration)}",
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
                    regions.Add(new CaptureSlot(kind, resolvedRegion, Math.Max(0, bottomPadding)));
                }

                _captureErrors.Remove(kind);
            }
            catch (Exception exception)
            {
                _captureErrors[kind] = exception.Message;
                _trackers[kind].MarkSignalLost(DateTime.UtcNow);
            }
        }

        var batches = groups.Values.SelectMany(slots =>
        {
            var batchesForDisplay = new List<CaptureBatch>();
            var pending = new List<CaptureSlot>();
            var bounds = Rectangle.Empty;
            long totalArea = 0;
            foreach (var slot in slots)
            {
                var captureBounds = new Rectangle(slot.Bounds.X, slot.Bounds.Y, slot.Bounds.Width, slot.Bounds.Height + slot.BottomPadding);
                var union = pending.Count == 0 ? captureBounds : Rectangle.Union(bounds, captureBounds);
                var area = (long)captureBounds.Width * captureBounds.Height;
                if (pending.Count > 0 && (long)union.Width * union.Height > (totalArea + area) * 4)
                {
                    batchesForDisplay.Add(new CaptureBatch(bounds, pending.ToArray()));
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
                batchesForDisplay.Add(new CaptureBatch(bounds, pending.ToArray()));
            }

            return batchesForDisplay;
        }).ToArray();
        var unchanged = _captureBatches.Length == batches.Length &&
            _captureBatches.Zip(batches).All(pair =>
                pair.First.Bounds == pair.Second.Bounds &&
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
        _settings.SelectionBindings ??= [];
        if (_settings.PauseBinding is null)
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
        SelectGrenade,
        SelectMelee,
        SelectClass
    }

    private sealed record CaptureSlot(AbilityKind Kind, Rectangle Bounds, int BottomPadding);

    private sealed class CaptureBatch(Rectangle bounds, CaptureSlot[] slots) : IDisposable
    {
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

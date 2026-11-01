using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace D2AbilityTracker;

internal sealed class MainForm : Form
{
    private const int HotkeyId = 0x5101;
    private const uint ModNoRepeat = 0x4000;
    private const int TargetCaptureRate = 240;
    private static readonly AbilityKind[] AbilityKinds = Enum.GetValues<AbilityKind>();
    private readonly string _settingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "D2AbilityTracker",
        "settings.json");
    private readonly TrackerSettings _settings;
    private readonly OverlayWindow _overlay = new();
    private readonly CancellationTokenSource _captureCancellation = new();
    private readonly object _captureSync = new();
    private readonly Thread _captureThread;
    private readonly Label _stateStatus = new();
    private readonly Button _toggleButton = new();
    private readonly ComboBox _hotkeyPicker = new();
    private readonly ComboBox _anchorPicker = new();
    private readonly ComboBox _monitorPicker = new();
    private readonly Dictionary<AbilityKind, AbilitySlotTracker> _trackers = [];
    private readonly Dictionary<AbilityKind, Label> _regionLabels = [];
    private readonly Dictionary<AbilityKind, Label> _statusLabels = [];
    private readonly Dictionary<AbilityKind, Button> _selectButtons = [];
    private readonly Dictionary<AbilityKind, string> _captureErrors = [];
    private readonly Queue<CaptureBatch> _retiredCaptureBatches = [];
    private CaptureBatch[] _captureBatches = [];
    private volatile bool _monitoring;
    private volatile bool _refreshCaptureRegionsRequested;
    private double _captureRate;
    private string? _captureLoopError;

    public MainForm()
    {
        _settings = LoadSettings();
        EnsureSlotSettings();
        Text = "D2 Ability Recharge Tracker";
        ClientSize = new Size(690, 480);
        MinimumSize = new Size(690, 480);
        MaximumSize = new Size(690, 480);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9);
        BuildControls();
        _overlay.Show();
        UpdateOverlay();
        UpdateStatus();
        SetMonitoring(true);
        FormClosed += (_, _) =>
        {
            _captureCancellation.Cancel();
            UnregisterHotKey(Handle, HotkeyId);
            _overlay.Close();
        };
        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Name = "D2 ability HUD capture"
        };
        _captureThread.Start();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RegisterSelectedHotkey();
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == HotkeyId)
        {
            SetMonitoring(!_monitoring);
        }

        base.WndProc(ref message);
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
            Text = "Tracks the cooldown line and slot background only; ability icons and element colours are not matched.",
            AutoSize = true,
            Location = new Point(20, 44)
        };
        var monitorLabel = new Label { Text = "Display", AutoSize = true, Location = new Point(20, 79) };
        _monitorPicker.SetBounds(78, 74, 350, 28);
        _monitorPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        RefreshDisplays();

        var help = new Label
        {
            Text = "Select each square slot tightly, including its border and the area where the cooldown line travels.",
            AutoSize = true,
            Location = new Point(20, 112)
        };

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
            var regionLabel = new Label
            {
                AutoEllipsis = true,
                Location = new Point(250, y + 1),
                Size = new Size(420, 22)
            };
            var statusLabel = new Label
            {
                AutoEllipsis = true,
                ForeColor = Color.DimGray,
                Location = new Point(250, y + 23),
                Size = new Size(420, 22)
            };
            _selectButtons.Add(kind, selectButton);
            _regionLabels.Add(kind, regionLabel);
            _statusLabels.Add(kind, statusLabel);
            Controls.AddRange([name, selectButton, regionLabel, statusLabel]);
        }

        var bottomDivider = new Label { BorderStyle = BorderStyle.Fixed3D, Height = 2, Width = 650, Location = new Point(20, 371) };
        _toggleButton.SetBounds(20, 388, 185, 36);
        _toggleButton.Click += (_, _) => SetMonitoring(!_monitoring);
        var hotkeyLabel = new Label { Text = "Toggle key", AutoSize = true, Location = new Point(224, 399) };
        _hotkeyPicker.SetBounds(300, 393, 80, 28);
        _hotkeyPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _hotkeyPicker.Items.AddRange(["F6", "F7", "F8", "F9", "F10", "F11", "F12"]);
        _hotkeyPicker.SelectedItem = _settings.Hotkey;
        if (_hotkeyPicker.SelectedIndex < 0)
        {
            _hotkeyPicker.SelectedItem = "F8";
        }

        _hotkeyPicker.SelectedIndexChanged += (_, _) =>
        {
            _settings.Hotkey = _hotkeyPicker.SelectedItem?.ToString() ?? "F8";
            RegisterSelectedHotkey();
            SaveSettings();
        };

        var anchorLabel = new Label { Text = "Overlay position", AutoSize = true, Location = new Point(410, 399) };
        _anchorPicker.SetBounds(515, 393, 150, 28);
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

        _stateStatus.SetBounds(20, 438, 640, 24);
        _stateStatus.ForeColor = Color.FromArgb(90, 90, 90);
        Controls.AddRange([title, intro, monitorLabel, _monitorPicker, help, divider, bottomDivider, _toggleButton,
            hotkeyLabel, _hotkeyPicker, anchorLabel, _anchorPicker, _stateStatus]);
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
        RefreshDisplays();
        if (_monitorPicker.SelectedItem is not DisplayChoice display)
        {
            MessageBox.Show(this, "Windows did not report an available display.", Text, MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var wasMonitoring = _monitoring;
        SetMonitoring(false);
        try
        {
            Hide();
            _overlay.Hide();
            Thread.Sleep(150);
            using var snapshot = ScreenCapture.Capture(display.Bounds);
            using var picker = new RegionPicker(snapshot, display.Bounds);
            if (picker.ShowDialog() == DialogResult.OK && picker.SelectedRegion is { } region)
            {
                var slot = GetSlot(kind);
                slot.Region = region;
                slot.MonitorBounds = display.Bounds;
                slot.MonitorDeviceName = display.DeviceName;
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

    private void CaptureLoop()
    {
        var cancellationToken = _captureCancellation.Token;
        var uiUpdateInterval = Stopwatch.Frequency / 10;
        var rateWindowStart = Stopwatch.GetTimestamp();
        var lastUiUpdate = rateWindowStart;
        var capturedFrames = 0;
        var frameInterval = Stopwatch.Frequency / (double)TargetCaptureRate;
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
                    nextFrame = Stopwatch.GetTimestamp() + (long)frameInterval;
                    continue;
                }

                var now = Stopwatch.GetTimestamp();
                if (now < nextFrame)
                {
                    var remainingMilliseconds = (nextFrame - now) * 1000d / Stopwatch.Frequency;
                    cancellationToken.WaitHandle.WaitOne(Math.Max(1, (int)Math.Ceiling(remainingMilliseconds)));
                    continue;
                }

                if (CaptureFrame(desktop.DeviceContext))
                {
                    capturedFrames++;
                }

                now = Stopwatch.GetTimestamp();
                nextFrame += (long)frameInterval;
                if (now - nextFrame > frameInterval * 3)
                {
                    nextFrame = now;
                }

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
                    capturedAny = true;
                    foreach (var slot in batch.Slots)
                    {
                        var region = slot.Bounds;
                        _trackers[slot.Kind].Observe(
                            AbilitySignalDetector.Read(
                                surface.Pixels,
                                surface.Stride,
                                region.Left - bounds.Left,
                                region.Top - bounds.Top,
                                region.Width,
                                region.Height),
                            timestamp);
                        _captureErrors.Remove(slot.Kind);
                    }
                }
                catch (Exception exception)
                {
                    foreach (var slot in batch.Slots)
                    {
                        _captureErrors[slot.Kind] = exception.Message;
                    }
                }
            }
        }

        return capturedAny;
    }

    private void RequestUiRefresh()
    {
        if (!IsHandleCreated || IsDisposed)
        {
            return;
        }

        try
        {
            BeginInvoke((Action)(() =>
            {
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
                    AbilityState.Consumed => "Used · waiting for the cooldown line",
                    AbilityState.Recharging => $"Recharging · {FormatDuration(tracker.CurrentDuration)}",
                    AbilityState.Complete => $"Recharge complete · {FormatDuration(tracker.LastDuration)}",
                    _ => slot.Region is null ? "Select this ability slot to track it" : "Watching slot background and cooldown line…"
                };
                _statusLabels[kind].ForeColor = tracker.State switch
                {
                    AbilityState.Consumed => Color.DarkOrange,
                    AbilityState.Recharging => Color.RoyalBlue,
                    AbilityState.Complete => Color.SeaGreen,
                    _ => Color.DimGray
                };
            }

            _toggleButton.Text = _monitoring ? "Pause detection" : "Resume detection";
            _stateStatus.Text = _captureLoopError is { } captureLoopError
                ? $"Capture stopped: {captureLoopError}"
                : _monitoring
                ? $"Starts on the cooldown line; confirms the blank end flash · capture {_captureRate:F0}/{TargetCaptureRate} Hz"
                : $"Paused · {_settings.Hotkey} to resume";
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

                return tracker?.State switch
                {
                    AbilityState.Ready => $"{label}  Ready{DurationSuffix(tracker.LastDuration)}",
                    AbilityState.Consumed => $"{label}  Used · waiting",
                    AbilityState.Recharging => $"{label}  {FormatDuration(tracker.CurrentDuration)}",
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

                    regions.Add(new CaptureSlot(kind, resolvedRegion));
                }

                _captureErrors.Remove(kind);
            }
            catch (Exception exception)
            {
                _captureErrors[kind] = exception.Message;
            }
        }

        var batches = groups.Values.Select(slots =>
        {
            var bounds = slots[0].Bounds;
            foreach (var slot in slots.Skip(1))
            {
                bounds = Rectangle.Union(bounds, slot.Bounds);
            }

            return new CaptureBatch(bounds, slots.ToArray());
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

    private void RegisterSelectedHotkey()
    {
        if (!IsHandleCreated)
        {
            return;
        }

        UnregisterHotKey(Handle, HotkeyId);
        var key = Enum.TryParse<Keys>(_settings.Hotkey, out var parsed) ? parsed : Keys.F8;
        if (!RegisterHotKey(Handle, HotkeyId, ModNoRepeat, (uint)key))
        {
            _stateStatus.Text = $"Couldn't register {_settings.Hotkey}; another app may be using it.";
            _stateStatus.ForeColor = Color.Firebrick;
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
        duration is { } value ? $"{(int)value.TotalMinutes:00}:{value.Seconds:00}.{value.Milliseconds / 100}" : "--:--.-";

    private sealed record DisplayChoice(string DeviceName, Rectangle Bounds, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record CaptureSlot(AbilityKind Kind, Rectangle Bounds);

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

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}

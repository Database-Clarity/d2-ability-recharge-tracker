using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using D2AbilityTracker;

// Keep every window the tests open off-screen, transparent and inactive; screenshots use DrawToBitmap.
HeadlessUi.Enabled = true;

if (args.Length == 2 && args[0] == "--ui-smoke")
{
    // Run against fresh, isolated portable storage next to the test binary (like CI), never the user's AppData settings.
    var portableFlag = Path.Combine(AppContext.BaseDirectory, "portable.flag");
    File.WriteAllText(portableFlag, "");
    foreach (var stale in new[] { "settings.json", "measurement-history.json", "update-source-version.txt" })
        File.Delete(Path.Combine(AppContext.BaseDirectory, stale));
    Assert(TrackerStoragePaths.Current.IsPortable, "The UI smoke test must use isolated portable storage.");
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
            Application.EnableVisualStyles();
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.ThrowException);
            Console.WriteLine("Opening compact UI...");
            using var form = new MainForm();
            Console.WriteLine("UI constructed.");
            form.Shown += (_, _) => form.BeginInvoke((Action)(() =>
            {
                try
                {
                    Console.WriteLine("Inspecting visible UI...");
                    Assert(form is AppWindow && form.FormBorderStyle == FormBorderStyle.None && form.Padding.Top >= 36,
                        "The app must reserve its integrated caption instead of displaying a native frame.");
                    Assert(Descendants(form).OfType<Button>().Any(button => button.AccessibleName == "Close") &&
                        Descendants(form).OfType<Button>().Any(button => button.AccessibleName == "Minimize"),
                        "Integrated caption actions must have accessible names.");
                    Assert(WindowHitTest(form, new Point(180, 18)) == 2,
                        "The integrated title bar must retain native window dragging.");
                    var navigation = Descendants(form).Single(control => control.Name == "PageNavigation");
                    var navigationRow = Descendants(form).Single(control => control.Name == "MainNavigation");
                    var pages = Descendants(form).Single(control => control.Name == "Pages");
                    var updateButton = Descendants(form).OfType<Button>().Single(button => button.Name == "CheckForUpdates");
                    var updatePill = Descendants(form).OfType<PillButton>().Single(button => button.Name == "UpdateAvailable");
                    Assert(!Descendants(navigationRow).Contains(updateButton) && updatePill.Parent == navigationRow && !updatePill.Visible,
                        "Manual update checks belong to Settings › About; the navigation row only shows an update pill when one is available.");
                    Assert(Descendants(navigationRow).OfType<Button>().All(button => button is NavigationButton || button == updatePill),
                        "The navigation row must not contain action buttons besides an available update.");
                    Assert(pages.Top <= 56 * form.DeviceDpi / 96f,
                        "Page navigation must be the only header row.");
                    Assert(navigation.Controls.Cast<Control>().Select(link => link.Text)
                            .SequenceEqual(["Tracking", "Repeat test", "History", "Settings"]),
                        "The utility should have Tracking, Repeat test, History, and Settings tabs.");
                    var settingsField = typeof(MainForm).GetField("_settings",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
                    var smokeSettings = (TrackerSettings)settingsField.GetValue(form)!;
                    Size? compactSize = null;
                    Assert(!Descendants(form).OfType<LinkLabel>().Any(link => link.Visible),
                        "A new app launch must not show a previous recording path.");
                    Directory.CreateDirectory(args[1]);
                    foreach (Button link in navigation.Controls)
                    {
                        link.PerformClick();
                        form.PerformLayout();
                        var statusPill = Descendants(form).OfType<StatusPill>().Single(pill => pill.Name == "CaptureStatus");
                        var lastLink = navigation.Controls.Cast<Control>().Last();
                        var linksRight = lastLink.RectangleToScreen(lastLink.ClientRectangle).Right;
                        var rowRight = navigationRow.RectangleToScreen(navigationRow.ClientRectangle).Right;
                        var pillBounds = statusPill.RectangleToScreen(statusPill.ClientRectangle);
                        Assert(statusPill.Parent is { Name: "MainNavigation" } && statusPill.Visible &&
                            !string.IsNullOrWhiteSpace(statusPill.Text) &&
                            linksRight <= pillBounds.Left && pillBounds.Right <= rowRight &&
                            rowRight - pillBounds.Right <= 8 * form.DeviceDpi / 96f &&
                            TextRenderer.MeasureText("Detecting · DXGI · 240/240 Hz", statusPill.Font).Width +
                                statusPill.Padding.Horizontal <= rowRight - linksRight,
                            "The capture status pill must stay right-aligned beside the page tabs without clipping on every page.");
                        Assert(!Descendants(form).OfType<Label>().Any(label => label.Visible && label.Text.StartsWith("Detection enabled")),
                            "The footer capture status must be replaced by the banner and pill.");
                        var page = pages.Controls.Cast<Control>().Single(control => control.Visible);
                        if (link.Text == "Tracking")
                        {
                            CheckTrackingPage(form, page, smokeSettings, args[1]);
                            Assert(!Descendants(page).OfType<Button>().Any(button => button.Text == "Record…"),
                                "Tracking must not repeat shortcut-recording buttons.");
                            Assert(!Descendants(page).OfType<Button>().Any(button => button.Text.StartsWith("Open ") || button.Text == "Record HUD"),
                                "HUD recording tools belong to Settings › Troubleshooting.");
                            Assert(Descendants(page).OfType<TwoLineStatusLabel>().Count() == 3,
                                "Every ability should use the fixed two-line status renderer.");
                            Assert(Descendants(page).OfType<CopyResultButton>().Count() == 3 &&
                                Descendants(page).OfType<CopyResultButton>().All(button => !string.IsNullOrWhiteSpace(button.ResultText)),
                                "Each tracking result must have an accessible copy action with complete text.");
                            Assert(Descendants(page).OfType<Button>().Any(button => button.Text.Contains("Pause detection (")),
                                "The pause action should display its hotkey on the Tracking page.");
                            const string longRecording = @"C:\Users\Somebody\Documents\D2AbilityTracker\HUD recordings\A very long directory for this measurement session\Another long directory\latest-hud-recording-2026-11-02.zip";
                            form.SetLastRecordingPath(longRecording);
                            Assert(!Descendants(page).OfType<PathLinkLabel>().Any(),
                                "The folder action must replace the path link.");
                        }
                        if (link.Text == "Settings")
                        {
                            var settingsButtons = Descendants(page).OfType<Button>().Where(button => button.Visible).ToArray();
                            Assert(settingsButtons.Count(button => button.Text == "Record HUD") == 1 &&
                                settingsButtons.Count(button => button.Text == "Open recordings folder") == 1 &&
                                settingsButtons.Contains(updateButton) && updateButton.Text == "Check for updates" &&
                                settingsButtons.Any(button => button.Text == "Changelog"),
                                "Settings must group Troubleshooting (Record HUD, Open recordings folder) and About (updates, changelog).");
                            Assert(Descendants(page).OfType<Label>().Where(label => label.Visible)
                                    .Count(label => label.Text is "Capture" or "Shortcuts" or "Overlay" or "Troubleshooting" or "About" &&
                                        label.Font.Bold && label.Font.SizeInPoints >= 10 && label.ForeColor == TrackerTheme.Text) == 5,
                                "Settings section headings must be 10 pt bold text.");
                            var shortcutNames = new[] { "PauseShortcut", "OverlayShortcut", "RepeatTestShortcut" };
                            Assert(shortcutNames.All(name => settingsButtons.Count(button => button.Name == name) == 1) &&
                                settingsButtons.Where(button => shortcutNames.Contains(button.Name)).All(button => !string.IsNullOrWhiteSpace(button.Text)) &&
                                Descendants(page).OfType<Label>().Any(label => label.Visible && label.Name == "ShortcutHint" && label.Text.Contains("Esc")),
                                "Settings › Shortcuts must hold the pause, overlay and repeat-test shortcuts with an Esc-clears hint.");
                            Assert(Descendants(page).OfType<Label>().Any(label => label.Visible && label.Name == "MeasuredCaptureRate" &&
                                    !string.IsNullOrWhiteSpace(label.Text)),
                                "Settings › Capture must show the live measured capture rate and backend.");
                            Assert(!Descendants(page).OfType<Label>().Any(label => label.Visible && label.Text == "Display"),
                                "The display picker belongs to the area-selection fallback, not Settings.");
                        }
                        if (link.Text == "Repeat test")
                        {
                            var repeatSettings = smokeSettings;
                            var abilityPicker = Descendants(page).OfType<FlatComboBox>().Single(picker => picker.Items.Count == 3);
                            abilityPicker.SelectedItem = abilityPicker.Items.Cast<object>().Single(item => item.ToString() == "Grenade");
                            var grenadeInput = new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.None);
                            var meleeInput = new InputBinding(InputDevice.Keyboard, 0x42, InputModifiers.None);
                            repeatSettings.AbilityUseBindings[AbilityKind.Grenade] = grenadeInput;
                            repeatSettings.AbilityUseBindings[AbilityKind.Melee] = meleeInput;
                            var captureTargetType = typeof(MainForm).GetNestedType("BindingCaptureTarget",
                                System.Reflection.BindingFlags.NonPublic)!;
                            typeof(MainForm).GetMethod("UpdateBindingButtonText",
                                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                                .Invoke(form, [Enum.Parse(captureTargetType, "AbilityUse")]);
                            var repeatButtons = Descendants(page).OfType<Button>().ToArray();
                            var inputButton = repeatButtons.FirstOrDefault(button => button.Text.StartsWith("Grenade:", StringComparison.Ordinal))
                                ?? throw new InvalidOperationException($"Missing Grenade binding button: {string.Join(" | ", repeatButtons.Select(button => $"{button.AccessibleName}={button.Text}"))}");
                            Assert(inputButton.Text == $"Grenade: {grenadeInput}", "The repeat menu must show the selected ability's saved input.");
                            abilityPicker.SelectedItem = abilityPicker.Items.Cast<object>().Single(item => item.ToString() == "Melee");
                            Assert(inputButton.Text == $"Melee: {meleeInput}" &&
                                inputButton.AccessibleName == "Ability-use input for Melee",
                                "Changing repeat ability must select and label its own saved input.");
                            abilityPicker.SelectedItem = abilityPicker.Items.Cast<object>().Single(item => item.ToString() == "Grenade");
                            typeof(MainForm).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
                            var testToggles = Descendants(page).OfType<Button>().Where(button => button.Name == "RepeatTestToggle").ToArray();
                            Assert(testToggles.Length == 1 && testToggles[0].Text.StartsWith("Start test", StringComparison.Ordinal) &&
                                    !Descendants(page).OfType<Button>().Any(button => button != testToggles[0] && button.Text.Contains(" test")),
                                "Start and stop must be one repeat-test toggle button.");
                            Assert(!Descendants(page).OfType<Button>().Any(button => button.Name == "RepeatTestShortcut"),
                                "The start / stop shortcut belongs to Settings › Shortcuts.");
                            var blockedReason = Descendants(page).OfType<Label>().Single(label => label.Name == "RepeatTestBlockedReason");
                            Assert(testToggles[0].Enabled == string.IsNullOrEmpty(blockedReason.Text),
                                "A disabled Start test must explain why inline, and an enabled one must not.");
                            var grenadeSlot = smokeSettings.Slots.Single(slot => slot.Kind == AbilityKind.Grenade);
                            var savedRegion = grenadeSlot.Region;
                            grenadeSlot.Region = null;
                            typeof(MainForm).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
                            Assert(!testToggles[0].Enabled && blockedReason.Text.Contains("Grenade area"),
                                "Without an area, Start test must be disabled with the reason inline.");
                            grenadeSlot.Region = savedRegion;
                            typeof(MainForm).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance |
                                System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
                        }
                        foreach (var picker in Descendants(page).OfType<ComboBox>())
                        {
                            Assert(picker is FlatComboBox, "All dropdowns must use the shared dark field.");
                            using var fieldImage = new Bitmap(picker.Width, picker.Height);
                            picker.DrawToBitmap(fieldImage, picker.ClientRectangle);
                            Assert(fieldImage.GetPixel(picker.Width - 5, picker.Height / 2).GetBrightness() < 0.3,
                                "The dropdown arrow area must use dark chrome, not the native white button.");
                        }
                        foreach (var number in Descendants(page).OfType<NumericUpDown>())
                        {
                            Assert(number is FlatNumericUpDown && number.Parent is InputFrame,
                                "Numeric fields must use the shared input frame and dark spinner.");
                            var original = number.Value;
                            if (number.Value < number.Maximum)
                            {
                                number.UpButton();
                                Assert(number.Value == Math.Min(number.Maximum, original + number.Increment),
                                    "The themed numeric field must preserve native increment behavior.");
                                number.DownButton();
                                number.Value = original;
                            }
                            Assert(number.Parent is InputFrame frame && frame.Height == 30 * form.DeviceDpi / 96f,
                                "Numeric frames must match the common control height.");
                            number.Enabled = false;
                            var edit = number.Controls.OfType<TextBox>().Single();
                            using var disabledNumber = new Bitmap(edit.Width, edit.Height);
                            edit.DrawToBitmap(disabledNumber, edit.ClientRectangle);
                            Assert(HasReadableText(disabledNumber), "Disabled numeric values must remain readable on dark inputs.");
                            number.Enabled = true;
                        }
                        if (link.Text == "History")
                        {
                            Assert(form.HistoryVisible && page is MeasurementHistoryView &&
                                page.Height >= 400 * form.DeviceDpi / 96f,
                                "History must be a full tab with its own explicit height.");
                        }
                        else
                        {
                            Assert(form.ClientSize.Height <= 500 * form.DeviceDpi / 96f,
                                "Each page should fit a compact desktop window.");
                            Assert(compactSize is null || compactSize == form.Size,
                                "Switching between compact pages must not resize the window.");
                            compactSize ??= form.Size;
                        }
                        Assert(form.ClientSize.Width <= 680 * form.DeviceDpi / 96f,
                            "The main window must stay narrow.");
                        foreach (var control in Descendants(page).Where(control => control.Visible && control is Button or ComboBox or NumericUpDown or LinkLabel))
                        {
                            var bounds = form.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
                            Assert(form.ClientRectangle.Contains(bounds), $"{control.Text} must remain inside the window.");
                        }

                        using var image = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(args[1], $"ui-{link.Text.Replace(' ', '-')}.png"));
                    }

                    RenderCaptureStatusStates(form, navigation, args[1]);

                    foreach (var button in Descendants(form).OfType<Button>().Where(button => !button.Enabled && !string.IsNullOrWhiteSpace(button.Text)))
                    {
                        using var disabled = new Bitmap(button.Width, button.Height);
                        button.DrawToBitmap(disabled, button.ClientRectangle);
                        Assert(HasReadableText(disabled), "Disabled button text must not fall back to black on dark gray.");
                    }
                    var repeat = (RepeatTestSession)typeof(MainForm).GetField("_repeatTest",
                        System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(form)!;
                    var now = DateTime.UtcNow;
                    repeat.Start(AbilityKind.Grenade, 3, TimeSpan.Zero, new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.None));
                    for (var index = 0; index < 3; index++)
                    {
                        var entry = new MeasurementHistoryEntry(Guid.NewGuid(), AbilityKind.Grenade, TimeSpan.FromSeconds(64.23 + index * 0.02).TotalSeconds,
                            64.20 + index * 0.02, 0.02, 0.16, MeasurementSource.Event, now, null, null, RepeatTestId: repeat.RunId);
                        repeat.ObserveState(AbilityKind.Grenade, AbilityState.Recharging, now);
                        repeat.RecordRecharge(AbilityKind.Grenade, TimeSpan.FromSeconds(entry.DurationSeconds), now,
                            TimeSpan.FromSeconds(entry.RegressionSeconds!.Value), entry);
                        if (index < 2) Assert(repeat.TryGetDueUse(now, true, out _), "Sample repeat must advance without sending input.");
                    }
                    navigation.Controls.OfType<Button>().Single(button => button.Text == "Repeat test").PerformClick();
                    typeof(MainForm).GetMethod("UpdateStatus", System.Reflection.BindingFlags.Instance |
                        System.Reflection.BindingFlags.NonPublic)!.Invoke(form, null);
                    var repeatCopy = Descendants(form).OfType<CopyResultButton>().Single(button => button.AccessibleName == "Copy repeat-test result");
                    Assert(repeatCopy.ResultText.Contains("± 0.023 s") && repeatCopy.ResultText.Contains("\nRegression") &&
                        repeatCopy.ResultText.Contains("± 0.160 s") && repeatCopy.ResultText.Contains("3/3 cycles"),
                        $"Standalone repeat results and their copy action must include both aggregates and uncertainties: {repeatCopy.ResultText}");
                    using (var completed = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(completed, new Rectangle(Point.Empty, completed.Size));
                        completed.Save(Path.Combine(args[1], "ui-Repeat-completed.png"));
                    }
                    using (var overlay = new OverlayWindow())
                    {
                        overlay.SetContent("Result", [new OverlayRow("Grenade", "64.25 ± 0.023 s", "Regression 64.22 ± 0.160 s")], true, TrackerTheme.Accent);
                        overlay.Show();
                        Assert(!Descendants(overlay).OfType<CopyResultButton>().Any(button => button.Visible),
                            "Normal overlay must retain noninteractive click-through presentation.");
                        overlay.SetMoveMode(true);
                        Assert(Descendants(overlay).OfType<CopyResultButton>().Single().ResultText.Contains("± 0.160 s"),
                            "Overlay move mode must expose a copy action for the complete result.");
                        overlay.SetMoveMode(false);
                        overlay.SetContent("TEST · CYCLE 2/5 · MEASURING", [
                            new OverlayRow("Grenade", "41.20 s · 64%", "Est. total 64.25 ± 0.30 s", Color.FromArgb(86, 167, 229), 0.64f),
                            new OverlayRow("Melee", "Ready", "Last 22.10 s", Color.FromArgb(89, 190, 130)),
                            new OverlayRow("Class", "Ready", "Last 30.02 s", Color.FromArgb(89, 190, 130))], true, TrackerTheme.Accent);
                        Assert(overlay.Height <= (int)Math.Round((22 + 3 * 37 + 2 * 6 + 10) * overlay.DeviceDpi / 96.0),
                            "The overlay header must stay slim (about 20 px).");
                        using (var overlayImage = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(overlayImage, new Rectangle(Point.Empty, overlayImage.Size));
                            overlayImage.Save(Path.Combine(args[1], "ui-Overlay-test.png"));
                        }
                        overlay.SetMoveMode(true);
                        using (var overlayImage = new Bitmap(overlay.Width, overlay.Height))
                        {
                            overlay.DrawToBitmap(overlayImage, new Rectangle(Point.Empty, overlayImage.Size));
                            overlayImage.Save(Path.Combine(args[1], "ui-Overlay-move.png"));
                        }
                        overlay.SetMoveMode(false);
                        overlay.Close();
                    }
                    // Fresh portable storage starts empty: seed two sessions with their own columns plus an unsorted result.
                    var smokeHistory = new MeasurementHistoryStore(Path.Combine(args[1], "smoke-history.json"));
                    var baseline = smokeHistory.CreateSession("Base stats", DateTimeOffset.UtcNow.AddHours(-3));
                    smokeHistory.AddCustomColumn(baseline.Id, "Armor");
                    var baselineEntry = new MeasurementHistoryEntry(Guid.NewGuid(), AbilityKind.Grenade, 82.40, 82.31, 0.02, 0.18,
                        MeasurementSource.Event, DateTimeOffset.UtcNow.AddHours(-3), "No exotic", null);
                    smokeHistory.TryAddMeasurement(baselineEntry);
                    smokeHistory.SaveCustomValue(baselineEntry.Id, "Armor", "Legendary");
                    smokeHistory.TryAddMeasurement(new MeasurementHistoryEntry(Guid.NewGuid(), AbilityKind.Melee, 20.10, 20.04, 0.02, 0.12,
                        MeasurementSource.Event, DateTimeOffset.UtcNow.AddHours(-3).AddMinutes(4), null, null));
                    var statSession = smokeHistory.CreateSession("Grenade stat testing", DateTimeOffset.UtcNow.AddMinutes(-5));
                    smokeHistory.AddCustomColumn(statSession.Id, "Grenade Stat");
                    smokeHistory.TryAddMeasurement(new MeasurementHistoryEntry(
                        Guid.NewGuid(), AbilityKind.Grenade, 64.25, 64.22, 0.02, 0.16,
                        MeasurementSource.Event, DateTimeOffset.UtcNow, "Grenade stat 5", null));
                    var sampleRun = Guid.NewGuid();
                    var sampleCycles = Enumerable.Range(0, 3).Select(index => new MeasurementHistoryEntry(
                        Guid.NewGuid(), AbilityKind.Grenade, 64.23 + index * 0.02, 64.20 + index * 0.02,
                        0.02, 0.16, MeasurementSource.Event, DateTimeOffset.UtcNow.AddSeconds(index),
                        null, null, RepeatTestId: sampleRun)).ToArray();
                    foreach (var cycle in sampleCycles) smokeHistory.TryAddMeasurement(cycle);
                    var sampleMean = RepeatMeasurementSummary.Create(sampleRun, sampleCycles, DateTime.UtcNow.AddSeconds(4));
                    smokeHistory.TryAddMeasurement(sampleMean);
                    smokeHistory.SaveNote(sampleMean.Id, "Grenade stat 5 · repeat test");
                    smokeHistory.SaveCustomValue(sampleMean.Id, "Grenade Stat", "5");
                    var unsortedEntry = new MeasurementHistoryEntry(Guid.NewGuid(), AbilityKind.ClassAbility, 30.02, 29.95, 0.02, 0.14,
                        MeasurementSource.Event, DateTimeOffset.UtcNow.AddDays(-1), "Before sessions", null);
                    smokeHistory.TryAddMeasurement(unsortedEntry, null);
                    form.UseMeasurementHistory(smokeHistory);
                    var historyLink = navigation.Controls.OfType<Button>().Single(button => button.Text == "History");
                    navigation.Controls.OfType<Button>().Single(button => button.Text == "Repeat test").PerformClick();
                    var collapsedSize = form.Size;
                    var openForms = Application.OpenForms.Count;
                    historyLink.PerformClick();
                    form.PerformLayout();
                    var historyForm = Descendants(form).OfType<MeasurementHistoryView>().Single();
                    Assert(form.HistoryVisible && historyForm.Visible && historyForm.Parent == pages &&
                        pages.Controls.Cast<Control>().Single(control => control.Visible) == historyForm &&
                        Application.OpenForms.Count == openForms,
                        "History must open as a tab inside the main window.");
                    var workHeight = Screen.FromControl(form).WorkingArea.Height;
                    Assert(form.Height > collapsedSize.Height || form.Height >= workHeight - 48,
                        "The History tab must get its own, taller explicit height.");
                    historyForm.ShowPrompt = _ => throw new InvalidOperationException("Tests must answer History prompts explicitly.");
                    var historyGrid = Descendants(historyForm).OfType<DataGridView>().Single();
                    var sessionsToolbar = Descendants(historyForm).Single(control => control.Name == "SessionsToolbar");
                    var allToolbar = Descendants(historyForm).Single(control => control.Name == "AllToolbar");
                    var activeLabel = Descendants(historyForm).OfType<Label>().Single(label => label.Name == "ActiveSession");
                    var toggleActive = Descendants(historyForm).OfType<Button>().Single(button => button.Name == "ToggleActive");
                    string[] HeaderTexts() => historyGrid.Columns.Cast<DataGridViewColumn>().Where(column => column.Visible)
                        .Select(column => column.HeaderText).ToArray();
                    Button[] VisibleActions() => Descendants(historyForm).OfType<Button>().Where(button => button.Visible).ToArray();
                    void AssertTidy(string view)
                    {
                        historyForm.PerformLayout();
                        Assert(VisibleActions().Length <= 6, $"{view} must show at most six actions.");
                        foreach (var control in Descendants(historyForm).Where(control => control.Visible && control is Button or ComboBox or Label or SegmentedControl))
                        {
                            Assert(historyForm.ClientRectangle.Contains(historyForm.RectangleToClient(
                                control.RectangleToScreen(control.ClientRectangle))),
                                $"{view}: {control.Name} {control.Text} must fit inside the History tab.");
                        }
                    }
                    void SaveHistoryImage(string name)
                    {
                        using var image = new Bitmap(form.Width, form.Height);
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(args[1], name));
                    }

                    // Sessions view: one session at a time, with only that session's columns.
                    Assert(historyForm.ViewMode == MeasurementHistoryView.SessionsView && historyForm.SelectedSessionId == statSession.Id &&
                        sessionsToolbar.Visible && !allToolbar.Visible && activeLabel.Text == "● Active: Grenade stat testing" &&
                        toggleActive.Text == "End session",
                        "History must open on the Sessions view with the active session selected and marked.");
                    Assert(HeaderTexts().Contains("Grenade Stat") && !HeaderTexts().Contains("Armor") && !HeaderTexts().Contains("Session") &&
                        historyGrid.Rows.Count == 5,
                        "The Sessions view must show only the selected session's measurements and custom columns.");
                    AssertTidy("Sessions view");
                    SaveHistoryImage("ui-History-sessions.png");
                    var sessionCsv = historyForm.ExportVisibleCsv();
                    Assert(sessionCsv.Contains("\"Grenade Stat\"") && !sessionCsv.Contains("Armor") && !sessionCsv.Contains("Before sessions"),
                        "Session export must include only that session's rows and columns.");
                    var customColumn = historyGrid.Columns.Cast<DataGridViewColumn>().Single(column => column.HeaderText == "Grenade Stat");
                    Assert(!customColumn.ReadOnly && historyGrid.Columns["Measured"]!.ReadOnly,
                        "Custom columns must be editable while measured values stay protected.");
                    var meanRow = historyGrid.Rows.Cast<DataGridViewRow>().Single(row => row.Tag is Guid id && id == sampleMean.Id);
                    historyGrid.CurrentCell = meanRow.Cells[customColumn.Index];
                    Assert(historyGrid.BeginEdit(false), "A custom value must support inline editing.");
                    ((TextBox)historyGrid.EditingControl!).Text = "10";
                    historyGrid.EndEdit();
                    Assert(smokeHistory.Measurements.Single(item => item.Id == sampleMean.Id).CustomValues!["Grenade Stat"] == "10",
                        "Editing a custom cell must persist its value.");
                    Assert(historyGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["Result"].Value?.ToString() == "Mean · 3" &&
                        row.Cells["Measured"].Value?.ToString()?.Contains("± 0.023 s") == true),
                        "History must display repeat count and aggregate uncertainty alongside individual cycles.");
                    foreach (var bar in historyGrid.Controls.OfType<ScrollBar>().Where(bar => bar.Visible))
                    {
                        using var scrollbarImage = new Bitmap(bar.Width, bar.Height);
                        bar.DrawToBitmap(scrollbarImage, bar.ClientRectangle);
                        Assert(scrollbarImage.GetPixel(1, 1).GetBrightness() < 0.3,
                            "History scrollbars must not expose native light arrow buttons.");
                    }
                    var noteEditor = Descendants(historyForm).OfType<TextBox>()
                        .Single(textBox => textBox.AccessibleName == "Measurement note");
                    var noteFrame = noteEditor.Parent!;
                    var saveNote = Descendants(historyForm).OfType<Button>().Single(button => button.Text == "Save note");
                    var assignButton = Descendants(historyForm).OfType<Button>().Single(button => button.Text == "Add to session ▾");
                    Assert(!assignButton.Visible && customColumn.AutoSizeMode == DataGridViewAutoSizeColumnMode.Fill &&
                        noteEditor.MaxLength == 240 && noteEditor.Height <= 44 &&
                        Math.Abs(saveNote.PointToScreen(new Point(0, saveNote.Height / 2)).Y -
                            noteFrame.PointToScreen(new Point(0, noteFrame.Height / 2)).Y) <= 1 &&
                        !historyGrid.Controls.OfType<HScrollBar>().Any(bar => bar.Visible),
                        "A custom column should share the grid width while the compact note editor and Save note stay aligned.");
                    Assert(historyGrid.Columns["Measured"]!.HeaderText == "Measured ±" &&
                        historyGrid.Columns["Regression"]!.HeaderText == "Regression ±",
                        "Each duration must have its own clearly identified uncertainty.");
                    Assert(historyGrid.ClientSize.Height >= historyGrid.ColumnHeadersHeight + 4 * historyGrid.RowTemplate.Height + SystemInformation.HorizontalScrollBarHeight,
                        "History should reserve at least four measurement rows.");

                    // All measurements view: every result with its session, base columns only.
                    historyForm.ViewMode = MeasurementHistoryView.AllView;
                    Assert(!sessionsToolbar.Visible && allToolbar.Visible && assignButton.Visible &&
                        HeaderTexts().Contains("Session") && !HeaderTexts().Contains("Grenade Stat") && !HeaderTexts().Contains("Armor") &&
                        historyGrid.Rows.Count == smokeHistory.Measurements.Count &&
                        historyGrid.Rows.Cast<DataGridViewRow>().Any(row => row.Cells["Session"].Value?.ToString() == "Unsorted") &&
                        !Descendants(historyForm).OfType<Button>().Single(button => button.Text == "Add column…").Visible,
                        "All measurements must list every result with a Session column and no custom columns.");
                    AssertTidy("All measurements view");
                    SaveHistoryImage("ui-History-all.png");
                    var allCsv = historyForm.ExportVisibleCsv();
                    Assert(allCsv.Contains("\"Before sessions\"") && !allCsv.Contains("Grenade Stat") && !allCsv.Contains("Armor"),
                        "All-measurements export must include every row with base columns only.");
                    var unsortedRow = historyGrid.Rows.Cast<DataGridViewRow>().Single(row => row.Tag is Guid id && id == unsortedEntry.Id);
                    historyGrid.CurrentCell = unsortedRow.Cells[0];
                    var assignItems = historyForm.AssignMenu.Items.OfType<ToolStripMenuItem>().ToArray();
                    Assert(assignItems.Select(item => item.Text).SequenceEqual(["Grenade stat testing · Active", "Base stats", "Unsorted"]),
                        "Add to session must offer every session (marking the active one) and Unsorted.");
                    assignItems.Single(item => item.Text == "Base stats").PerformClick();
                    Assert(smokeHistory.Measurements.Single(item => item.Id == unsortedEntry.Id).SessionId == baseline.Id &&
                        smokeHistory.ActiveSessionId == statSession.Id &&
                        unsortedRow.DataGridView is null && historyGrid.SelectedRows[0].Cells["Session"].Value?.ToString() == "Base stats",
                        "Add to session must move the selected result into any session without changing the active one.");

                    // Session actions: columns stay per session; rename, activate, create and delete.
                    historyForm.SelectSession(baseline.Id);
                    Assert(HeaderTexts().Contains("Armor") && !HeaderTexts().Contains("Grenade Stat") && historyGrid.Rows.Count == 3 &&
                        toggleActive.Text == "Make active",
                        "Selecting another session must show its own columns and rows.");
                    var addColumn = Descendants(historyForm).OfType<Button>().Single(button => button.Text == "Add column…");
                    historyForm.ShowPrompt = dialog => { dialog.Value = "Stat 2"; return DialogResult.OK; };
                    addColumn.PerformClick();
                    Assert(smokeHistory.GetCustomColumns(baseline.Id).SequenceEqual(["Armor", "Stat 2"]) &&
                        smokeHistory.GetCustomColumns(statSession.Id).SequenceEqual(["Grenade Stat"]) && HeaderTexts().Contains("Stat 2"),
                        "Add column… must add the prompted column to the selected session only.");
                    historyForm.ShowPrompt = _ => DialogResult.Cancel;
                    addColumn.PerformClick();
                    Assert(smokeHistory.GetCustomColumns(baseline.Id).Count == 2, "Cancelling the column prompt must not add a column.");
                    historyForm.ShowPrompt = dialog =>
                    {
                        Assert(dialog.HasInput && dialog.Value == "Base stats", "Rename must start from the current name.");
                        dialog.Value = "Base stats (40)";
                        return DialogResult.OK;
                    };
                    historyForm.SessionMenu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Rename…").PerformClick();
                    Assert(smokeHistory.Sessions.Single(session => session.Id == baseline.Id).Name == "Base stats (40)",
                        "Rename… must rename the selected session.");
                    toggleActive.PerformClick();
                    Assert(smokeHistory.ActiveSessionId == baseline.Id && toggleActive.Text == "End session" &&
                        activeLabel.Text == "● Active: Base stats (40)",
                        "Make active must reopen and activate the selected session.");
                    toggleActive.PerformClick();
                    Assert(smokeHistory.ActiveSessionId is null && toggleActive.Text == "Make active" && activeLabel.Text == "No active session",
                        "End session must clear the active session.");
                    historyForm.ShowPrompt = dialog => { dialog.Value = "Next stat test"; return DialogResult.OK; };
                    Descendants(historyForm).OfType<Button>().Single(button => button.Text == "New session…").PerformClick();
                    var nextSession = smokeHistory.Sessions.Single(session => session.Name == "Next stat test");
                    Assert(smokeHistory.ActiveSessionId == nextSession.Id && historyForm.SelectedSessionId == nextSession.Id &&
                        historyGrid.Rows.Count == 0 && HeaderTexts().All(header => header is not ("Armor" or "Grenade Stat")),
                        "New session… must create, activate and select an empty session without other sessions' columns.");
                    historyForm.SelectSession(baseline.Id);
                    var baselineCount = historyGrid.Rows.Count;
                    PromptDialog? confirmation = null;
                    historyForm.ShowPrompt = dialog => { confirmation = dialog; return DialogResult.OK; };
                    historyForm.SessionMenu.Items.OfType<ToolStripMenuItem>().Single(item => item.Text == "Delete…").PerformClick();
                    Assert(confirmation is { HasInput: false } && smokeHistory.Sessions.All(session => session.Id != baseline.Id) &&
                        smokeHistory.Measurements.Count(item => item.SessionId is null) == baselineCount &&
                        smokeHistory.Measurements.Single(item => item.Id == baselineEntry.Id).CustomValues!["Armor"] == "Legendary" &&
                        historyForm.SelectedSessionId is { } remaining && remaining != baseline.Id,
                        "Delete… must confirm, then remove the session and leave its measurements unsorted.");
                    foreach (var (dialogName, prompt) in new[]
                    {
                        ("ui-AddColumn.png", new PromptDialog("Add column", "Name a new column for “Grenade stat testing”. Double-click its cells to edit values; this session's CSV export includes it.", "Add column", placeholder: "e.g. Grenade Stat")),
                        ("ui-DeleteSession.png", new PromptDialog("Delete session", "Delete “Grenade stat testing” and its columns? Its 5 results and their notes move to Unsorted in All measurements.", "Delete session", destructive: true))
                    })
                    {
                        using (prompt)
                        {
                            prompt.Show(form);
                            var accept = Descendants(prompt).OfType<Button>().Single(button => button == prompt.AcceptButton);
                            Assert(prompt.Location == HeadlessUi.OffscreenLocation && accept.Enabled != prompt.HasInput,
                                "History prompts must open off-screen in tests; name prompts disable their action until a name is typed.");
                            if (prompt.HasInput) prompt.Value = "  Melee Stat ";
                            Assert((!prompt.HasInput || accept.Enabled && prompt.Value == "Melee Stat") &&
                                Descendants(prompt).Where(control => control.Visible && control != prompt).All(control =>
                                    prompt.ClientRectangle.Contains(prompt.RectangleToClient(control.RectangleToScreen(control.ClientRectangle)))),
                                "History prompts must fit their controls and trim the typed name.");
                            using var dialogImage = new Bitmap(prompt.Width, prompt.Height);
                            prompt.DrawToBitmap(dialogImage, new Rectangle(Point.Empty, dialogImage.Size));
                            dialogImage.Save(Path.Combine(args[1], dialogName));
                            prompt.Close();
                        }
                    }
                    form.SetHistoryVisible(false);
                    Assert(!historyForm.Visible && !form.HistoryVisible && form.Size == collapsedSize &&
                        pages.Controls.Cast<Control>().Single(control => control.Visible) == testPage(),
                        "Leaving history must return to the previous page at its compact size.");
                    Control testPage() => pages.Controls.Cast<Control>().Single(page =>
                        Descendants(page).OfType<CopyResultButton>().Any(button => button.AccessibleName == "Copy repeat-test result"));
                    using (var guide = new SelectionGuide())
                    {
                        guide.Show(form);
                        Assert(guide.FormBorderStyle == FormBorderStyle.None, "The selection guide must share app chrome.");
                        using var image = new Bitmap(guide.Width, guide.Height);
                        guide.DrawToBitmap(image, guide.ClientRectangle);
                        image.Save(Path.Combine(args[1], "ui-Guide.png"));
                        guide.Close();
                    }
                    form.ShowChangelog("Changes since v0.1.15",
                        [new ChangelogRelease(new Version(0, 1, 16), "v0.1.16",
                            "## Changes\n- **Compact** utility layout.\n- `F8` pauses tracking.\n## Commit history\n- Internal commit detail.\n[Full changelog](https://github.com/Database-Clarity/d2-ability-recharge-tracker/compare/v0.1.15...v0.1.16)",
                            new Uri("https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/tag/v0.1.16"))]);
                    var changelog = Descendants(form).OfType<ChangelogView>().Single();
                    Assert(changelog.FindForm() == form, "Changelog must be hosted inside the main app.");
                    var notes = Descendants(changelog).OfType<RichTextBox>().Single();
                    var commitHistoryToggle = Descendants(changelog).OfType<Button>().Single(button => button.Name == "ToggleCommitHistory");
                    Assert(notes.Text.Contains("Compact") && !notes.Text.Contains("Internal commit detail") &&
                        !notes.Text.Contains("Full changelog"),
                        "The changelog must show user-facing changes with commit details collapsed by default.");
                    commitHistoryToggle.PerformClick();
                    Assert(notes.Text.Contains("Internal commit detail") && commitHistoryToggle.Text == "Hide commit history",
                        "Commit history must remain available when explicitly expanded.");
                    commitHistoryToggle.PerformClick();
                    Assert(!notes.Text.Contains("Internal commit detail") && commitHistoryToggle.Text == "Show commit history",
                        "Commit history must collapse again without hiding user-facing changes.");
                    Assert(!notes.Text.Contains("**") && !notes.Text.Contains("##") && !notes.Text.Contains("]("),
                        "Release headings, emphasis, and links must not display raw Markdown syntax.");
                    notes.Select(0, 1);
                    Assert(notes.SelectionFont is { Bold: true } releaseFont && releaseFont.Name == "Segoe UI",
                        "Release headings must retain the app's typography and emphasis.");
                    notes.Select(0, 0);
                    using (var image = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(args[1], "ui-Changelog.png"));
                    }
                    Descendants(changelog).OfType<Button>().Single(button => button.Text == "Back").PerformClick();
                    Assert(!Descendants(form).OfType<ChangelogView>().Any(),
                        "Back should restore the app without leaving an extra changelog window.");
                    using var hud = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png"));
                    var source = new Rectangle(0, 0, hud.Width, hud.Height);
                    var regions = AbilitySlotRegionFinder.Find(hud, source);
                    using var multiPicker = new RegionPicker(hud, source, new Rectangle(0, 0, 1080, 900),
                        "test HUD", initialSelections: regions);
                    multiPicker.Show(form);
                    var clusterOnScreen = multiPicker.SelectionClusterOnScreen;
                    Assert(clusterOnScreen.Width >= 1080 * 0.5f && clusterOnScreen.Width <= 1080 * 0.8f &&
                        clusterOnScreen.Left >= 0 && clusterOnScreen.Right <= 1080,
                        "Auto-selection must open zoomed around the entire three-slot cluster with comfortable margins.");
                    using (var image = new Bitmap(multiPicker.Width, multiPicker.Height))
                    {
                        multiPicker.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(args[1], "ui-Auto-detect.png"));
                    }
                    var confirm = Descendants(multiPicker).OfType<Button>().Single(button => button.Text == "Use all three");
                    Assert(confirm.Enabled, "Three separate valid regions should be confirmable.");
                    confirm.PerformClick();
                    Assert(multiPicker.DialogResult == DialogResult.OK && multiPicker.SelectedRegions.SequenceEqual(regions),
                        "A single confirmation must return all three regions together.");
                    using var fullFrame = new Bitmap(3840, 2160);
                    using (var graphics = Graphics.FromImage(fullFrame)) graphics.DrawImageUnscaled(hud, 100, 1600);
                    var fullRegions = regions.Select(region => new Rectangle(region.X + 100, region.Y + 1600, region.Width, region.Height)).ToArray();
                    using var fullPicker = new RegionPicker(fullFrame, new Rectangle(0, 0, 3840, 2160),
                        new Rectangle(0, 0, 1920, 1080), "full gameplay frame", initialSelections: fullRegions);
                    fullPicker.Show(form);
                    var fullCluster = fullPicker.SelectionClusterOnScreen;
                    Assert(fullCluster.Width >= 960 && fullCluster.Width <= 1536 &&
                        fullCluster.Left >= 0 && fullCluster.Right <= 1920 &&
                        fullCluster.Top >= 110 && fullCluster.Bottom <= 1028,
                        $"A small HUD within a full 4K frame must auto-zoom into a large, entirely visible three-slot cluster; got {fullCluster}.");
                    Descendants(fullPicker).OfType<Button>().Single(button => button.Text == "Use all three").PerformClick();
                    Assert(fullPicker.SelectedRegions.SequenceEqual(fullRegions),
                        "Zoom must preserve original full-frame selection coordinates.");
                    var pendingCheck = new TaskCompletionSource<UpdateRelease?>();
                    var startupCheck = form.CheckForUpdateAsync(interactive: false, _ => pendingCheck.Task);
                    Assert(!updateButton.Enabled && updateButton.Text == "Checking…" && !updatePill.Visible,
                        "Startup update checks must report their pending state in Settings without cluttering the main screen.");
                    pendingCheck.SetResult(new UpdateRelease(new Version(0, 1, 99), "v0.1.99", "setup.exe",
                        new Uri("https://github.com/example/setup.exe"), 1, null,
                        new Uri("https://github.com/example/releases")));
                    startupCheck.GetAwaiter().GetResult();
                    Assert(updateButton.Enabled && updateButton.Text == "Update to v0.1.99…" &&
                        updateButton.ForeColor == TrackerTheme.Accent && form.CurrentUpdatePhase == UpdatePhase.Available,
                        "A background update discovery must highlight the Settings update action.");
                    navigation.Controls.OfType<Button>().Single(button => button.Text == "Tracking").PerformClick();
                    form.PerformLayout();
                    var capturePill = Descendants(form).OfType<StatusPill>().Single(pill => pill.Name == "CaptureStatus");
                    var updatePillBounds = updatePill.RectangleToScreen(updatePill.ClientRectangle);
                    var capturePillBounds = capturePill.RectangleToScreen(capturePill.ClientRectangle);
                    var lastTab = navigation.Controls.Cast<Control>().Last();
                    Assert(updatePill.Visible && updatePill.Enabled && updatePill.Text == "Update to v0.1.99" &&
                        lastTab.RectangleToScreen(lastTab.ClientRectangle).Right <= updatePillBounds.Left &&
                        updatePillBounds.Right <= capturePillBounds.Left &&
                        form.RectangleToScreen(form.ClientRectangle).Contains(capturePillBounds),
                        "An available update must appear as an accent pill beside the capture status on every page.");
                    using (var image = new Bitmap(form.Width, form.Height))
                    {
                        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                        image.Save(Path.Combine(args[1], "ui-Update-available.png"));
                    }
                    form.CheckForUpdateAsync(interactive: false,
                        _ => Task.FromException<UpdateRelease?>(new HttpRequestException("Test network failure"))).GetAwaiter().GetResult();
                    Assert(updateButton.Enabled && updateButton.Text == "Retry update check" &&
                        updateButton.ForeColor == TrackerTheme.Orange && !updatePill.Visible,
                        "Background update failures must stay in Settings, not look like success, and drop a stale update pill.");
                    form.CheckForUpdateAsync(interactive: false, _ => Task.FromResult<UpdateRelease?>(null)).GetAwaiter().GetResult();
                    Assert(updateButton.Text == "Check for updates" && updateButton.ForeColor == TrackerTheme.Text && !updatePill.Visible,
                        "A successful retry with no newer release must clear the error indicator.");
                }
                catch (Exception exception)
                {
                    failure = exception;
                }
                finally
                {
                    form.Close();
                }
            }));
            Application.Run(form);
        }
        catch (Exception exception)
        {
            failure = exception;
        }
    });
    thread.IsBackground = true;
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    Assert(thread.Join(TimeSpan.FromSeconds(30)), "UI smoke check must finish within 30 seconds.");
    File.Delete(portableFlag);
    if (failure is not null)
    {
        throw new InvalidOperationException("UI smoke check failed.", failure);
    }

    Console.WriteLine("Compact UI smoke checks passed.");
    return;
}

if (args.Length > 0 && args[0] is "--replay-hud" or "--extract-hud-fixtures")
{
    HudReplay.Run(args);
    return;
}

if (args.Length > 0 && args[0] == "--replay")
{
    FootageReplay.Run(args);
    return;
}

var start = DateTime.UnixEpoch;
if (args.Length == 1 && args[0] == "--replay-hud-signals")
{
    HudSequences.Run(report: true);
    return;
}

HudSequences.Run();
ChangelogTests.Run();
MeasurementHistoryTests.Run();
RepeatSummaryTests.Run();
TrackerStoragePathsTests.Run();
using (var pathFont = new Font("Segoe UI", 9))
{
    const string path = @"C:\Users\Somebody\Documents\D2AbilityTracker\HUD recordings\latest-hud.zip";
    Assert(PathLinkLabel.CompactPath(path, pathFont, 2000) == path,
        "Paths that fit should be displayed unchanged.");
    foreach (var width in new[] { 0, 10, 30, 80, 120, 240, 400 })
    {
        var compact = PathLinkLabel.CompactPath(path, pathFont, width);
        Assert(compact.Length == 0 || TextRenderer.MeasureText(compact, pathFont, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= width,
            "Compacted paths must fit their available pixel width.");
        if (width >= 240) Assert(compact.EndsWith("latest-hud.zip"),
            "Path compaction should preserve the filename whenever it fits.");
    }
    var longName = PathLinkLabel.CompactPath(@"C:\Recordings\a-recording-with-an-unusually-long-filename.zip", pathFont, 150);
    Assert(longName.Contains('…') && longName.EndsWith(".zip"),
        "A filename that is itself too long should retain its extension.");
    using var pathLink = new PathLinkLabel { Width = 150, FullPath = path };
    var narrowText = pathLink.Text;
    pathLink.Width = 2000;
    Assert(pathLink.Text == path && narrowText != path,
        "Path truncation must recalculate when available width changes.");
}
var ability = AbilityKind.Grenade;
var useBinding = new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.Control | InputModifiers.Shift);
var session = new RepeatTestSession();
session.Start(ability, 2, TimeSpan.FromSeconds(1), useBinding);
Assert(session.Phase == RepeatTestPhase.AwaitingInitialUse, "Test should wait for the user to begin the first recharge.");

session.RecordRecharge(ability, TimeSpan.FromSeconds(20), start);
Assert(session.CompletedCycles == 0, "A recharge must not count before the initial ability use.");
session.ObserveState(ability, AbilityState.Recharging, start.AddSeconds(1));
session.RecordRecharge(ability, TimeSpan.FromSeconds(10), start.AddSeconds(11));
Assert(session.Phase == RepeatTestPhase.WaitingForNextUse, "The first recharge should schedule another use.");
Assert(!session.TryGetDueUse(start.AddSeconds(12), false, out _), "The test must wait while Destiny 2 is not foreground.");
Assert(session.TryGetDueUse(start.AddSeconds(12), true, out var scheduledBinding), "The due input should be sent while Destiny 2 is foreground.");
Assert(scheduledBinding == useBinding, "The configured key and modifiers should be preserved.");

session.ObserveState(ability, AbilityState.Recharging, start.AddSeconds(13));
Assert(session.Phase == RepeatTestPhase.WaitingForRecharge, "The next cooldown should follow the automatic ability input.");
session.RecordRecharge(ability, TimeSpan.FromSeconds(12), start.AddSeconds(25));
Assert(session.Phase == RepeatTestPhase.Completed, "The test should finish at the requested cycle count.");
Assert(session.AverageDuration == TimeSpan.FromSeconds(11), "The final result should average all measured recharges.");
Assert(session.Status(start.AddSeconds(25)).Contains("average 11.00 s (00:11.00)", StringComparison.Ordinal), "The summary should show the mean in seconds and clock format.");
Assert(DurationDisplay.Format(TimeSpan.FromMilliseconds(73456)) == "73.46 s (01:13.46)",
    "Recharge durations should include total seconds rounded to two decimals.");
Assert(DurationDisplay.Format(null) == "--.-- s (--:--.--)", "Missing durations should show placeholders for both formats.");
Assert(DurationDisplay.Format(TimeSpan.FromMilliseconds(59999)) == "60.00 s (01:00.00)", "Both formats should agree when rounding crosses a minute.");

var stopped = new RepeatTestSession();
stopped.Start(ability, 3, TimeSpan.Zero, useBinding);
stopped.Stop();
Assert(stopped.Phase == RepeatTestPhase.Stopped, "Stopping should terminate an active repeat test.");

var missedUse = new RepeatTestSession();
missedUse.Start(ability, 2, TimeSpan.Zero, useBinding);
missedUse.ObserveState(ability, AbilityState.Recharging, start);
missedUse.RecordRecharge(ability, TimeSpan.FromSeconds(10), start.AddSeconds(10));
Assert(missedUse.TryGetDueUse(start.AddSeconds(10), true, out _), "The configured reuse input should be scheduled.");
missedUse.ObserveState(ability, AbilityState.Ready, start.AddSeconds(19));
Assert(missedUse.Phase == RepeatTestPhase.Failed, "A missing cooldown after automatic use should fail clearly.");

var tracker = new AbilitySlotTracker(ability);
var empty = new AbilitySignal(false, false, 0, 0);
var line = new AbilitySignal(false, true, 0, 0.21f);
var charged = new AbilitySignal(true, false, 0.8f, 0);
tracker.Observe(charged, start);
var flash = new AbilitySignal(false, false, 0, 0, 0.04f);
tracker.Observe(flash, start.AddSeconds(1));
Assert(tracker.State == AbilityState.Recharging, "One bottom-border flash should start timing immediately.");
tracker.Observe(line, start.AddSeconds(1).AddMilliseconds(33));
tracker.Observe(empty, start.AddSeconds(4));
Assert(tracker.Observe(charged, start.AddSeconds(4).AddMilliseconds(33)), "The ready fill should confirm the recharge end.");
Assert(tracker.LastDuration == TimeSpan.FromSeconds(3), "Duration should end at the first blank-frame timestamp.");
Assert(tracker.IsCharged, "The HUD charged state should be available for the automatic-use safety guard.");
tracker.Observe(empty, start.AddSeconds(5));
Assert(!tracker.IsCharged, "The charged-state guard should clear when the HUD fill disappears.");

var wrongLineTracker = new AbilitySlotTracker(ability);
wrongLineTracker.Observe(charged, start);
wrongLineTracker.Observe(line, start.AddSeconds(1));
wrongLineTracker.Observe(line, start.AddSeconds(2));
Assert(wrongLineTracker.State != AbilityState.Recharging,
    "A line inside the square must not start timing without the bottom-border flash.");
var earlyFlashTracker = new AbilitySlotTracker(ability);
earlyFlashTracker.Observe(charged, start);
earlyFlashTracker.Observe(flash with { Charged = true }, start.AddSeconds(1));
earlyFlashTracker.Observe(charged, start.AddSeconds(1).AddMilliseconds(10));
Assert(earlyFlashTracker.LastDuration is null, "Ready fill immediately after a start flash is not a completed cooldown.");
var dropoutTracker = new AbilitySlotTracker(AbilityKind.Melee);
dropoutTracker.Observe(charged, start);
dropoutTracker.Observe(flash, start.AddSeconds(1));
dropoutTracker.Observe(line, start.AddSeconds(2));
dropoutTracker.Observe(empty, start.AddSeconds(3));
dropoutTracker.Observe(line, start.AddSeconds(4));
dropoutTracker.Observe(empty, start.AddSeconds(7));
Assert(dropoutTracker.Observe(charged, start.AddSeconds(7).AddMilliseconds(10)) &&
    dropoutTracker.LastDuration == TimeSpan.FromSeconds(6),
    "A temporary internal-line dropout must not replace the later blank completion endpoint.");

foreach (var size in new[] { 40, 80, 160 })
{
    var normalBorder = ReadSyntheticSlot(size, false);
    var thickBorder = ReadSyntheticSlot(size, true);
    var interiorLine = ReadSyntheticSlot(size, false, true);
    Assert(interiorLine.BottomBorderThickness == normalBorder.BottomBorderThickness,
        "An interior horizontal line must not count toward bottom-border thickness.");
    Assert(thickBorder.BottomBorderThickness > normalBorder.BottomBorderThickness + 0.012f,
        $"The bottom-border thickening must be detected at {size}px, including pixels below the selected outline.");
    var pixelTracker = new AbilitySlotTracker(ability);
    pixelTracker.Observe(normalBorder with { Charged = true }, start);
    pixelTracker.Observe(thickBorder, start.AddSeconds(1));
    Assert(pixelTracker.State != AbilityState.Recharging,
        "Border thickening alone in pixel footage must not mistake a bright scene edge for ability use.");
    var progressSignal = ReadSyntheticProgress(size, 0.6f);
    Assert(progressSignal.ChargeProgress is { } observed && Math.Abs(observed - 0.6f) < 0.08f,
        "Matching side-strip boundaries should expose resolution-scaled charge progress.");
}

var resilient = new AbilitySlotTracker(AbilityKind.ClassAbility);
resilient.Observe(charged, start);
resilient.Observe(flash, start.AddSeconds(1));
resilient.Observe(line with { ChargeProgress = 0.3f }, start.AddSeconds(4));
resilient.Observe(line with { ChargeProgress = 0.4f }, start.AddSeconds(5));
resilient.Observe(empty, start.AddSeconds(6));
Assert(resilient.TrackingWarning is not null && resilient.CurrentDuration == TimeSpan.FromSeconds(5),
    "Losing contrast mid-charge must keep the original timer and expose recovery status.");
Assert(!resilient.Observe(charged, start.AddSeconds(6).AddMilliseconds(10)) && resilient.MeasurementDiscarded,
    "A charged-looking frame during recovery must not manufacture an endpoint.");

var recovered = new AbilitySlotTracker(AbilityKind.Melee);
recovered.Observe(charged, start);
recovered.Observe(flash, start.AddSeconds(1));
recovered.Observe(line with { ChargeProgress = 0.3f }, start.AddSeconds(4));
recovered.Observe(line with { ChargeProgress = 0.4f }, start.AddSeconds(5));
recovered.Observe(empty, start.AddSeconds(6));
recovered.Observe(line with { ChargeProgress = 0.65f }, start.AddSeconds(8));
Assert(recovered.TrackingWarning is null && recovered.CurrentDuration == TimeSpan.FromSeconds(7),
    "Recovered forward progress must resume the same measurement without resetting its start.");
recovered.Observe(line with { ChargeProgress = 0.95f }, start.AddSeconds(10));
recovered.Observe(empty, start.AddSeconds(10).AddMilliseconds(100));
Assert(recovered.Observe(charged, start.AddSeconds(10).AddMilliseconds(150)) &&
    recovered.LastDuration == TimeSpan.FromSeconds(9.1),
    "A visible endpoint after recovery must retain the original start timestamp.");
var calibrated = new AbilitySlotTracker(ability);
var greenReady = charged with { FillHue = 127.5f, FillColor = Color.FromArgb(60, 160, 80), PixelEvidence = true };
calibrated.Observe(greenReady, start);
calibrated.Observe(greenReady, start.AddMilliseconds(200));
calibrated.Observe(greenReady with { BottomBorderThickness = 0.2f }, start.AddMilliseconds(300));
Assert(calibrated.State == AbilityState.Ready,
    "A bright scene edge under an already charged pixel slot must not start another cycle.");
calibrated.Observe(empty with { UseFlash = true, PixelEvidence = true }, start.AddSeconds(1));
Assert(calibrated.State == AbilityState.Consumed && calibrated.CurrentDuration is null,
    "The use animation must arm start detection without including its animation delay in recharge timing.");
calibrated.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.01f },
    start.AddSeconds(1).AddMilliseconds(100));
calibrated.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.04f },
    start.AddSeconds(1).AddMilliseconds(500));
Assert(calibrated.State == AbilityState.Recharging && calibrated.CurrentDuration == TimeSpan.Zero,
    "The later consumed-slot border pulse must establish the actual recharge start.");
calibrated.Observe(line with { ChargeProgress = 0.4f, PixelEvidence = true }, start.AddSeconds(4));
calibrated.Observe(greenReady with { FillHue = 247.5f, FillColor = Color.FromArgb(80, 80, 180) }, start.AddSeconds(5));
Assert(!calibrated.IsCharged && calibrated.State == AbilityState.Recharging && !calibrated.MeasurementDiscarded,
    "A saturated scene of another color must not become the slot's charged fill or discard its timer.");
Assert(calibrated.ProgressSearchTolerance(start.AddSeconds(8)) > 0.12f,
    "Recovery must expand the boundary search using elapsed time and observed charge rate.");
calibrated.Observe(line with { ChargeProgress = 0.95f, PixelEvidence = true }, start.AddSeconds(10));
calibrated.Observe(greenReady, start.AddSeconds(10).AddMilliseconds(100));
Assert(!calibrated.IsCharged, "Automatic ability use must wait for confirmed ready fill.");
Assert(calibrated.Observe(greenReady, start.AddSeconds(10).AddMilliseconds(300)) &&
    calibrated.LastDuration == TimeSpan.FromSeconds(8.6),
    "Debouncing the returning color must preserve its first endpoint timestamp.");
var contrastEndpoint = new AbilitySlotTracker(ability);
contrastEndpoint.Observe(greenReady, start);
contrastEndpoint.Observe(empty with { UseFlash = true, PixelEvidence = true }, start.AddSeconds(1));
contrastEndpoint.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.01f }, start.AddSeconds(1.1));
contrastEndpoint.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.04f }, start.AddSeconds(1.5));
contrastEndpoint.Observe(line with { PixelEvidence = true, ChargeProgress = 0.8f, FillLuminance = 0.5f }, start.AddSeconds(8));
contrastEndpoint.Observe(line with { PixelEvidence = true, ChargeProgress = 0.95f, FillLuminance = 0.5f }, start.AddSeconds(10));
contrastEndpoint.Observe(empty with { PixelEvidence = true, FillLuminance = 0.25f }, start.AddSeconds(10.1));
Assert(contrastEndpoint.LastDuration is null, "A luminance drop alone must not complete a measurement.");
contrastEndpoint.Observe(greenReady, start.AddSeconds(10.2));
Assert(contrastEndpoint.Observe(greenReady, start.AddSeconds(10.4)) &&
    contrastEndpoint.LastDuration is { } contrastDuration && Math.Abs(contrastDuration.TotalSeconds - 8.6) < 0.000001,
    "A relative fill disappearance on a bright scene must preserve the blank endpoint, confirmed by ready color.");
Assert(MainForm.MapRegion(new Rectangle(100, 100, 40, 40), new Rectangle(0, 0, 1000, 1000),
    new Rectangle(200, 300, 500, 500)) == new Rectangle(250, 350, 20, 20),
    "Window-relative regions should follow movement and resizing.");

var model = new RechargeModel();
var noise = new Random(7);
for (var sample = 0; sample < 200; sample++)
{
    var seconds = sample * 0.05;
    var outlier = sample % 23 == 5 ? 0.3 : 0;
    model.Add(seconds, 0.01 + seconds / 50 + (noise.NextDouble() - 0.5) * 0.004 + outlier);
}
Assert(model.IsReady && model.SecondsAt(1) is { } modelEnd && Math.Abs(modelEnd - 49.5) < 0.6 &&
    model.SecondsUncertaintyAt(1) is { } modelSpread && modelSpread > 0 && modelSpread < 2 &&
    Math.Abs(modelEnd - 49.5) < 3 * modelSpread + 0.05,
    "The robust model should reject outliers and predict the end within its stated uncertainty.");
model.Reset();
Assert(!model.IsReady && model.SecondsAt(1) is null, "Resetting the model should clear the prediction.");
for (var sample = 0; sample < 40; sample++)
{
    model.Add(sample * 0.1, 0.1);
}
Assert(model.SecondsAt(1) is null, "Stationary progress must not produce a finish estimate.");
var centralRegression = new RechargeModel();
var shiftedRegression = new RechargeModel();
for (var sample = 0; sample <= 1000; sample++)
{
    var progress = sample / 1000d;
    var seconds = 7 + progress * 100;
    if (progress < 0.1)
    {
        seconds -= 5 * (0.1 - progress);
    }
    else if (progress > 0.9)
    {
        seconds += 80 * (progress - 0.9);
    }

    centralRegression.Add(seconds, progress);
    shiftedRegression.Add(seconds + 50, progress);
}
Assert(centralRegression.FinalRegression() is { } finalRegression &&
    Math.Abs(finalRegression.Duration.TotalSeconds - 100) < 0.001 &&
    finalRegression.Uncertainty > TimeSpan.Zero,
    "Final regression must extrapolate the 10%-90% slope, excluding nonlinear edges and event offsets.");
Assert(shiftedRegression.FinalRegression() is { } shiftedFinal &&
    Math.Abs(shiftedFinal.Duration.TotalSeconds - 100) < 0.001,
    "Moving the observed start must not change the full-charge regression duration.");
var incompleteRegression = new RechargeModel();
for (var sample = 0; sample <= 85; sample++)
{
    incompleteRegression.Add(sample, sample / 100d);
}
Assert(incompleteRegression.FinalRegression() is null, "A final 10%-90% fit must wait until 90% is observed.");
incompleteRegression.Reset();
for (var sample = 20; sample <= 95; sample++)
{
    incompleteRegression.Add(sample, sample / 100d);
}
Assert(incompleteRegression.FinalRegression() is null, "A cycle missing the 10% boundary must not report a full-interval fit.");
centralRegression.Reset();
Assert(centralRegression.FinalRegression() is null, "Resetting a cycle must also clear the final regression.");
var finalTracker = new AbilitySlotTracker(ability);
finalTracker.Observe(charged, start);
finalTracker.Observe(flash, start.AddSeconds(1));
for (var sample = 1; sample <= 96; sample++)
{
    finalTracker.Observe(line with { ChargeProgress = sample / 100f }, start.AddSeconds(1 + sample / 10d));
}
finalTracker.Observe(empty, start.AddSeconds(11));
finalTracker.Observe(charged, start.AddSeconds(11.2));
Assert(finalTracker.LastRegressionDuration is { } completedFit &&
    Math.Abs(completedFit.TotalSeconds - 10) < 0.001 && finalTracker.LastRegressionUncertainty > TimeSpan.Zero,
    "Completion must retain the 10%-90% regression before clearing the live model.");
Assert(MainForm.PausedMeasurementStatus(finalTracker).Contains("Complete") &&
    MainForm.PausedMeasurementStatus(finalTracker).Contains("10.00") &&
    MainForm.PausedMeasurementStatus(finalTracker).Contains("regression"),
    "Paused status must preserve the completed measurement and regression.");
Assert(MainForm.MeasurementStatus(finalTracker, true).Split('\n').Length == 2 &&
    MainForm.PausedMeasurementStatus(finalTracker).Split('\n').Length == 2,
    "Completed and paused ability status must have exactly two rows.");
Assert(MainForm.WithShortcut("Pause detection", InputBinding.DefaultPause).Contains(InputBinding.DefaultPause.ToString()),
    "Action labels must include their configured shortcut.");
var estimatingTracker = new AbilitySlotTracker(ability);
estimatingTracker.Observe(charged, start);
estimatingTracker.Observe(flash, start.AddSeconds(1));
for (var sample = 1; sample <= 40; sample++)
{
    estimatingTracker.Observe(line with { ChargeProgress = sample * 0.01f },
        start.AddSeconds(1 + sample * 0.1));
}
Assert(estimatingTracker.EstimatedDuration is { } trackerEstimate && Math.Abs(trackerEstimate.TotalSeconds - 10) < 0.1 &&
    estimatingTracker.EstimateUncertainty is { } trackerSpread && trackerSpread < TimeSpan.FromSeconds(1),
    "The active tracker should expose its fitted estimate with an uncertainty.");
Assert(MainForm.PausedMeasurementStatus(estimatingTracker).Contains("Recharging") &&
    MainForm.PausedMeasurementStatus(estimatingTracker).Contains("stopped"),
    "An interrupted recharge must be identified as stopped, not a completed measurement.");
estimatingTracker.MarkSignalLost(start.AddSeconds(6));
Assert(estimatingTracker.EstimatedDuration is not null && estimatingTracker.CurrentDuration == TimeSpan.FromSeconds(5),
    "Signal loss should retain elapsed time and the whole-cycle estimate for fast recovery.");
Assert(estimatingTracker.Observe(line with { ChargeProgress = 0.55f }, start.AddSeconds(6.5)) == false &&
    estimatingTracker.TrackingWarning is null,
    "A single consistent progress sample should end recovery.");
Assert(CaptureSchedule.NextFrame(0, 10, 120, 1000) == 40, "Slow capture must get three times its work duration as rest.");
Assert(CaptureSchedule.NextFrame(100, 101, 120, 1000) == 108, "Fast captures should respect the configured target.");
Assert(CaptureSchedule.NextFrame(100, 150, 240, 1000) == 300, "Overruns must not cause a burst of catch-up captures.");
Assert(!MainForm.CaptureIsRecent(1000, 0, 1000) &&
    MainForm.CaptureIsRecent(2999, 1000, 1000) &&
    !MainForm.CaptureIsRecent(3000, 1000, 1000),
    "Capture activity must expire after two seconds and never infer activity before the first frame.");
Assert(MainForm.UnavailableMeasurementStatus(estimatingTracker) == "No capture frames\nWaiting for screen capture",
    "Unavailable capture must not imply a live recharge or completed measurement.");
Assert(MainForm.UnavailableMeasurementStatus(finalTracker).Contains("Last 10.00") &&
    MainForm.UnavailableMeasurementStatus(finalTracker).Contains("regression") &&
    MainForm.UnavailableMeasurementStatus(finalTracker).Split('\n').Length == 2,
    "Unavailable capture must retain the last completed result and regression in two rows.");
Assert(MainForm.UnavailableMeasurementStatus(finalTracker, "Waiting for game", string.Empty) is var waitingRow &&
    waitingRow.StartsWith("Waiting for game\nLast 10.00", StringComparison.Ordinal) &&
    MainForm.UnavailableMeasurementStatus(estimatingTracker, "Waiting for game", string.Empty) == "Waiting for game\n",
    "Waiting rows must name the reason first and keep the last result on the second line.");
AbilityKind[] allCaptured = [AbilityKind.Grenade, AbilityKind.Melee, AbilityKind.ClassAbility];
var gameMissing = MainForm.RouteCaptureErrors(
    allCaptured.ToDictionary(kind => kind, _ => MainForm.GameWindowUnavailableError), allCaptured);
Assert(gameMissing.WaitingForGame && gameMissing.SharedError is null && gameMissing.RowErrors.Count == 0,
    "A missing Destiny 2 window must be routed to the banner instead of repeating on every row.");
Assert(MainForm.RouteCaptureErrors(new Dictionary<AbilityKind, string> { [AbilityKind.Melee] = MainForm.GameWindowUnavailableError },
        [AbilityKind.Melee]).WaitingForGame,
    "A missing game window is global even when only one ability is tracked.");
var sharedFailure = MainForm.RouteCaptureErrors(allCaptured.ToDictionary(kind => kind, _ => "Access denied"), allCaptured);
Assert(sharedFailure.SharedError == "Access denied" && sharedFailure.RowErrors.Count == 0 && !sharedFailure.WaitingForGame,
    "One identical error on every captured slot must be shown once in the banner.");
var distinctFailures = MainForm.RouteCaptureErrors(new Dictionary<AbilityKind, string>
{
    [AbilityKind.Grenade] = "Area is outside the display",
    [AbilityKind.Melee] = "Another failure"
}, allCaptured);
Assert(distinctFailures.SharedError is null && distinctFailures.RowErrors.Count == 2 &&
    distinctFailures.RowErrors[AbilityKind.Grenade] == "Area is outside the display",
    "Distinct per-slot failures must stay on their own rows.");
var partialFailure = MainForm.RouteCaptureErrors(new Dictionary<AbilityKind, string>
{
    [AbilityKind.Grenade] = "Access denied",
    [AbilityKind.Melee] = "Access denied"
}, allCaptured);
Assert(partialFailure.SharedError is null && partialFailure.RowErrors.Count == 2,
    "An error is only global when every captured slot shares it.");
Assert(MainForm.RouteCaptureErrors(new Dictionary<AbilityKind, string> { [AbilityKind.ClassAbility] = "Area is outside the display" },
        [AbilityKind.ClassAbility]).RowErrors.ContainsKey(AbilityKind.ClassAbility),
    "A single tracked slot's own failure must remain a row error.");
var mixedFailure = MainForm.RouteCaptureErrors(new Dictionary<AbilityKind, string>
{
    [AbilityKind.Grenade] = MainForm.GameWindowUnavailableError,
    [AbilityKind.ClassAbility] = "Area is outside the display"
}, allCaptured);
Assert(mixedFailure.WaitingForGame && mixedFailure.RowErrors.Keys.SequenceEqual([AbilityKind.ClassAbility]),
    "A missing game window must not hide another slot's distinct failure.");
TrackingStatusSnapshot StatusSnapshot(CaptureErrorRouting routing, bool monitoring = true, bool stalled = false,
    string? loopError = null, string? inputError = null, bool hasRegions = true, double rate = 118) =>
    new(monitoring, InputBinding.DefaultPause, loopError, inputError, hasRegions, routing, stalled, "DXGI", rate, 120);
var healthy = MainForm.RouteCaptureErrors(new Dictionary<AbilityKind, string>(), allCaptured);
var waitingStatus = MainForm.DescribeTrackingStatus(StatusSnapshot(gameMissing, stalled: true));
Assert(waitingStatus.Banner is { Tone: StatusTone.Warning } waitingBanner && waitingBanner.Text == MainForm.WaitingForGameBanner &&
    waitingStatus.Pill == new StatusMessage("Waiting for game", StatusTone.Warning),
    "Waiting for the game must be an amber banner and pill, not a red failure.");
var sharedStatus = MainForm.DescribeTrackingStatus(StatusSnapshot(sharedFailure));
Assert(sharedStatus.Banner is { Tone: StatusTone.Error } sharedBanner && sharedBanner.Text.Contains("Access denied") &&
    sharedStatus.Pill.Tone == StatusTone.Error,
    "A shared capture failure must be reported once in red.");
Assert(MainForm.DescribeTrackingStatus(StatusSnapshot(healthy, loopError: "Device lost")) is
{ Banner: { Tone: StatusTone.Error, Text: "Capture stopped: Device lost" } },
    "A crashed capture loop must be reported as stopped.");
Assert(MainForm.DescribeTrackingStatus(StatusSnapshot(healthy, stalled: true)) is
{ Banner: { Tone: StatusTone.Warning, Text: "No capture frames · DXGI" }, Pill.Tone: StatusTone.Warning },
    "Missing capture frames must name the backend in an amber banner.");
var detectingStatus = MainForm.DescribeTrackingStatus(StatusSnapshot(healthy));
Assert(detectingStatus.Banner is null && detectingStatus.Pill == new StatusMessage("Detecting · DXGI · 118/120 Hz", StatusTone.Good),
    "Healthy detection must show only the green pill with backend and rate.");
var pausedStatus = MainForm.DescribeTrackingStatus(StatusSnapshot(gameMissing, monitoring: false));
Assert(pausedStatus.Banner is null && pausedStatus.Pill.Tone == StatusTone.Neutral &&
    pausedStatus.Pill.Text == $"Paused · {InputBinding.DefaultPause} to resume",
    "Paused must be neutral, name the actual resume shortcut, and hide stale capture errors.");
Assert(MainForm.DescribeTrackingStatus(StatusSnapshot(healthy, inputError: "Hook failed")) is
{ Banner: { Tone: StatusTone.Warning } inputBanner, Pill.Tone: StatusTone.Good } && inputBanner.Text.Contains("Hook failed"),
    "Unavailable input hooks must be an amber one-line banner while detection continues.");
Assert(MainForm.DescribeTrackingStatus(StatusSnapshot(healthy, hasRegions: false) with { AnyRegions = false }) is
{ Banner: null, Pill: { Tone: StatusTone.Neutral, Text: "Areas not set" } },
    "An unconfigured tracker must leave setup to the first-run card and not report a failure.");
Assert(MainForm.DescribeTrackingStatus(StatusSnapshot(healthy, hasRegions: false)) is
{ Banner.Tone: StatusTone.Neutral, Pill.Tone: StatusTone.Neutral },
    "A tracked ability without an area must be explained neutrally.");

var repeatShortcut = new InputBinding(InputDevice.Keyboard, 0x79, InputModifiers.Alt);
var settings = new TrackerSettings
{
    PauseBinding = useBinding,
    AbilityUseBindings = new()
    {
        [AbilityKind.Grenade] = new InputBinding(InputDevice.Mouse, MouseInputCode.X2, InputModifiers.Shift),
        [AbilityKind.Melee] = new InputBinding(InputDevice.Keyboard, 0x42, InputModifiers.None)
    },
    RepeatTestBinding = repeatShortcut,
    SoloBindings = new() { [AbilityKind.Melee] = useBinding },
    SoloAbility = AbilityKind.Melee,
    OverlayShortcutCleared = true,
    CaptureRate = 60
    ,
    TestHoldSeconds = 1.75m
};
var restoredSettings = JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(settings));
Assert(restoredSettings?.PauseBinding == useBinding, "Modified pause bindings should round-trip in settings.");
Assert(restoredSettings?.AbilityUseBindings.Count == 2 &&
    restoredSettings.AbilityUseBindings[AbilityKind.Grenade] == settings.AbilityUseBindings[AbilityKind.Grenade] &&
    restoredSettings.AbilityUseBindings[AbilityKind.Melee] == settings.AbilityUseBindings[AbilityKind.Melee],
    "Ability-use inputs must round-trip independently per ability.");
var legacyAbilityInput = new InputBinding(InputDevice.Mouse, MouseInputCode.X2, InputModifiers.Shift);
var migratedAbilitySettings = JsonSerializer.Deserialize<TrackerSettings>(
    JsonSerializer.Serialize(new TrackerSettings { AbilityUseBinding = legacyAbilityInput }))!;
migratedAbilitySettings.MigrateAbilityUseBinding();
Assert(migratedAbilitySettings.AbilityUseBindings.Count == Enum.GetValues<AbilityKind>().Length &&
    Enum.GetValues<AbilityKind>().All(ability => migratedAbilitySettings.AbilityUseBindings[ability] == legacyAbilityInput),
    "Existing installs with one shared ability input must migrate it to each ability without losing behavior.");
Assert(restoredSettings?.RepeatTestBinding == repeatShortcut && restoredSettings.SoloBindings[AbilityKind.Melee] == useBinding &&
    restoredSettings.SoloAbility == AbilityKind.Melee && restoredSettings.OverlayShortcutCleared,
    "Task shortcuts should round-trip in settings.");
Assert(restoredSettings?.CaptureRate == 60, "The configured capture target should persist.");
Assert(restoredSettings?.TestHoldSeconds == 1.75m &&
    JsonSerializer.Deserialize<TrackerSettings>("{}")!.TestHoldSeconds == 0.12m,
    "Hold time must persist and older settings must default to a 120 ms tap.");
Assert(useBinding.ToString() == "Ctrl+Shift+G", "Key names and modifiers should be presented clearly.");
var mouseBinding = new InputBinding(InputDevice.Mouse, MouseInputCode.X1, InputModifiers.Alt);
Assert(mouseBinding.ToString() == "Alt+Mouse X1", "Mouse buttons should retain their modifier and readable label.");
Assert(new InputBinding(InputDevice.Keyboard, (int)Keys.Escape, InputModifiers.None).IsClearRequest &&
    !new InputBinding(InputDevice.Keyboard, (int)Keys.Escape, InputModifiers.Shift).IsClearRequest,
    "Plain Escape should clear a shortcut while recording it.");
Assert(Marshal.SizeOf<InputBindingSender.InputEvent>() == (IntPtr.Size == 8 ? 40 : 28), "SendInput structure must match the Windows ABI.");
InputSenderTests.Run();

var viewport = new RectangleF(0, 86, 1080, 900);
var submitSize = new Size(112, 32);
Assert(RegionPicker.SelectionSubmitLocation(new RectangleF(100, 200, 50, 50), viewport, submitSize) == new Point(164, 200),
    "Submit should sit beside the selection with clearance for its resize handles.");
Assert(RegionPicker.SelectionSubmitLocation(new RectangleF(1000, 200, 50, 50), viewport, submitSize) == new Point(874, 200),
    "Submit should switch to the left near the viewport's right edge.");
Assert(RegionPicker.SelectionSubmitLocation(new RectangleF(0, 900, 1080, 86), viewport, submitSize) == new Point(0, 854),
    "Submit should fit above a wide selection at the bottom edge.");
Assert(RegionPicker.SelectionSubmitLocation(viewport, viewport, submitSize) == new Point(0, 86),
    "Submit must stay accessible even when the selection fills the viewport.");

using (var preview = new Bitmap(40, 40))
using (var picker = new RegionPicker(preview, new Rectangle(0, 0, 40, 40), new Rectangle(0, 0, 1080, 900), "test preview"))
{
    AssertReadableButtons(picker);
}

using (var preview = new Bitmap(200, 80))
using (var overlapping = new RegionPicker(preview, new Rectangle(0, 0, 200, 80),
    new Rectangle(0, 0, 1080, 900), "test preview", initialSelections:
    [new Rectangle(10, 10, 40, 40), new Rectangle(30, 10, 40, 40), new Rectangle(90, 10, 40, 40)]))
{
    Assert(!Descendants(overlapping).OfType<Button>().Single(button => button.Text == "Use all three").Enabled,
        "Overlapping regions must not be confirmable.");
    Assert(overlapping.SelectedRegions.Length == 0, "Opening or cancelling a selector must not commit regions.");
}

using (var guide = new SelectionGuide())
using (var illustration = new Bitmap(guide.ClientSize.Width, guide.ClientSize.Height))
{
    guide.DrawToBitmap(illustration, guide.ClientRectangle);
    Assert(illustration.GetPixel(100, 100) != illustration.GetPixel(300, 100),
        "The guide should render an illustrated slot against its background.");
    AssertReadableButtons(guide);
}

using (var hudPreview = new Bitmap(581, 182))
using (var graphics = Graphics.FromImage(hudPreview))
{
    graphics.Clear(Color.FromArgb(25, 24, 32));
    using var pink = new SolidBrush(Color.FromArgb(244, 115, 170));
    for (var x = 110; x < 570; x++)
    {
        var y = (int)Math.Round(75 - (x - 110) * 0.055);
        graphics.FillRectangle(pink, x, y - 2, 1, 5);
    }

    using var outline = new Pen(Color.FromArgb(220, 220, 225), 1.5f);
    for (var index = 0; index < 3; index++)
    {
        var left = 110 + index * 67;
        var top = 83 - index * 4;
        var side = 61;
        var corners = new[]
        {
            new PointF(left, top), new PointF(left + side, top - side * 0.055f),
            new PointF(left + side, top + side - side * 0.055f), new PointF(left, top + side)
        };
        graphics.DrawPolygon(outline, corners);
    }

    var window = new Rectangle(500, 300, hudPreview.Width, hudPreview.Height);
    var suggestions = AbilitySlotRegionFinder.Find(hudPreview, window);
    Assert(suggestions.Length == 3, "The pink HUD anchor and outlined ability squares should produce three regions.");
    var expected = new[]
    {
        new Rectangle(610, 380, 61, 65), new Rectangle(677, 376, 61, 65), new Rectangle(744, 372, 61, 65)
    };
    for (var index = 0; index < expected.Length; index++)
    {
        expected[index].Inflate(4, 4);
        Assert(Math.Abs(suggestions[index].Left - expected[index].Left) <= 3 &&
            Math.Abs(suggestions[index].Top - expected[index].Top) <= 3 &&
            Math.Abs(suggestions[index].Width - expected[index].Width) <= 3 &&
            Math.Abs(suggestions[index].Height - expected[index].Height) <= 3,
            $"Synthetic slot {index} should include padding around its visible border; got {suggestions[index]}.");
        var border = Rectangle.FromLTRB(610 + index * 67, 379 - index * 4,
            672 + index * 67, 445 - index * 4);
        Assert(suggestions[index].Left <= border.Left - 2 && suggestions[index].Top <= border.Top - 2 &&
            suggestions[index].Right >= border.Right + 1 && suggestions[index].Bottom >= border.Bottom + 2,
            "The padded selection must comfortably include the top, sides, and bottom line.");
        if (index > 0) Assert(!suggestions[index].IntersectsWith(suggestions[index - 1]),
            "Padded neighboring selections must not overlap.");
    }
}

var recordingDisplayBounds = new Rectangle(100, 50, 1000, 800);
var recordingSlots = new[]
{
        new AbilitySlotSettings
        {
            Kind = AbilityKind.Grenade,
            Region = new Rectangle(300, 200, 60, 60),
            MonitorBounds = recordingDisplayBounds,
            MonitorDeviceName = "DISPLAY1"
        },
        new AbilitySlotSettings
        {
            Kind = AbilityKind.Melee,
            Region = new Rectangle(370, 195, 60, 60),
            MonitorBounds = recordingDisplayBounds,
            MonitorDeviceName = "DISPLAY1"
        },
        new AbilitySlotSettings
        {
            Kind = AbilityKind.ClassAbility,
            Region = new Rectangle(440, 190, 60, 60),
            MonitorBounds = recordingDisplayBounds,
            MonitorDeviceName = "DISPLAY1"
        }
    };
Assert(AbilityHudRecorder.GetCaptureBounds(recordingSlots) == new Rectangle(284, 174, 232, 102),
    "The debug recording should capture the tracked ability areas with a small surrounding margin.");
Assert(AbilityHudRecorder.GetCaptureBounds(recordingSlots.Where(slot => slot.Kind == AbilityKind.Melee)) == new Rectangle(354, 179, 92, 92),
    "A solo recording should capture only the tracked ability area.");
var recordingPath = Path.Combine(Path.GetTempPath(), $"ability-hud-test-{Guid.NewGuid():N}.zip");
using (var recorder = new AbilityHudRecorder(new Rectangle(0, 0, 8, 8), "DISPLAY1", recordingPath))
{
    var magenta = Enumerable.Repeat(unchecked((int)0xFFFF00FF), 64).ToArray();
    var recordingStart = DateTime.UtcNow;
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(magenta, System.Runtime.InteropServices.GCHandleType.Pinned);
    try
    {
        for (var frame = 0; frame < 6; frame++)
        {
            // 120 Hz capture ticks must be thinned to the 60 FPS recording rate.
            var timestamp = recordingStart.AddSeconds(frame / 120d);
            if (recorder.IsDue(timestamp))
            {
                recorder.Submit(handle.AddrOfPinnedObject(), 32, timestamp, "Ready,,,,,");
            }
        }
    }
    finally
    {
        handle.Free();
    }

    recorder.Stop();
    var recordingResult = recorder.Completion.GetAwaiter().GetResult();
    Assert(recordingResult.Frames == 3 && recordingResult.DroppedFrames == 0 && File.Exists(recordingPath),
        "A stopped recording should keep 60 FPS frames from the capture loop and produce a complete archive.");
}
try
{
    using var recordingArchive = ZipFile.OpenRead(recordingPath);
    var firstFrame = recordingArchive.GetEntry("frames/000001.png");
    Assert(firstFrame is not null &&
        recordingArchive.GetEntry("frames.csv") is not null &&
        recordingArchive.GetEntry("recording.txt") is not null,
        "A recording archive should include its lossless frame, timing index, and metadata.");
    using var frameStream = firstFrame!.Open();
    using var decodedFrame = new Bitmap(frameStream);
    Assert(decodedFrame.GetPixel(0, 0).ToArgb() == Color.Magenta.ToArgb(),
        "The archived PNG frame should preserve the captured HUD pixels losslessly.");
}
finally
{
    File.Delete(recordingPath);
}

const string releaseJson = """
{
  "tag_name": "v0.1.12",
  "draft": false,
  "prerelease": false,
  "html_url": "https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/tag/v0.1.12",
  "assets": [
    { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt", "size": 4 },
    {
      "name": "D2AbilityTracker-v0.1.12-Setup.exe",
      "browser_download_url": "https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/download/v0.1.12/D2AbilityTracker-v0.1.12-Setup.exe",
      "size": 1234,
      "digest": "sha256:ABCDEF"
    }
  ]
}
""";
var newerRelease = UpdateChecker.ParseLatestRelease(releaseJson, new Version(0, 1, 9));
Assert(newerRelease is { Tag: "v0.1.12", AssetName: "D2AbilityTracker-v0.1.12-Setup.exe", Size: 1234, Sha256: "abcdef" } &&
    newerRelease.Version == new Version(0, 1, 12),
    "The updater should pick the setup asset and digest from a newer GitHub release.");
Assert(UpdateChecker.ParseLatestRelease(releaseJson, new Version(0, 1, 12)) is null &&
    UpdateChecker.ParseLatestRelease(releaseJson, new Version(0, 2, 0)) is null,
    "The updater should ignore releases that are not newer than the running build.");
Assert(UpdateChecker.ParseLatestRelease(releaseJson.Replace("\"prerelease\": false", "\"prerelease\": true"), new Version(0, 1, 0)) is null &&
    UpdateChecker.ParseLatestRelease(releaseJson.Replace("D2AbilityTracker-v0.1.12-Setup.exe\",", "Other.exe\","), new Version(0, 1, 0)) is null,
    "The updater should skip prereleases and releases without the expected installer.");
Assert(UpdateChecker.TryParseTag("v0.1.10", out var parsedTag) && parsedTag > new Version(0, 1, 9) &&
    !UpdateChecker.TryParseTag("latest", out _),
    "Release tags should compare numerically rather than as text.");

using (var listener = new GlobalInputListener())
using (var hookThreadResponded = new ManualResetEventSlim())
{
    listener.Start();
    var dispatcher = typeof(GlobalInputListener)
        .GetField("_hookDispatcher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?.GetValue(listener) as System.Windows.Forms.Control
        ?? throw new InvalidOperationException("The global hooks need a dedicated message dispatcher.");
    dispatcher.BeginInvoke((Action)(() => hookThreadResponded.Set()));
    Assert(hookThreadResponded.Wait(TimeSpan.FromSeconds(2)),
        "The hook message pump must keep running while the calling/UI thread is blocked.");
}

var autoDetectFixtures = new[]
{
    "hud-2-5.png", "hud-30.png", "hud-60.png", "hud-100.png", "hud-140.png", "hud-163.png"
};
var expectedAbilityRegions = new[]
{
    new Rectangle(110, 82, 60, 63),
    new Rectangle(176, 77, 60, 63),
    new Rectangle(242, 73, 61, 63)
};
foreach (var fixtureName in autoDetectFixtures)
{
    var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
    using var fixture = new Bitmap(fixturePath);
    var detected = AbilitySlotRegionFinder.Find(fixture, new Rectangle(0, 0, fixture.Width, fixture.Height));
    Console.WriteLine($"{fixtureName}: {string.Join("; ", detected)}");
    Assert(detected.Length == expectedAbilityRegions.Length,
        $"Auto-detection should find three square borders in {fixtureName}; found {detected.Length}.");
    for (var index = 0; index < expectedAbilityRegions.Length; index++)
    {
        var expectedRegion = expectedAbilityRegions[index];
        expectedRegion.Inflate(2, 2);
        var foundRegion = detected[index];
        Assert(Math.Abs(foundRegion.Left - expectedRegion.Left) <= 3 &&
            Math.Abs(foundRegion.Top - expectedRegion.Top) <= 3 &&
            Math.Abs(foundRegion.Width - expectedRegion.Width) <= 3 &&
            Math.Abs(foundRegion.Height - expectedRegion.Height) <= 3,
            $"Auto-detection of {fixtureName} slot {index} should match the visible border within 3 px; expected {expectedRegion}, found {foundRegion}.");
    }
}

using (var yellowFixture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-solar-yellow.png")))
{
    var detected = AbilitySlotRegionFinder.Find(
        yellowFixture, new Rectangle(0, 0, yellowFixture.Width, yellowFixture.Height));
    Console.WriteLine($"hud-solar-yellow.png: {string.Join("; ", detected)}");
    Assert(detected.Length == 3, "Auto-detection should find the three slots below the yellow subclass strip.");
    var expected = new[]
    {
        new Rectangle(123, 72, 60, 64),
        new Rectangle(189, 67, 60, 64),
        new Rectangle(255, 63, 59, 63)
    };
    for (var index = 0; index < expected.Length; index++)
    {
        var found = detected[index];
        var region = expected[index];
        region.Inflate(2, 2);
        Assert(Math.Abs(found.Left - region.Left) <= 3 &&
            Math.Abs(found.Top - region.Top) <= 3 &&
            Math.Abs(found.Width - region.Width) <= 3 &&
            Math.Abs(found.Height - region.Height) <= 3,
            $"Yellow-strip slot {index} should match the visible border within 3 px; expected {region}, found {found}.");
    }
}

using (var pickerFixture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png")))
{
    var expected = new[]
    {
        new Rectangle(136, 131, 77, 80),
        new Rectangle(219, 125, 77, 80),
        new Rectangle(302, 119, 77, 80)
    };
    foreach (var fullFrame in new[] { false, true })
    {
        using var frame = new Bitmap(fullFrame ? 1920 : pickerFixture.Width, fullFrame ? 1080 : pickerFixture.Height);
        var offset = fullFrame ? new Point(40, 750) : Point.Empty;
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.FromArgb(18, 19, 24));
            graphics.DrawImageUnscaled(pickerFixture, offset);
        }

        var source = new Rectangle(500, 300, frame.Width, frame.Height);
        var detected = AbilitySlotRegionFinder.Find(frame, source);
        Console.WriteLine($"Picker screenshot (full frame: {fullFrame}): {string.Join("; ", detected)}");
        Assert(detected.Length == 3, "The dim HUD screenshot must find all three abilities, not the weapon panel.");
        for (var index = 0; index < expected.Length; index++)
        {
            var region = expected[index];
            region.Inflate(2, 2);
            region.Offset(source.X + offset.X, source.Y + offset.Y);
            var found = detected[index];
            Assert(Math.Abs(found.X - region.X) <= 3 && Math.Abs(found.Y - region.Y) <= 3 &&
                Math.Abs(found.Width - region.Width) <= 3 && Math.Abs(found.Height - region.Height) <= 3,
                $"Picker screenshot slot {index} must cover its ability border; expected {region}, found {found}.");
        }
    }
}

using (var withoutStrips = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png")))
{
    using (var graphics = Graphics.FromImage(withoutStrips))
    {
        graphics.FillPolygon(Brushes.Black,
            [new Point(0, 0), new Point(749, 0), new Point(749, 84), new Point(0, 136)]);
    }

    for (var y = 0; y < withoutStrips.Height; y++)
    {
        for (var x = 0; x < withoutStrips.Width; x++)
        {
            var pixel = withoutStrips.GetPixel(x, y);
            var light = (pixel.R * 77 + pixel.G * 150 + pixel.B * 29) >> 8;
            withoutStrips.SetPixel(x, y, Color.FromArgb(light, light, light));
        }
    }

    var detected = AbilitySlotRegionFinder.Find(withoutStrips,
        new Rectangle(0, 0, withoutStrips.Width, withoutStrips.Height));
    Assert(detected.Length == 3, "The three-slot pattern must be detected without colored strips or element colors.");
    Assert(Math.Abs(detected[0].X - 134) <= 3 && Math.Abs(detected[1].X - 217) <= 3 &&
        Math.Abs(detected[2].X - 300) <= 3,
        "Removing strip and element colors must not move detection onto the weapon panel.");
}

using (var pickerFixture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png")))
using (var weaponsOnly = pickerFixture.Clone(new Rectangle(385, 0, 364, 296), pickerFixture.PixelFormat))
{
    var detected = AbilitySlotRegionFinder.Find(weaponsOnly, new Rectangle(0, 0, weaponsOnly.Width, weaponsOnly.Height));
    Assert(detected.Length == 0, "The weapon/ammo panel and colored strips alone must not count as an ability row.");
}

foreach (var slotCount in new[] { 0, 1, 2, 3 })
{
    using var pattern = new Bitmap(500, 220);
    using (var graphics = Graphics.FromImage(pattern))
    using (var border = new Pen(Color.LightGray, 2))
    {
        graphics.Clear(Color.FromArgb(25, 24, 32));
        for (var index = 0; index < slotCount; index++)
        {
            var left = 60 + index * 75;
            var top = 90 - index * 5;
            graphics.DrawPolygon(border, [
                new Point(left, top), new Point(left + 65, top - 4),
                new Point(left + 65, top + 61), new Point(left, top + 65)
            ]);
        }
    }

    var detected = AbilitySlotRegionFinder.Find(pattern, new Rectangle(0, 0, pattern.Width, pattern.Height));
    Assert(detected.Length == (slotCount == 3 ? 3 : 0),
        $"A complete three-slot pattern is required; {slotCount} visible slots produced {detected.Length} regions.");
}

var scaleFixturePaths = new[] { "hud-2-5.png" };
var resolutionScales = new[] { ("720p", 2f / 3), ("1080p", 1f), ("1440p", 4f / 3), ("4K", 2f) };
foreach (var fixtureName in scaleFixturePaths)
{
    using var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName));
    foreach (var (resolution, scale) in resolutionScales)
    {
        var scaledWidth = (int)Math.Round(original.Width * scale);
        var scaledHeight = (int)Math.Round(original.Height * scale);
        using var scaled = new Bitmap(scaledWidth, scaledHeight);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(original, new Rectangle(0, 0, scaledWidth, scaledHeight));
        }

        var detected = AbilitySlotRegionFinder.Find(scaled, new Rectangle(0, 0, scaled.Width, scaled.Height));
        Assert(detected.Length == expectedAbilityRegions.Length,
            $"Auto-detection should find all slot borders in {fixtureName} scaled for {resolution}.");
        for (var index = 0; index < expectedAbilityRegions.Length; index++)
        {
            var baseRegion = expectedAbilityRegions[index];
            var expectedRegion = new Rectangle(
                (int)Math.Round(baseRegion.X * scale), (int)Math.Round(baseRegion.Y * scale),
                (int)Math.Round(baseRegion.Width * scale), (int)Math.Round(baseRegion.Height * scale));
            var side = (baseRegion.Width - 2) * scale;
            var addedMargin = Math.Max(2, (int)Math.Ceiling(side * 0.05)) - Math.Max(1, (int)(side * 0.03));
            expectedRegion.Inflate(addedMargin, addedMargin);
            var foundRegion = detected[index];
            const int tolerance = 3;
            Assert(new Rectangle(0, 0, scaled.Width, scaled.Height).Contains(foundRegion) &&
                (index == 0 || !foundRegion.IntersectsWith(detected[index - 1])),
                "Padded regions must stay within the source and must not overlap neighboring abilities.");
            var error = Math.Max(
                Math.Max(Math.Abs(foundRegion.Left - expectedRegion.Left), Math.Abs(foundRegion.Top - expectedRegion.Top)),
                Math.Max(Math.Abs(foundRegion.Width - expectedRegion.Width), Math.Abs(foundRegion.Height - expectedRegion.Height)));
            Assert(error <= tolerance,
                $"{resolution} scaling should preserve slot {index} within {tolerance} px; expected {expectedRegion}, found {foundRegion}.");
            Console.WriteLine($"{fixtureName} at {resolution}: slot {index} max error {error} px.");
        }
    }
}

using (var performanceFrame = new Bitmap(1920, 1080))
using (var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-2-5.png")))
using (var graphics = Graphics.FromImage(performanceFrame))
{
    graphics.DrawImageUnscaled(original, 100, 400);
    var stopwatch = Stopwatch.StartNew();
    var detected = AbilitySlotRegionFinder.Find(performanceFrame, new Rectangle(0, 0, 1920, 1080));
    stopwatch.Stop();
    Assert(detected.Length == expectedAbilityRegions.Length, "The 1920×1080 detection timing check should find all three slots.");
    Assert(stopwatch.Elapsed.TotalMilliseconds < 300,
        $"Ability slot detection on a 1920×1080 frame should finish under 300 ms; took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    Console.WriteLine($"Ability slot detection on a 1920×1080 frame: {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
}

Console.WriteLine("All bottom-flash detection, capture scheduling, selection, repeat-test, and input-hook checks passed.");

if (args.Contains("--update-smoke"))
{
    Task.Run(async () =>
    {
        var latest = await UpdateChecker.CheckAsync(CancellationToken.None);
        Console.WriteLine(latest is null ? "Running build is current." : $"Latest update: {latest.Tag} ({latest.AssetName})");
        using var smokeClient = new HttpClient();
        smokeClient.DefaultRequestHeaders.UserAgent.ParseAdd("D2AbilityTracker-tests");
        var smokeRelease = UpdateChecker.ParseLatestRelease(
            await smokeClient.GetStringAsync($"https://api.github.com/repos/{UpdateChecker.Repository}/releases/latest"),
            new Version(0, 0, 0)) ?? throw new InvalidOperationException("The latest release has no installer asset.");
        var downloaded = await UpdateChecker.DownloadAsync(smokeRelease, null, CancellationToken.None);
        Console.WriteLine($"Downloaded and verified {new FileInfo(downloaded).Length} bytes to {downloaded}.");
        File.Delete(downloaded);
    }).GetAwaiter().GetResult();
}

if (args.Contains("--capture-benchmark"))
{
    var benchmarkScreen = Screen.PrimaryScreen!;
    var benchmarkBounds = new Rectangle(benchmarkScreen.Bounds.Left + 200, benchmarkScreen.Bounds.Top + 200, 400, 170);
    using var desktop = ScreenCapture.OpenDesktop();
    using var duplication = new DesktopDuplicationCapture();
    using var gdiSurface = new ScreenCaptureSurface(400, 170, desktop.DeviceContext);
    using var dxgiSurface = new ScreenCaptureSurface(400, 170, desktop.DeviceContext);
    gdiSurface.Capture(desktop.DeviceContext, benchmarkBounds);
    var before = new byte[gdiSurface.Stride * 170];
    Marshal.Copy(gdiSurface.Pixels, before, 0, before.Length);
    var duplicated = false;
    for (var attempt = 0; attempt < 100 && !duplicated; attempt++)
    {
        // Duplication only serves frames once something has presented after it started.
        duplication.BeginTick();
        duplicated = duplication.TryCapture(benchmarkScreen.DeviceName, benchmarkBounds, dxgiSurface, out _);
        Thread.Sleep(duplicated ? 0 : 20);
    }

    if (duplicated)
    {
        gdiSurface.Capture(desktop.DeviceContext, benchmarkBounds);
        int stable = 0, differing = 0;
        for (var offset = 0; offset < before.Length; offset++)
        {
            if (offset % 4 != 3 && Marshal.ReadByte(gdiSurface.Pixels, offset) == before[offset])
            {
                stable++;
                differing += Marshal.ReadByte(dxgiSurface.Pixels, offset) != before[offset] ? 1 : 0;
            }
        }

        Console.WriteLine($"DXGI duplication available; {differing} of {stable} stable color bytes differ from GDI.");
        RunCaptureBenchmark("DXGI", () =>
        {
            duplication.BeginTick();
            return duplication.TryCapture(benchmarkScreen.DeviceName, benchmarkBounds, dxgiSurface, out _) ? dxgiSurface : null;
        });
    }
    else
    {
        Console.WriteLine($"DXGI duplication unavailable or no frame presented within 2 s: {duplication.LastError}");
    }

    RunCaptureBenchmark("GDI", () =>
    {
        gdiSurface.Capture(desktop.DeviceContext, benchmarkBounds);
        return gdiSurface;
    });
}

static void RunCaptureBenchmark(string name, Func<ScreenCaptureSurface?> capture)
{
    var clock = Stopwatch.StartNew();
    var captures = 0;
    long workTicks = 0;
    var nextFrame = Stopwatch.GetTimestamp();
    using var wait = new PrecisionWait();
    using var never = new ManualResetEvent(false);
    while (clock.Elapsed < TimeSpan.FromSeconds(2))
    {
        var now = Stopwatch.GetTimestamp();
        if (now < nextFrame)
        {
            wait.Wait(TimeSpan.FromSeconds((nextFrame - now) / (double)Stopwatch.Frequency), never);
            continue;
        }

        var started = Stopwatch.GetTimestamp();
        var surface = capture() ?? throw new InvalidOperationException($"{name} capture failed.");
        AbilitySignalDetector.Read(surface.Pixels, surface.Stride, 0, 0, 60, 60, 6);
        var finished = Stopwatch.GetTimestamp();
        workTicks += finished - started;
        nextFrame = CaptureSchedule.NextFrame(started, finished, 120, Stopwatch.Frequency);
        captures++;
    }

    Console.WriteLine($"Load-limited 400x170 {name} capture: {captures / clock.Elapsed.TotalSeconds:F1} Hz, " +
        $"{workTicks / (double)Stopwatch.Frequency / clock.Elapsed.TotalSeconds:P1} capture duty.");
}
static AbilitySignal ReadSyntheticSlot(int size, bool thickBorder, bool interiorLine = false)
{
    var padding = AbilitySignalDetector.BottomPadding(size);
    var data = new byte[size * (size + padding) * 4];
    var borderRows = Math.Max(1, size / 40);
    for (var y = size - borderRows; y < size + (thickBorder ? borderRows : 0); y++)
    {
        for (var x = 0; x < size; x++)
        {
            var offset = (y * size + x) * 4;
            data[offset] = data[offset + 1] = data[offset + 2] = 255;
        }

    }

    if (interiorLine)
    {
        for (var x = 0; x < size; x++)
        {
            var offset = ((int)(size * 0.88) * size + x) * 4;
            data[offset] = data[offset + 1] = data[offset + 2] = 255;
        }
    }

    var buffer = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, buffer, data.Length);
        return AbilitySignalDetector.Read(buffer, size * 4, 0, 0, size, size, padding);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static AbilitySignal ReadSyntheticProgress(int size, float progress)
{
    var data = new byte[size * size * 4];
    for (var y = (int)(size * (1 - progress)); y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var offset = (y * size + x) * 4;
            data[offset] = data[offset + 1] = data[offset + 2] = 255;
        }
    }

    var buffer = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, buffer, data.Length);
        return AbilitySignalDetector.Read(buffer, size * 4, 0, 0, size, size);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static void AssertReadableButtons(System.Windows.Forms.Control control)
{
    foreach (System.Windows.Forms.Control child in control.Controls)
    {
        if (child is System.Windows.Forms.Button button)
        {
            Assert(button.ForeColor.GetBrightness() > 0.8f && button.BackColor.GetBrightness() < 0.3f,
                $"Selector button '{button.Text}' should have explicit light-on-dark text.");
        }

        AssertReadableButtons(child);
    }
}

// Simulates "Destiny 2 not found" and a slot-specific failure on Tracking without persisting settings.
static void RenderCaptureStatusStates(MainForm form, Control navigation, string outputDirectory)
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    T Field<T>(string name) => (T)typeof(MainForm).GetField(name, flags)!.GetValue(form)!;
    var settings = Field<TrackerSettings>("_settings");
    var captureErrors = Field<Dictionary<AbilityKind, string>>("_captureErrors");
    var statusLabels = Field<Dictionary<AbilityKind, Label>>("_statusLabels");
    var toolTip = Field<ToolTip>("_toolTip");
    var sync = Field<object>("_captureSync");
    var updateStatus = typeof(MainForm).GetMethod("UpdateStatus", flags)!;
    var banner = Descendants(form).OfType<StatusBanner>().Single();
    var pill = Descendants(form).OfType<StatusPill>().Single(control => control.Name == "CaptureStatus");
    navigation.Controls.OfType<Button>().Single(button => button.Text == "Tracking").PerformClick();
    var savedRegions = settings.Slots.ToDictionary(slot => slot.Kind, slot => slot.Region);
    var savedSolo = settings.SoloAbility;
    var restingSize = form.Size;
    Dictionary<AbilityKind, string> savedErrors;
    lock (sync)
    {
        savedErrors = new Dictionary<AbilityKind, string>(captureErrors);
    }

    void SetErrors(Dictionary<AbilityKind, string> errors)
    {
        lock (sync)
        {
            captureErrors.Clear();
            foreach (var error in errors) captureErrors[error.Key] = error.Value;
        }
        updateStatus.Invoke(form, null);
        form.PerformLayout();
    }

    void Save(string name)
    {
        using var image = new Bitmap(form.Width, form.Height);
        form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
        image.Save(Path.Combine(outputDirectory, name));
    }

    try
    {
        lock (sync)
        {
            settings.SoloAbility = null;
            foreach (var slot in settings.Slots)
                slot.Region ??= new Rectangle(100 + 90 * (int)slot.Kind, 900, 60, 64);
        }

        SetErrors(Enum.GetValues<AbilityKind>().ToDictionary(kind => kind, _ => MainForm.GameWindowUnavailableError));
        Assert(banner.Visible && banner.Tone == StatusTone.Warning && banner.Text == MainForm.WaitingForGameBanner &&
            pill.Tone == StatusTone.Warning && pill.Text == "Waiting for game",
            "A missing Destiny 2 window must show one amber banner and pill.");
        Assert(statusLabels.Values.All(label => label.Text.StartsWith("Waiting for game\n", StringComparison.Ordinal) &&
                label.ForeColor == TrackerTheme.Muted && !label.Text.Contains("Destiny 2 window")),
            "Rows must show a muted waiting state instead of repeating the global error.");
        Assert(form.ClientRectangle.Contains(form.RectangleToClient(banner.RectangleToScreen(banner.ClientRectangle))) &&
            TextRenderer.MeasureText(banner.Text, banner.Font).Width + banner.Padding.Horizontal <= banner.Width,
            "The waiting banner must fit inside the window without truncation.");
        Assert(Descendants(form).OfType<Button>().Single(button => button.Name == "PauseDetection") is
        { BackColor: var pauseBack, ForeColor: var pauseFore } && pauseBack == TrackerTheme.Accent && pauseFore == TrackerTheme.Background,
            "Capture health must not be encoded in the pause button, which keeps its primary style.");
        Assert(form.Size == restingSize, "Showing the status banner must not resize the window.");
        Save("ui-Tracking-waiting.png");

        const string slotError = "Ability area is outside the current display.";
        SetErrors(new Dictionary<AbilityKind, string> { [AbilityKind.Melee] = slotError });
        Assert(statusLabels[AbilityKind.Melee].ForeColor == TrackerTheme.Red &&
            statusLabels[AbilityKind.Melee].Text.StartsWith("Capture failed\n", StringComparison.Ordinal) &&
            toolTip.GetToolTip(statusLabels[AbilityKind.Melee]) == slotError,
            "A slot-specific failure must be red on that row with the full text in its tooltip.");
        Assert(statusLabels.Where(item => item.Key != AbilityKind.Melee).All(item => item.Value.ForeColor != TrackerTheme.Red) &&
            !(banner.Visible && banner.Text.Contains(slotError)),
            "A slot-specific failure must not be routed to the banner or other rows.");
        Save("ui-Tracking-row-error.png");

        // A HUD recording shows a chip with Stop on Tracking instead of resizing the window.
        var chip = Descendants(form).OfType<StatusPill>().Single(control => control.Name == "RecordingChip");
        var stop = Descendants(form).OfType<Button>().Single(control => control.Name == "StopRecording");
        var trackRow = Descendants(form).Single(control => control.Name == "TrackRow");
        var selector = Descendants(form).OfType<SegmentedControl>().Single(control => control.Name == "TrackSelector");
        chip.Text = "Recording HUD · 00:42 · 2520 frames";
        typeof(MainForm).GetMethod("ShowRecordingIndicator", flags)!.Invoke(form, [true]);
        form.PerformLayout();
        var rowBounds = trackRow.RectangleToScreen(trackRow.ClientRectangle);
        var chipBounds = chip.RectangleToScreen(chip.ClientRectangle);
        var stopBounds = stop.RectangleToScreen(stop.ClientRectangle);
        Assert(chip.Visible && stop.Visible && chip.Tone == StatusTone.Error && form.Size == restingSize &&
            rowBounds.Contains(chipBounds) && rowBounds.Contains(stopBounds) &&
            selector.RectangleToScreen(selector.ClientRectangle).Right < chipBounds.Left &&
            TextRenderer.MeasureText(chip.Text, chip.Font).Width + chip.Padding.Horizontal <= chip.Width,
            $"The recording chip and its Stop action must fit beside the track selector without resizing the window: row {rowBounds}, chip {chipBounds} visible {chip.Visible}, stop {stopBounds}, size {form.Size} vs {restingSize}.");
        Save("ui-Tracking-recording.png");
        typeof(MainForm).GetMethod("ShowRecordingIndicator", flags)!.Invoke(form, [false]);

        // First run: no areas at all shows the setup card instead of results, banner, or action bar.
        lock (sync)
        {
            foreach (var slot in settings.Slots) slot.Region = null;
        }
        SetErrors([]);
        var emptyState = Descendants(form).Single(control => control.Name == "EmptyState");
        var emptyButtons = Descendants(emptyState).OfType<Button>().ToArray();
        Assert(emptyState.Visible && !banner.Visible && pill.Text == "Areas not set" &&
            !Descendants(form).Single(control => control.Name == "TrackingActions").Visible &&
            !Descendants(form).Single(control => control.Name == "AbilityRows").Visible &&
            form.Size == restingSize,
            "Without any areas, Tracking must show only the first-run card, without resizing the window.");
        Assert(emptyButtons.Select(button => button.Text).SequenceEqual(["Auto-detect areas…", "Select manually ▾", "Guide"]) &&
            emptyButtons[0].BackColor == TrackerTheme.Accent && emptyButtons.Skip(1).All(link => link.FlatAppearance.BorderSize == 0) &&
            emptyButtons.All(button => form.ClientRectangle.Contains(form.RectangleToClient(button.RectangleToScreen(button.ClientRectangle)))),
            "The first-run card must offer a primary Auto-detect action plus manual-selection and guide links.");
        Save("ui-Tracking-first-run.png");
        Assert(Descendants(emptyState).OfType<Label>().All(label => label.PreferredWidth <= label.Width &&
                emptyState.RectangleToScreen(emptyState.ClientRectangle).Contains(label.RectangleToScreen(label.ClientRectangle))),
            "First-run instructions must not be clipped: " + string.Join("; ", Descendants(emptyState).OfType<Label>().Select(label =>
                $"{label.Text} {TextRenderer.MeasureText(label.Text, label.Font).Width}/{label.Width}")));
    }
    finally
    {
        lock (sync)
        {
            settings.SoloAbility = savedSolo;
            foreach (var slot in settings.Slots)
                slot.Region = savedRegions[slot.Kind];
        }

        SetErrors(savedErrors);
    }
}

// Verifies the de-buttoned Tracking page: few visible actions, dark menus, the track selector and hover-only copy actions.
// Seeds sample areas when the machine has none (e.g. CI), so the configured Tracking layout is checked, then restores them.
static void CheckTrackingPage(MainForm form, Control page, TrackerSettings settings, string outputDirectory)
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var savedRegions = settings.Slots.ToDictionary(slot => slot.Kind, slot => slot.Region);
    foreach (var slot in settings.Slots)
        slot.Region ??= new Rectangle(100 + 90 * (int)slot.Kind, 900, 60, 64);
    typeof(MainForm).GetMethod("UpdateStatus", flags)!.Invoke(form, null);
    form.PerformLayout();
    try
    {
        CheckConfiguredTrackingPage(form, page, settings, outputDirectory);
    }
    finally
    {
        foreach (var slot in settings.Slots)
            slot.Region = savedRegions[slot.Kind];
        typeof(MainForm).GetMethod("UpdateStatus", flags)!.Invoke(form, null);
        form.PerformLayout();
    }
}

static void CheckConfiguredTrackingPage(MainForm form, Control page, TrackerSettings settings, string outputDirectory)
{
    const System.Reflection.BindingFlags flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
    var selector = Descendants(page).OfType<SegmentedControl>().Single(control => control.Name == "TrackSelector");
    Assert(selector.Visible && selector.Segments.Select(segment => segment.Text).SequenceEqual(["All", "Grenade", "Melee", "Class"]) &&
        selector.SelectedIndex == (settings.SoloAbility is { } solo ? 1 + (int)solo : 0),
        "Tracking must offer one segmented All / Grenade / Melee / Class selector reflecting solo tracking.");
    var actions = Descendants(page).Where(control => control.Visible &&
        (control is SegmentedControl || control is ButtonBase and not CopyResultButton and not RadioButton)).ToArray();
    Assert(actions.Length <= 5,
        $"Tracking should show at most five actions at rest: {string.Join(", ", actions.Select(action => action.Text))}");
    Assert(!Descendants(page).OfType<ButtonBase>().Any(button => button.Visible &&
            (button.Text is "Guide" or "Reset areas" or "Select area…" or "Auto-detect areas…" || button.Text.StartsWith("Track only"))),
        "Area setup actions must live in the Areas menu.");
    Assert(!Descendants(page).OfType<Label>().Any(label => label.Visible &&
            System.Text.RegularExpressions.Regex.IsMatch(label.Text, @"^\d+ × \d+$")),
        "The area-size column must be removed from the main page.");
    var copies = Descendants(page).OfType<CopyResultButton>().ToArray();
    Assert(copies.All(copy => copy.Visible && copy.Enabled && copy.TabStop && !copy.IconVisible),
        "Copy actions must stay hidden until hover or focus while remaining keyboard focusable.");
    if (copies[0].Focus())
    {
        Assert(copies[0].IconVisible, "A focused copy action must be visible.");
        page.Focus();
    }

    var pause = Descendants(page).OfType<Button>().Single(button => button.Name == "PauseDetection");
    Assert(pause.Visible && pause.BackColor == TrackerTheme.Accent && pause.FlatAppearance.BorderSize == 0,
        "Pause detection must be the filled primary action on Tracking.");
    var overlayToggle = Descendants(page).OfType<ToggleSwitch>().Single(control => control.Name == "OverlayToggle");
    var overlayVisible = settings.OverlayVisible;
    Assert(overlayToggle.Visible && overlayToggle.Checked == overlayVisible && overlayToggle.Text == "Overlay",
        "The compact overlay toggle must reflect the overlay setting.");
    var onClick = typeof(Control).GetMethod("OnClick", flags)!;
    onClick.Invoke(overlayToggle, [EventArgs.Empty]);
    Assert(settings.OverlayVisible != overlayVisible && overlayToggle.Checked != overlayVisible,
        "The overlay toggle must switch the overlay setting.");
    onClick.Invoke(overlayToggle, [EventArgs.Empty]);
    Assert(settings.OverlayVisible == overlayVisible && overlayToggle.Checked == overlayVisible,
        "Toggling the overlay twice must restore it.");

    var areasButton = Descendants(page).OfType<Button>().Single(button => button.Name == "AreasButton");
    var areasMenu = areasButton.ContextMenuStrip!;
    Assert(areasMenu.Items.OfType<ToolStripMenuItem>().Select(item => item.Text).SequenceEqual([
            "Auto-detect areas…", "Select grenade area…", "Select melee area…", "Select class ability area…",
            "Selection guide", "Reset areas…"]) &&
        areasMenu.Items.OfType<ToolStripSeparator>().Count() == 2,
        "The Areas menu must hold detection, manual selection, the guide and reset.");
    var moreButton = Descendants(page).OfType<Button>().Single(button => button.Name == "MoreButton");
    Assert(moreButton.AccessibleName == "More actions" &&
        moreButton.ContextMenuStrip!.Items.OfType<ToolStripMenuItem>().Any(item => item.Text == "Move overlay"),
        "The overflow menu must offer Move overlay.");
    foreach (var (anchor, name) in new[] { (areasButton, "Areas"), (moreButton, "More") })
    {
        var menu = anchor.ContextMenuStrip!;
        Assert(menu.BackColor == TrackerTheme.Surface && menu.Renderer is ToolStripProfessionalRenderer
        { ColorTable.ToolStripDropDownBackground: var menuBack } && menuBack == TrackerTheme.Surface,
            "Menus must use the dark theme renderer.");
        menu.Opacity = 0; // Drop-downs are clamped onto a visible screen; keep them invisible.
        menu.Show(anchor, new Point(0, anchor.Height + 2));
        try
        {
            using var image = new Bitmap(menu.Width, menu.Height);
            menu.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            Assert(image.GetPixel(image.Width / 2, image.Height - 3).GetBrightness() < 0.3 && HasReadableText(image),
                "Menus must render light text on a dark surface.");
            image.Save(Path.Combine(outputDirectory, $"ui-Menu-{name}.png"));
        }
        finally
        {
            menu.Close();
        }
    }

    var savedSolo = settings.SoloAbility;
    var setSolo = typeof(MainForm).GetMethod("SetSolo", flags)!;
    var statusLabels = (Dictionary<AbilityKind, Label>)typeof(MainForm).GetField("_statusLabels", flags)!.GetValue(form)!;
    try
    {
        selector.Segments[1].PerformClick();
        Assert(settings.SoloAbility == AbilityKind.Grenade && selector.SelectedIndex == 1 &&
            statusLabels[AbilityKind.Melee].Text.StartsWith("Skipped", StringComparison.Ordinal) &&
            statusLabels[AbilityKind.ClassAbility].ForeColor == TrackerTheme.Muted,
            "Choosing one ability must track it alone and mark the others as skipped.");
        using (var image = new Bitmap(form.Width, form.Height))
        {
            form.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
            image.Save(Path.Combine(outputDirectory, "ui-Tracking-solo.png"));
        }
        selector.Segments[0].PerformClick();
        Assert(settings.SoloAbility is null && !statusLabels[AbilityKind.Melee].Text.StartsWith("Skipped", StringComparison.Ordinal),
            "All must track every ability again.");
    }
    finally
    {
        setSolo.Invoke(form, [savedSolo]);
    }
}

static IEnumerable<Control> Descendants(Control parent)
{
    foreach (Control child in parent.Controls)
    {
        yield return child;
        foreach (var descendant in Descendants(child))
        {
            yield return descendant;
        }
    }
}

static bool HasReadableText(Bitmap image)
{
    var pixels = 0;
    for (var y = 2; y < image.Height - 2; y++)
        for (var x = 2; x < image.Width - 2; x++)
        {
            var color = image.GetPixel(x, y);
            if (color.R >= 120 && color.G >= 120 && color.B >= 120) pixels++;
        }
    return pixels >= 5;
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }

}

static int WindowHitTest(Form form, Point point)
{
    var screen = form.PointToScreen(point);
    return (int)NativeWindowTest.SendMessage(form.Handle, 0x0084, IntPtr.Zero,
        (IntPtr)((screen.X & 0xffff) | ((screen.Y & 0xffff) << 16)));
}

internal static class NativeWindowTest
{
    [DllImport("user32.dll")]
    internal static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);
}

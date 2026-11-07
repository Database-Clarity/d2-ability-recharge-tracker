namespace D2AbilityTracker;

// Tracking page: track selector, ability results, first-run card, and the action bar with its menus.
internal sealed partial class MainForm
{
    private const int AbilityNameWidth = 128;
    private const int AbilityRowHeight = 44;
    private readonly SegmentedControl _trackSelector = new("All", "Grenade", "Melee", "Class");
    private readonly ToggleSwitch _overlaySwitch = new();
    private readonly Button _areasButton = new();
    private readonly Button _moreButton = new();
    private readonly ContextMenuStrip _areasMenu = TrackerTheme.Menu("AreasMenu");
    private readonly ContextMenuStrip _moreMenu = TrackerTheme.Menu("MoreMenu");
    private readonly Dictionary<AbilityKind, ToolStripMenuItem> _selectAreaItems = [];
    private readonly Dictionary<AbilityKind, Label> _abilityNameLabels = [];
    private readonly StatusPill _recordingChip = new();
    private readonly Button _stopRecordingButton = new();
    private readonly Button _emptyStateAutoDetect = new();
    private readonly Button _selectManuallyLink = new();
    private readonly Button _guideLink = new();
    private readonly ToolStripMenuItem _resetAreasItem = new("Reset areas…");
    private readonly ToolStripMenuItem _moveOverlayItem = new("Move overlay");
    private Panel? _emptyState;
    private Control? _trackRow;
    private Control? _abilityRows;
    private Control? _trackingActions;
    private FlowLayoutPanel? _recordingIndicator;
    private bool? _trackingConfigured;

    private Panel BuildTrackingPage()
    {
        var stack = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Dock = DockStyle.Top,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        stack.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, GridWidth));
        _statusBanner.Name = "TrackingStatusBanner";
        _statusBanner.Width = GridWidth - 8;
        _statusBanner.Height = 32;
        _statusBanner.Margin = new Padding(3, 0, 5, 6);
        _statusBanner.Visible = false;
        _trackRow = BuildTrackRow();
        _abilityRows = BuildAbilityRows();
        _emptyState = BuildEmptyState();
        // Explicit rows: automatic placement skips hidden controls, so the recording row would land last when shown.
        stack.Controls.Add(_statusBanner, 0, 0);
        stack.Controls.Add(_trackRow, 0, 1);
        stack.Controls.Add(_recordingIndicator!, 0, 2);
        stack.Controls.Add(_abilityRows, 0, 3);
        stack.Controls.Add(_emptyState, 0, 4);
        _trackingActions = BuildTrackingActions();
        BuildTrackingMenus();
        var page = new Panel { Name = "TrackingPage", Dock = DockStyle.Fill, Margin = Padding.Empty };
        page.Controls.Add(stack);
        page.Controls.Add(_trackingActions);
        return page;
    }

    private Control BuildTrackRow()
    {
        var row = new Panel
        {
            Name = "TrackRow",
            Width = GridWidth,
            Height = TrackerTheme.ControlHeight + 8,
            Margin = new Padding(0, 0, 0, 4)
        };
        var selector = new FlowLayoutPanel
        {
            Dock = DockStyle.Left,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        var label = GridLabel("Track");
        label.Margin = new Padding(3, 9, 10, 0);
        _trackSelector.Name = "TrackSelector";
        _trackSelector.AccessibleName = "Tracked abilities";
        _trackSelector.Margin = new Padding(3, 4, 3, 4);
        _trackSelector.SelectedIndex = _settings.SoloAbility is { } solo ? 1 + Array.IndexOf(AbilityKinds, solo) : 0;
        _trackSelector.SelectionChanged += (_, _) =>
            SetSolo(_trackSelector.SelectedIndex <= 0 ? null : AbilityKinds[_trackSelector.SelectedIndex - 1]);
        foreach (var segment in _trackSelector.Segments.Skip(1))
        {
            _toolTip.SetToolTip(segment, $"Track only {segment.Text.ToLowerInvariant()}: one captured slot for the best performance.");
        }
        _toolTip.SetToolTip(_trackSelector.Segments[0], "Track all three abilities.");
        selector.Controls.Add(label);
        selector.Controls.Add(_trackSelector);
        var markCharged = _markChargedButton;
        markCharged.Name = "MarkCharged";
        markCharged.Text = WithShortcut("Mark charged", MarkChargedBinding);
        markCharged.AutoSize = true;
        markCharged.Margin = new Padding(12, 4, 3, 4);
        markCharged.Click += (_, _) => MarkTrackedCharged();
        _toolTip.SetToolTip(markCharged, "Mark all tracked abilities charged and learn their current appearance (F9 by default).");
        selector.Controls.Add(markCharged);

        _recordingChip.Name = "RecordingChip";
        _recordingChip.Tone = StatusTone.Error;
        _recordingChip.Margin = new Padding(3, 7, 2, 0);
        _recordingChip.AccessibleName = "HUD recording";
        _stopRecordingButton.Name = "StopRecording";
        _stopRecordingButton.Text = "Stop";
        _stopRecordingButton.Width = 52;
        _stopRecordingButton.Margin = new Padding(0, 4, 5, 0);
        _stopRecordingButton.Click += (_, _) => ToggleHudRecording();
        _toolTip.SetToolTip(_stopRecordingButton, "Stop the HUD recording and save it.");
        // Its own row under the track selector, so nothing on the track row can cover it.
        _recordingIndicator = new FlowLayoutPanel
        {
            Name = "RecordingIndicator",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 0, 0, 4),
            Padding = Padding.Empty,
            Visible = false
        };
        _recordingIndicator.Controls.Add(_recordingChip);
        _recordingIndicator.Controls.Add(_stopRecordingButton);
        row.Controls.Add(selector);
        return row;
    }

    private Control BuildAbilityRows()
    {
        var rows = new TableLayoutPanel
        {
            Name = "AbilityRows",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        foreach (var kind in AbilityKinds)
        {
            var row = new Panel
            {
                Name = $"{kind}Row",
                Width = GridWidth - 8,
                Height = AbilityRowHeight,
                Margin = new Padding(3, 0, 5, 4)
            };
            var statusLabel = new TwoLineStatusLabel
            {
                Dock = DockStyle.Fill,
                AutoEllipsis = true,
                ForeColor = TrackerTheme.Muted,
                TextAlign = ContentAlignment.MiddleLeft
            };
            var copyResult = new CopyResultButton($"Copy {KindName(kind)} result", () => $"{KindName(kind)}: {statusLabel.Text}")
            {
                Dock = DockStyle.Right,
                Width = 32,
                Revealed = false
            };
            _toolTip.SetToolTip(copyResult, $"Copy {KindName(kind)} measurement and regression");
            var nameColumn = new Panel { Dock = DockStyle.Left, Width = AbilityNameWidth, Margin = Padding.Empty };
            var name = new Label
            {
                Text = KindName(kind),
                Dock = DockStyle.Top,
                Height = AbilityRowHeight / 2,
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = TrackerTheme.Text,
                AutoEllipsis = true
            };
            TrackerTheme.SetTypography(name, 9, FontStyle.Bold);
            nameColumn.Controls.Add(name);
            row.Controls.Add(statusLabel);
            row.Controls.Add(copyResult);
            row.Controls.Add(nameColumn);

            // The copy action appears while the pointer is over the row or the button has keyboard focus.
            void UpdateReveal() => copyResult.Revealed =
                row.IsHandleCreated && row.ClientRectangle.Contains(row.PointToClient(Cursor.Position));
            foreach (var control in new Control[] { row, nameColumn, name, statusLabel, copyResult })
            {
                control.MouseEnter += (_, _) => UpdateReveal();
                control.MouseLeave += (_, _) => UpdateReveal();
            }

            rows.Controls.Add(row);
            _abilityNameLabels.Add(kind, name);
            _statusLabels.Add(kind, statusLabel);
        }

        return rows;
    }

    private Panel BuildEmptyState()
    {
        var card = new Panel
        {
            Name = "EmptyState",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(GridWidth - 8, 0),
            BackColor = TrackerTheme.Surface,
            Margin = new Padding(3, 0, 5, 6),
            Padding = new Padding(20, 18, 20, 18),
            Visible = false
        };
        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Location = new Point(card.Padding.Left, card.Padding.Top),
            Margin = Padding.Empty,
            Padding = Padding.Empty,
            BackColor = TrackerTheme.Surface
        };
        var heading = new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 10) };
        TrackerTheme.SectionHeading(heading);
        heading.Text = "Set up ability tracking";
        layout.Controls.Add(heading);
        foreach (var step in new[]
        {
            "1. Start Destiny 2 and make sure the ability HUD is visible.",
            "2. Click below – the tracker finds all three slots and lets you adjust."
        })
        {
            layout.Controls.Add(new Label { Text = step, AutoSize = true, ForeColor = TrackerTheme.Muted, Margin = new Padding(0, 0, 0, 4) });
        }

        var actions = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = new Padding(0, 12, 0, 0),
            Padding = Padding.Empty,
            BackColor = TrackerTheme.Surface
        };
        _emptyStateAutoDetect.Name = "EmptyStateAutoDetect";
        _emptyStateAutoDetect.Text = "Auto-detect areas…";
        _emptyStateAutoDetect.Width = 172;
        _emptyStateAutoDetect.Margin = new Padding(0, 0, 12, 0);
        _emptyStateAutoDetect.Click += (_, _) => AutoDetectRegions();
        _selectManuallyLink.Text = "Select manually ▾";
        _selectManuallyLink.AutoSize = true;
        _selectManuallyLink.Margin = new Padding(0, 0, 4, 0);
        _selectManuallyLink.Click += (_, _) => ShowMenu(_areasMenu, _selectManuallyLink);
        _guideLink.Text = "Guide";
        _guideLink.AutoSize = true;
        _guideLink.Margin = Padding.Empty;
        _guideLink.Click += (_, _) => ShowSelectionGuide();
        _toolTip.SetToolTip(_guideLink, "Show how to select an ability slot.");
        actions.Controls.Add(_emptyStateAutoDetect);
        actions.Controls.Add(_selectManuallyLink);
        actions.Controls.Add(_guideLink);
        layout.Controls.Add(actions);
        card.Controls.Add(layout);
        return card;
    }

    private Control BuildTrackingActions()
    {
        var bar = new TableLayoutPanel
        {
            Name = "TrackingActions",
            Dock = DockStyle.Bottom,
            ColumnCount = 5,
            RowCount = 1,
            Height = TrackerTheme.ControlHeight + 14,
            Margin = Padding.Empty,
            Padding = new Padding(0, 8, 0, 0)
        };
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        bar.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        bar.Paint += (_, args) =>
        {
            using var divider = new Pen(TrackerTheme.Border);
            args.Graphics.DrawLine(divider, 3, 0, bar.Width - 6, 0);
        };

        _toggleButton.Name = "PauseDetection";
        _toggleButton.Width = 184;
        _toggleButton.Click += (_, _) => SetMonitoring(!_monitoring);
        _overlaySwitch.Name = "OverlayToggle";
        _overlaySwitch.Text = "Overlay";
        _overlaySwitch.Click += (_, _) => SetOverlayVisible(!_settings.OverlayVisible);
        _areasButton.Name = "AreasButton";
        _areasButton.Text = "Areas ▾";
        _areasButton.Width = 92;
        _areasButton.ContextMenuStrip = _areasMenu;
        _areasButton.Click += (_, _) => ShowMenu(_areasMenu, _areasButton);
        _toolTip.SetToolTip(_areasButton, "Detect, select, or reset the ability areas.");
        _moreButton.Name = "MoreButton";
        _moreButton.Text = "⋯";
        _moreButton.AccessibleName = "More actions";
        _moreButton.Width = 36;
        _moreButton.ContextMenuStrip = _moreMenu;
        _moreButton.Click += (_, _) => ShowMenu(_moreMenu, _moreButton);
        _toolTip.SetToolTip(_moreButton, "More actions");
        foreach (var control in new Control[] { _toggleButton, _overlaySwitch, _areasButton, _moreButton })
        {
            control.Anchor = AnchorStyles.Left;
            control.Height = TrackerTheme.ControlHeight;
            control.Margin = new Padding(3, 3, 5, 3);
        }
        _toggleButton.Margin = new Padding(3, 3, 16, 3);
        bar.Controls.Add(_toggleButton, 0, 0);
        bar.Controls.Add(_overlaySwitch, 1, 0);
        bar.Controls.Add(_areasButton, 3, 0);
        bar.Controls.Add(_moreButton, 4, 0);
        return bar;
    }

    private void BuildTrackingMenus()
    {
        _areasMenu.Items.Add(new ToolStripMenuItem("Auto-detect areas…", null, (_, _) => BeginInvoke(AutoDetectRegions)));
        _areasMenu.Items.Add(new ToolStripSeparator());
        foreach (var kind in AbilityKinds)
        {
            var item = new ToolStripMenuItem($"Select {KindName(kind).ToLowerInvariant()} area…", null,
                (_, _) => BeginInvoke(() => SelectRegion(kind)))
            {
                ShowShortcutKeys = true
            };
            _selectAreaItems.Add(kind, item);
            _areasMenu.Items.Add(item);
        }

        _areasMenu.Items.Add(new ToolStripSeparator());
        _areasMenu.Items.Add(new ToolStripMenuItem("Selection guide", null, (_, _) => BeginInvoke(ShowSelectionGuide)));
        _resetAreasItem.Click += (_, _) => BeginInvoke(ResetSelections);
        _areasMenu.Items.Add(_resetAreasItem);
        _areasMenu.Opening += (_, _) => RefreshAreasMenu();

        _moveOverlayItem.Click += (_, _) => SetOverlayMoveMode(!_overlayMoveMode);
        _moreMenu.Items.Add(_moveOverlayItem);
        _moreMenu.Items.Add(new ToolStripMenuItem("Copy all results", null, (_, _) => CopyAllResults()));
        _moreMenu.Items.Add(new ToolStripSeparator());
        _moreMenu.Items.Add(new ToolStripMenuItem("Troubleshooting…", null, (_, _) => ShowTroubleshooting()));
        _moreMenu.Opening += (_, _) => RefreshMoreMenu();
        RefreshAreasMenu();
        RefreshMoreMenu();
    }

    private void RefreshAreasMenu()
    {
        foreach (var (kind, item) in _selectAreaItems)
        {
            item.ShortcutKeyDisplayString = GetSlot(kind).Region is { } region ? $"{region.Width} × {region.Height}" : "not set";
        }

        _resetAreasItem.Enabled = _settings.Slots.Any(slot => slot.Region is not null);
    }

    private void RefreshMoreMenu()
    {
        _moveOverlayItem.Text = _overlayMoveMode ? "Finish moving overlay" : "Move overlay";
    }

    private static void ShowMenu(ContextMenuStrip menu, Control anchor)
    {
        menu.Show(anchor, new Point(0, anchor.Height + 2), ToolStripDropDownDirection.BelowRight);
    }

    private void CopyAllResults()
    {
        var text = string.Join(Environment.NewLine, AbilityKinds
            .Where(kind => IsTracked(kind) && GetSlot(kind).Region is not null)
            .Select(kind => $"{KindName(kind)}: {_statusLabels[kind].Text.TrimEnd()}"));
        try
        {
            if (text.Length > 0) Clipboard.SetText(text);
        }
        catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or ThreadStateException)
        {
            MessageBox.Show(this, $"Couldn't copy the results: {exception.Message}", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ShowTroubleshooting()
    {
        if (_settingsPage is null) return;
        SelectPage(_settingsPage);
        _recordHudButton.Focus();
    }

    /// <summary>Applies the primary / secondary / tertiary button tiers after the shared theme pass.</summary>
    private void ApplyTrackingButtonTiers()
    {
        TrackerTheme.Primary(_toggleButton);
        TrackerTheme.Primary(_emptyStateAutoDetect);
        TrackerTheme.Link(_selectManuallyLink, TrackerTheme.Surface);
        TrackerTheme.Link(_guideLink, TrackerTheme.Surface);
        TrackerTheme.Link(_stopRecordingButton);
        _stopRecordingButton.ForeColor = TrackerTheme.Red;
        foreach (var copy in _statusLabels.Values.Select(label => label.Parent!.Controls.OfType<CopyResultButton>().Single()))
        {
            copy.BackColor = TrackerTheme.Background;
            copy.FlatAppearance.MouseOverBackColor = TrackerTheme.Hover;
        }
    }

    /// <summary>Switches between the first-run card and the configured results without resizing the window.</summary>
    private void UpdateTrackingLayout(bool configured)
    {
        if (_trackingConfigured == configured) return;
        _trackingConfigured = configured;
        _emptyState!.Visible = !configured;
        _trackRow!.Visible = configured;
        _abilityRows!.Visible = configured;
        _trackingActions!.Visible = configured;
    }

    /// <summary>The tallest Tracking layout: banner plus either the configured results and action bar, or the first-run card.</summary>
    private int TrackingPageHeight()
    {
        if (_trackRow is null || _abilityRows is null || _emptyState is null || _trackingActions is null) return 0;
        static int Outer(Control control, int height) => height + control.Margin.Vertical;
        var banner = Outer(_statusBanner, _statusBanner.Height);
        var configured = Outer(_trackRow, _trackRow.Height) +
            Outer(_abilityRows, _abilityRows.GetPreferredSize(Size.Empty).Height) +
            _trackingActions.Height;
        var firstRun = Outer(_emptyState, _emptyState.GetPreferredSize(Size.Empty).Height);
        return banner + Math.Max(configured, firstRun);
    }

    private void ShowRecordingIndicator(bool recording)
    {
        // Assign unconditionally: Visible reads false while the Tracking page is hidden (a recording is usually
        // stopped from Settings), so comparing against it would leave the indicator showing when the user comes back.
        if (_recordingIndicator is not null)
        {
            _recordingIndicator.Visible = recording;
        }
    }
}

namespace D2AbilityTracker;

/// <summary>
/// History tab with two views: <b>Sessions</b> (one session at a time, with its own custom columns and session
/// actions) and <b>All measurements</b> (every result, filterable by ability, with its session as a column).
/// </summary>
internal sealed class MeasurementHistoryView : UserControl
{
    internal const int SessionsView = 0;
    internal const int AllView = 1;

    private MeasurementHistoryStore _history;
    private readonly SegmentedControl _mode = new("Sessions", "All measurements") { Name = "HistoryMode" };
    private readonly Label _activeSession = new() { Name = "ActiveSession" };
    private readonly FlowLayoutPanel _sessionsToolbar = new() { Name = "SessionsToolbar" };
    private readonly FlowLayoutPanel _allToolbar = new() { Name = "AllToolbar" };
    private readonly FlatComboBox _sessionPicker = new();
    private readonly Button _newSession = new() { Text = "New session…" };
    private readonly Button _toggleActive = new() { Name = "ToggleActive", Text = "Make active" };
    private readonly Button _sessionMore = new() { Name = "SessionMore", Text = "⋯", AccessibleName = "More session actions" };
    private readonly ContextMenuStrip _sessionMenu = TrackerTheme.Menu("SessionMenu");
    private readonly FlatComboBox _abilityFilter = new();
    private readonly DataGridView _measurements = new();
    private readonly TextBox _note = new();
    private readonly Button _saveNote = new() { Text = "Save note" };
    private readonly Button _assignSession = new() { Text = "Add to session ▾" };
    private readonly ContextMenuStrip _assignMenu = TrackerTheme.Menu("AssignMenu");
    private readonly Button _addColumn = new() { Text = "Add column…" };
    private readonly Button _export = new() { Text = "Export CSV…" };
    private readonly Label _status = new();
    private IReadOnlyList<string> _visibleColumns = [];

    public MeasurementHistoryView(MeasurementHistoryStore history)
    {
        _history = history;
        ShowPrompt = dialog => dialog.ShowDialog((IWin32Window?)FindForm() ?? this);
        Name = "MeasurementHistory";
        AccessibleName = "Measurement history";
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        Padding = new Padding(0, 1, 0, 0);

        // Header: view switch on the left, compact active-session indicator on the right.
        _mode.Margin = new Padding(3, 4, 3, 4);
        _activeSession.AutoSize = true;
        _activeSession.AutoEllipsis = true;
        _activeSession.MaximumSize = new Size(300, 0);
        _activeSession.Anchor = AnchorStyles.Right;
        _activeSession.Margin = new Padding(8, 0, 3, 0);
        var header = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.Controls.Add(_mode, 0, 0);
        header.Controls.Add(_activeSession, 1, 0);

        // Sessions toolbar: pick a session, start a new one, and act on the selected one.
        _sessionPicker.DropDownStyle = ComboBoxStyle.DropDownList;
        _sessionPicker.Width = 290;
        _sessionPicker.AccessibleName = "Session";
        _sessionPicker.Margin = new Padding(3, 0, 6, 0);
        _sessionMore.MinimumSize = new Size(TrackerTheme.ControlHeight + 6, TrackerTheme.ControlHeight);
        _sessionMore.Margin = new Padding(6, 0, 0, 0);
        _newSession.Margin = new Padding(0, 0, 6, 0);
        _toggleActive.Margin = Padding.Empty;
        foreach (var bar in new[] { _sessionsToolbar, _allToolbar })
        {
            bar.AutoSize = true;
            bar.Dock = DockStyle.Fill;
            bar.WrapContents = false;
            bar.Margin = new Padding(0, 4, 0, 6);
        }
        _sessionsToolbar.Controls.AddRange([InlineLabel("Session"), _sessionPicker, _newSession, _toggleActive, _sessionMore]);
        _sessionMenu.Items.Add("Rename…", null, (_, _) => RenameSession());
        _sessionMenu.Items.Add("Delete…", null, (_, _) => DeleteSession());
        _sessionMore.ContextMenuStrip = _sessionMenu;

        _abilityFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _abilityFilter.Width = 130;
        _abilityFilter.AccessibleName = "Ability";
        _abilityFilter.Margin = new Padding(3, 0, 0, 0);
        _abilityFilter.Items.Add(new AbilityFilter(null, "All abilities"));
        foreach (var ability in Enum.GetValues<AbilityKind>())
            _abilityFilter.Items.Add(new AbilityFilter(ability, AbilityName(ability)));
        _allToolbar.Controls.AddRange([InlineLabel("Ability"), _abilityFilter]);
        _assignSession.ContextMenuStrip = _assignMenu;

        ConfigureGrid();

        _note.Multiline = true;
        _note.ScrollBars = ScrollBars.None;
        _note.Dock = DockStyle.Fill;
        _note.Height = 44;
        _note.MaxLength = 240;
        _note.AccessibleName = "Measurement note";
        _note.PlaceholderText = "Note for the selected measurement";
        _note.BackColor = TrackerTheme.Surface;
        foreach (var button in new[] { _newSession, _toggleActive, _sessionMore, _saveNote, _assignSession, _addColumn, _export })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(Math.Max(button.MinimumSize.Width, 80), TrackerTheme.ControlHeight);
            button.Padding = new Padding(6, 0, 6, 0);
        }
        _sessionMore.MinimumSize = new Size(TrackerTheme.ControlHeight + 6, TrackerTheme.ControlHeight);
        _sessionMore.Padding = Padding.Empty;
        var noteActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            Padding = new Padding(6, 3, 0, 0)
        };
        noteActions.Controls.AddRange([_saveNote, _assignSession]);
        var noteRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, RowCount = 1, Padding = new Padding(0, 4, 0, 0) };
        noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        noteRow.Controls.Add(new InputFrame(_note) { Dock = DockStyle.Fill, Height = 44, MinimumSize = new Size(80, 44) }, 0, 0);
        noteRow.Controls.Add(noteActions, 1, 0);

        var viewActions = new FlowLayoutPanel { Name = "ViewActions", AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        viewActions.Controls.AddRange([_addColumn, _export]);
        _status.AutoSize = true;
        _status.ForeColor = TrackerTheme.Muted;
        _status.Padding = new Padding(0, 4, 0, 0);
        _status.Margin = new Padding(3, 0, 3, 0);
        _status.MaximumSize = new Size(600, 0);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        footer.Controls.Add(noteRow, 0, 0);
        footer.Controls.Add(viewActions, 0, 1);
        footer.Controls.Add(_status, 0, 2);

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(0, 4, 0, 0) };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(_sessionsToolbar, 0, 1);
        root.Controls.Add(_allToolbar, 0, 2);
        root.Controls.Add(_measurements, 0, 3);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);
        TrackerTheme.Apply(this);
        TrackerTheme.Primary(_newSession);
        TrackerTheme.SetTypography(_activeSession, 9, FontStyle.Regular);
        Paint += (_, args) =>
        {
            using var pen = new Pen(TrackerTheme.Border);
            args.Graphics.DrawLine(pen, 0, 0, Width, 0);
        };

        _mode.SelectionChanged += (_, _) => ApplyMode();
        _sessionPicker.SelectedIndexChanged += (_, _) =>
        {
            RebuildCustomColumns();
            RefreshMeasurements();
        };
        _abilityFilter.SelectedIndexChanged += (_, _) => RefreshMeasurements();
        _measurements.SelectionChanged += (_, _) => LoadSelectedNote();
        _measurements.CellClick += (_, args) =>
        {
            if (args.RowIndex < 0 || _measurements.Columns[args.ColumnIndex].Name != "Result" ||
                _measurements.Rows[args.RowIndex].Tag is not Guid id ||
                _history.Measurements.FirstOrDefault(item => item.Id == id) is not { } clicked ||
                clicked.RepeatTestId != clicked.Id)
            {
                return;
            }

            if (!_expandedTests.Remove(id)) _expandedTests.Add(id);
            RefreshMeasurements(id);
        };
        _newSession.Click += (_, _) => NewSession();
        _toggleActive.Click += (_, _) => ToggleActive();
        _sessionMore.Click += (_, _) => ShowMenu(_sessionMenu, _sessionMore);
        _saveNote.Click += (_, _) => SaveSelectedNote();
        _assignSession.Click += (_, _) => ShowMenu(_assignMenu, _assignSession);
        _addColumn.Click += (_, _) => AddColumn();
        _export.Click += (_, _) => ExportCsv();
        _measurements.CellValidating += (_, args) =>
        {
            if (!SaveCustomCell(args.RowIndex, args.ColumnIndex, args.FormattedValue?.ToString() ?? ""))
                args.Cancel = true;
        };
        _measurements.CellEndEdit += (_, args) =>
        {
            var cell = _measurements.Rows[args.RowIndex].Cells[args.ColumnIndex];
            cell.ErrorText = SaveCustomCell(args.RowIndex, args.ColumnIndex, cell.Value?.ToString() ?? "")
                ? "" : "This value could not be saved. Edit it to retry before exporting.";
        };
        _measurements.EditingControlShowing += (_, args) =>
        {
            args.Control.BackColor = TrackerTheme.Surface;
            args.Control.ForeColor = TrackerTheme.Text;
        };

        _abilityFilter.SelectedIndex = 0;
        _mode.SelectedIndex = SessionsView;
        RefreshSessionChoices();
        ApplyMode();
    }

    /// <summary>Shows a modal prompt and returns its result. Tests replace it to answer without a window.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<PromptDialog, DialogResult> ShowPrompt { get; set; }

    /// <summary>The selected view: <see cref="SessionsView"/> or <see cref="AllView"/>.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal int ViewMode
    {
        get => _mode.SelectedIndex;
        set
        {
            _mode.SelectedIndex = value;
            ApplyMode();
        }
    }

    internal Guid? SelectedSessionId => (_sessionPicker.SelectedItem as SessionChoice)?.Id;

    internal ContextMenuStrip SessionMenu => _sessionMenu;

    internal ContextMenuStrip AssignMenu => _assignMenu;

    private Guid? SelectedMeasurementId => _measurements.SelectedRows.Count == 1
        ? _measurements.SelectedRows[0].Tag as Guid?
        : null;

    internal void UseStore(MeasurementHistoryStore history)
    {
        _history = history;
        RefreshSessionChoices();
        ApplyMode();
    }

    /// <summary>Re-reads the store unless the user is mid-edit, keeping the view, selection and filters.</summary>
    internal void Reload()
    {
        if (_measurements.IsCurrentCellInEditMode || _note.Modified || _note.Focused) return;
        var selected = SelectedMeasurementId;
        RefreshSessionChoices();
        RebuildCustomColumns();
        RefreshMeasurements(selected);
    }

    /// <summary>Selects a session in the Sessions view.</summary>
    internal void SelectSession(Guid sessionId)
    {
        _mode.SelectedIndex = SessionsView;
        RefreshSessionChoices(sessionId);
        ApplyMode();
    }

    private void ApplyMode()
    {
        var sessions = _mode.SelectedIndex == SessionsView;
        _sessionsToolbar.Visible = sessions;
        _allToolbar.Visible = !sessions;
        _addColumn.Visible = sessions;
        _assignSession.Visible = !sessions;
        _measurements.Columns["Session"]!.Visible = !sessions;
        RebuildCustomColumns();
        RefreshMeasurements();
    }

    private void ConfigureGrid()
    {
        _measurements.Dock = DockStyle.Fill;
        _measurements.ReadOnly = false;
        _measurements.AllowUserToAddRows = false;
        _measurements.AllowUserToDeleteRows = false;
        _measurements.AllowUserToResizeRows = false;
        _measurements.MultiSelect = false;
        _measurements.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _measurements.RowHeadersVisible = false;
        _measurements.AutoGenerateColumns = false;
        _measurements.BackgroundColor = TrackerTheme.Surface;
        _measurements.BorderStyle = BorderStyle.FixedSingle;
        _measurements.GridColor = TrackerTheme.Border;
        _measurements.DefaultCellStyle.BackColor = TrackerTheme.Surface;
        _measurements.DefaultCellStyle.ForeColor = TrackerTheme.Text;
        _measurements.DefaultCellStyle.SelectionBackColor = Color.FromArgb(55, 75, 90);
        _measurements.DefaultCellStyle.SelectionForeColor = TrackerTheme.Text;
        _measurements.ColumnHeadersDefaultCellStyle.BackColor = TrackerTheme.Background;
        _measurements.ColumnHeadersDefaultCellStyle.ForeColor = TrackerTheme.Text;
        _measurements.ColumnHeadersDefaultCellStyle.SelectionBackColor = TrackerTheme.Background;
        _measurements.ColumnHeadersDefaultCellStyle.SelectionForeColor = TrackerTheme.Text;
        _measurements.ColumnHeadersBorderStyle = DataGridViewHeaderBorderStyle.Single;
        _measurements.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
        _measurements.ColumnHeadersHeight = 28;
        _measurements.CellBorderStyle = DataGridViewCellBorderStyle.SingleHorizontal;
        _measurements.RowTemplate.Height = 28;
        _measurements.EnableHeadersVisualStyles = false;
        foreach (var bar in _measurements.Controls.OfType<ScrollBar>())
            _ = new ScrollBarSkin(bar);
        AddTextColumn("Ability", "Ability", 58);
        AddTextColumn("Result", "Result", 86);
        AddTextColumn("Session", "Session", 100);
        AddTextColumn("Measured", "Measured ±", 108);
        AddTextColumn("Regression", "Regression ±", 108);
        AddTextColumn("Time", "Measured at", 82);
        AddTextColumn("Note", "Note", 80, minimumWidth: 80, fill: true);
        _measurements.Columns["Measured"]!.ToolTipText = "Observed event-to-event duration ± measurement uncertainty, in seconds.";
        _measurements.Columns["Regression"]!.ToolTipText = "Full duration fitted from 10–90% charge ± regression uncertainty, in seconds.";
    }

    /// <summary>Shows the selected session's own custom columns (Sessions view only).</summary>
    private void RebuildCustomColumns()
    {
        var columns = _mode.SelectedIndex == SessionsView && SelectedSessionId is { } id
            ? _history.GetCustomColumns(id) : [];
        if (columns.SequenceEqual(_visibleColumns) &&
            _measurements.Columns.Cast<DataGridViewColumn>().All(column => column.Tag is not string || column.ReadOnly == !_history.CanWrite))
            return;
        foreach (var column in _measurements.Columns.Cast<DataGridViewColumn>().Where(item => item.Tag is string).ToArray())
            _measurements.Columns.Remove(column);
        foreach (var name in columns)
        {
            _measurements.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"Custom{_measurements.Columns.Count}",
                HeaderText = name,
                Tag = name,
                ReadOnly = !_history.CanWrite,
                Width = 80,
                MinimumWidth = 72,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }

        _visibleColumns = columns;
    }

    private bool Ask(PromptDialog dialog)
    {
        using (dialog) return ShowPrompt(dialog) == DialogResult.OK;
    }

    private void NewSession()
    {
        string name;
        using (var dialog = new PromptDialog("New session",
            "New measurements join this session until you end it or make another session active.",
            "Start session", placeholder: "e.g. Grenade stat 70", maxLength: 80))
        {
            if (ShowPrompt(dialog) != DialogResult.OK) return;
            name = dialog.Value;
        }

        try
        {
            var session = _history.CreateSession(name, DateTimeOffset.UtcNow);
            SelectSession(session.Id);
            SetStatus($"Started “{session.Name}”. New measurements join it automatically.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't start session: {exception.Message}", error: true);
        }
    }

    private void ToggleActive()
    {
        if (SelectedSessionId is not { } id) return;
        try
        {
            if (id == _history.ActiveSessionId)
            {
                _history.EndActiveSession(DateTimeOffset.UtcNow);
                SetStatus("Session ended. New measurements stay unsorted until a session is active.");
            }
            else
            {
                _history.SetActiveSession(id, DateTimeOffset.UtcNow);
                SetStatus("Session is active. New measurements join it automatically.");
            }

            RefreshSessionChoices(id);
            UpdateActionStates();
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't change the active session: {exception.Message}", error: true);
        }
    }

    private void RenameSession()
    {
        if (SelectedSession is not { } session) return;
        string name;
        using (var dialog = new PromptDialog("Rename session", "Enter a new name for this session.", "Rename",
            placeholder: "Session name", initialValue: session.Name, maxLength: 80))
        {
            if (ShowPrompt(dialog) != DialogResult.OK) return;
            name = dialog.Value;
        }

        try
        {
            _history.RenameSession(session.Id, name);
            RefreshSessionChoices(session.Id);
            RefreshMeasurements(SelectedMeasurementId);
            SetStatus("Session renamed.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't rename session: {exception.Message}", error: true);
        }
    }

    private void DeleteSession()
    {
        if (SelectedSession is not { } session) return;
        var count = _history.Measurements.Count(item => item.SessionId == session.Id);
        if (!Ask(new PromptDialog("Delete session",
            $"Delete “{session.Name}” and its columns? Its {Results(count)} and their notes move to Unsorted in All measurements.",
            "Delete session", destructive: true)))
            return;
        try
        {
            _history.DeleteSession(session.Id);
            RefreshSessionChoices();
            RebuildCustomColumns();
            RefreshMeasurements();
            SetStatus($"Deleted “{session.Name}”; its measurements are now unsorted.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't delete session: {exception.Message}", error: true);
        }
    }

    private void AddColumn()
    {
        if (SelectedSession is not { } session) return;
        string name;
        using (var dialog = new PromptDialog("Add column",
            $"Name a new column for “{session.Name}”. Double-click its cells to edit values; this session's CSV export includes it.",
            "Add column", placeholder: "e.g. Grenade Stat"))
        {
            if (ShowPrompt(dialog) != DialogResult.OK || dialog.Value.Length == 0) return;
            name = dialog.Value;
        }

        try
        {
            _history.AddCustomColumn(session.Id, name);
            RebuildCustomColumns();
            RefreshMeasurements(SelectedMeasurementId);
            SetStatus("Column added. Double-click its cells to edit values.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't add column: {exception.Message}", error: true);
        }
    }

    private bool SaveCustomCell(int rowIndex, int columnIndex, string value)
    {
        if (rowIndex < 0 || columnIndex < 0 || _measurements.Columns[columnIndex].Tag is not string column ||
            _measurements.Rows[rowIndex].Tag is not Guid id) return true;
        try
        {
            if (_history.Measurements.Single(item => item.Id == id).CustomValues?.GetValueOrDefault(column) == value)
                return true;
            _history.SaveCustomValue(id, column, value);
            SetStatus("Custom value saved.");
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't save value: {exception.Message}", error: true);
            return false;
        }
    }

    /// <summary>CSV of the rows on screen: the session's custom columns in Sessions, base columns only in All measurements.</summary>
    internal string ExportVisibleCsv()
    {
        if (!_measurements.EndEdit()) throw new InvalidOperationException("Finish editing the current cell before exporting.");
        if (_measurements.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>())
            .Any(cell => !string.IsNullOrEmpty(cell.ErrorText)))
            throw new InvalidOperationException("A custom value could not be saved. Edit that cell to retry before exporting.");
        var ids = _measurements.Rows.Cast<DataGridViewRow>().Select(row => (Guid)row.Tag!).ToArray();
        var records = _history.Measurements.ToDictionary(item => item.Id);
        return MeasurementCsv.Create(ids.Select(id => records[id]), _history.Sessions, _visibleColumns);
    }

    private void ExportCsv()
    {
        var fileName = SelectedSession is { } session && _mode.SelectedIndex == SessionsView
            ? string.Concat(session.Name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)) + ".csv"
            : "ability-measurements.csv";
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = fileName,
            DefaultExt = "csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, ExportVisibleCsv(), new System.Text.UTF8Encoding(true));
            SetStatus($"Exported {Results(_measurements.Rows.Count)} to {dialog.FileName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Couldn't export: {exception.Message}", error: true);
        }
    }

    private void SaveSelectedNote()
    {
        if (SelectedMeasurementId is not { } id) return;
        try
        {
            _history.SaveNote(id, _note.Text);
            _note.Modified = false;
            RefreshMeasurements(id);
            SetStatus("Note saved.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't save note: {exception.Message}", error: true);
        }
    }

    private void AssignSelectedMeasurement(Guid? sessionId)
    {
        if (SelectedMeasurementId is not { } id) return;
        try
        {
            _history.AssignToSession(id, sessionId);
            RefreshSessionChoices();
            RefreshMeasurements(id);
            SetStatus(sessionId is { } target
                ? $"Added to “{_history.Sessions.Single(session => session.Id == target).Name}”."
                : "Measurement is now unsorted.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            SetStatus($"Couldn't move measurement: {exception.Message}", error: true);
        }
    }

    private MeasurementHistorySession? SelectedSession =>
        SelectedSessionId is { } id ? _history.Sessions.FirstOrDefault(session => session.Id == id) : null;

    private void RefreshSessionChoices(Guid? preferredSessionId = null)
    {
        var sessions = _history.Sessions.OrderByDescending(session => session.StartedAt).ToArray();
        var measurements = _history.Measurements;
        var activeId = _history.ActiveSessionId;
        var previous = preferredSessionId ?? SelectedSessionId;
        _sessionPicker.BeginUpdate();
        _sessionPicker.Items.Clear();
        foreach (var session in sessions)
        {
            var count = measurements.Count(item => item.SessionId == session.Id);
            var active = session.Id == activeId ? " · Active" : "";
            _sessionPicker.Items.Add(new SessionChoice(session.Id,
                $"{session.Name} · {session.StartedAt.ToLocalTime():MMM d} · {Results(count)}{active}"));
        }

        var choices = _sessionPicker.Items.Cast<SessionChoice>().ToArray();
        _sessionPicker.SelectedItem = choices.FirstOrDefault(choice => choice.Id == previous)
            ?? choices.FirstOrDefault(choice => choice.Id == activeId) ?? choices.FirstOrDefault();
        _sessionPicker.EndUpdate();

        var activeSession = sessions.FirstOrDefault(session => session.Id == activeId);
        _activeSession.Text = activeSession is null ? "No active session" : $"● Active: {activeSession.Name}";
        _activeSession.ForeColor = activeSession is null ? TrackerTheme.Muted : TrackerTheme.Text;

        _assignMenu.Items.Clear();
        foreach (var session in sessions)
        {
            var item = new ToolStripMenuItem(session.Id == activeId ? $"{session.Name} · Active" : session.Name)
            {
                Tag = session.Id
            };
            var target = session.Id;
            item.Click += (_, _) => AssignSelectedMeasurement(target);
            _assignMenu.Items.Add(item);
        }

        if (sessions.Length > 0) _assignMenu.Items.Add(new ToolStripSeparator());
        _assignMenu.Items.Add("Unsorted", null, (_, _) => AssignSelectedMeasurement(null));

        if (_history.LoadError is { } loadError)
            SetStatus($"Couldn't load measurement history; the file was left unchanged: {loadError}", error: true);
        UpdateActionStates();
    }

    private readonly HashSet<Guid> _expandedTests = [];

    /// <summary>
    /// Newest first, with each repeat test's cycles placed (oldest first) right after the test's mean row. Cycles
    /// whose test has no mean (stopped early) stay where their time puts them.
    /// </summary>
    internal static MeasurementHistoryEntry[] GroupRepeatTests(IEnumerable<MeasurementHistoryEntry> measurements)
    {
        var all = measurements.ToArray();
        var summaries = all.Where(item => item.Id == item.RepeatTestId).Select(item => item.Id).ToHashSet();
        var cycles = all.Where(item => item.RepeatTestId is { } test && item.Id != test && summaries.Contains(test))
            .GroupBy(item => item.RepeatTestId!.Value)
            .ToDictionary(group => group.Key, group => group.OrderBy(item => item.MeasuredAt).ToArray());
        var ordered = new List<MeasurementHistoryEntry>();
        foreach (var item in all.Where(item => !(item.RepeatTestId is { } test && item.Id != test && summaries.Contains(test)))
                     .OrderByDescending(item => item.MeasuredAt))
        {
            ordered.Add(item);
            if (summaries.Contains(item.Id) && cycles.TryGetValue(item.Id, out var members)) ordered.AddRange(members);
        }

        return [.. ordered];
    }

    private void RefreshMeasurements(Guid? preferredMeasurementId = null)
    {
        var sessionsView = _mode.SelectedIndex == SessionsView;
        var sessionId = SelectedSessionId;
        var ability = (_abilityFilter.SelectedItem as AbilityFilter)?.Ability;
        var sessionNames = _history.Sessions.ToDictionary(session => session.Id, session => session.Name);
        var records = _history.Measurements
            .Where(measurement => sessionsView
                ? sessionId is not null && measurement.SessionId == sessionId
                : ability is null || measurement.Ability == ability)
            .ToArray();
        records = GroupRepeatTests(records);
        _measurements.Rows.Clear();
        var cycleNumbers = new Dictionary<Guid, int>();
        foreach (var measurement in records)
        {
            var isSummary = measurement.Id == measurement.RepeatTestId;
            var groupId = measurement.RepeatTestId;
            var grouped = !isSummary && groupId is { } owner0 && records.Any(item => item.Id == owner0);
            var cycleNumber = groupId is { } test && !isSummary
                ? cycleNumbers[test] = cycleNumbers.GetValueOrDefault(test) + 1
                : 0;
            var regression = measurement.RegressionSeconds is { } seconds ? $"{seconds:F2}" : "—";
            var regressionUncertainty = measurement.RegressionUncertaintySeconds is { } regressionSpread
                ? $" ± {regressionSpread:F3} s"
                : string.Empty;
            var sessionName = measurement.SessionId is { } owner && sessionNames.TryGetValue(owner, out var name) ? name : null;
            var index = _measurements.Rows.Add(
                AbilityName(measurement.Ability),
                isSummary ? $"{(_expandedTests.Contains(measurement.Id) ? "▾" : "▸")} Mean · {measurement.CycleCount}" :
                    grouped ? $"    Cycle {cycleNumber}" :
                    measurement.RepeatTestId is not null ? "Cycle" : "Single",
                sessionName ?? "Unsorted",
                $"{measurement.DurationSeconds:F2} ± {measurement.UncertaintySeconds:F3} s",
                regression + regressionUncertainty,
                measurement.MeasuredAt.ToLocalTime().ToString("MMM d HH:mm"),
                measurement.Note ?? string.Empty);
            var row = _measurements.Rows[index];
            row.Tag = measurement.Id;
            if (sessionName is null) row.Cells["Session"].Style.ForeColor = TrackerTheme.Muted;
            foreach (DataGridViewColumn column in _measurements.Columns)
                if (column.Tag is string customName)
                    row.Cells[column.Index].Value = measurement.CustomValues?.GetValueOrDefault(customName) ?? "";
            if (grouped)
            {
                // Cycles sit under their repeat test's mean row and show when it is expanded.
                row.Visible = _expandedTests.Contains(groupId!.Value);
                row.DefaultCellStyle.ForeColor = TrackerTheme.Muted;
            }

            if (isSummary)
            {
                row.DefaultCellStyle.ForeColor = TrackerTheme.Accent;
                var clickHint = _expandedTests.Contains(measurement.Id) ? " Click to hide its cycles." : " Click to show its cycles.";
                row.Cells["Result"].ToolTipText = $"Repeat-test mean of {measurement.CycleCount} validated cycles. Uncertainty combines the mean per-cycle uncertainty floor with the standard error from cycle scatter." + clickHint;
                row.Cells["Regression"].ToolTipText = $"Regression mean uses {measurement.RegressionCycleCount}/{measurement.CycleCount} cycles with validated 10–90% fits.";
            }
        }

        var preferredRow = _measurements.Rows.Cast<DataGridViewRow>()
            .FirstOrDefault(row => row.Visible && preferredMeasurementId is { } id && row.Tag is Guid rowId && rowId == id)
            ?? _measurements.Rows.Cast<DataGridViewRow>().FirstOrDefault(row => row.Visible);
        if (preferredRow is not null)
        {
            preferredRow.Selected = true;
            _measurements.CurrentCell = preferredRow.Cells[0];
        }

        LoadSelectedNote();
        if (records.Length == 0 && _history.LoadError is null)
        {
            SetStatus(!sessionsView ? "No measurements match this filter."
                : sessionId is null ? "No sessions yet. Choose New session… to group measurements while you test."
                : "No measurements in this session yet. New results join the active session automatically.");
        }
        else if (_history.LoadError is null)
        {
            SetStatus(sessionsView
                ? $"{Results(records.Length)} in this session."
                : $"{Results(records.Length)}. Use Add to session to sort a result into any session.");
        }

        UpdateActionStates();
    }

    private void LoadSelectedNote()
    {
        var measurement = SelectedMeasurementId is { } selectedId
            ? _history.Measurements.FirstOrDefault(item => item.Id == selectedId)
            : null;
        _note.Text = measurement?.Note ?? string.Empty;
        _note.Modified = false;
        UpdateActionStates();
    }

    private void UpdateActionStates()
    {
        var writable = _history.CanWrite;
        var hasSelection = SelectedMeasurementId is not null;
        var session = SelectedSessionId;
        _newSession.Enabled = writable;
        _toggleActive.Text = session is not null && session == _history.ActiveSessionId ? "End session" : "Make active";
        _toggleActive.Enabled = writable && session is not null;
        foreach (ToolStripItem item in _sessionMenu.Items) item.Enabled = writable && session is not null;
        _addColumn.Enabled = writable && session is not null;
        _export.Enabled = _measurements.Rows.Count > 0;
        _saveNote.Enabled = writable && hasSelection;
        _note.ReadOnly = !writable || !hasSelection;
        _assignSession.Enabled = writable && hasSelection;
    }

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.ForeColor = error ? TrackerTheme.Red : TrackerTheme.Muted;
    }

    private static void ShowMenu(ContextMenuStrip menu, Control anchor) =>
        menu.Show(anchor, new Point(0, anchor.Height + 2), ToolStripDropDownDirection.BelowRight);

    private void AddTextColumn(string name, string header, int width, int minimumWidth = 5, bool fill = false)
    {
        _measurements.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            MinimumWidth = minimumWidth,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable,
            ReadOnly = true
        });
    }

    private static Label InlineLabel(string text) => new()
    {
        Text = text,
        AutoSize = true,
        ForeColor = TrackerTheme.Muted,
        Anchor = AnchorStyles.Left,
        Margin = new Padding(3, 7, 2, 0)
    };

    private static string Results(int count) => count == 1 ? "1 result" : $"{count} results";

    private static string AbilityName(AbilityKind ability) => ability switch
    {
        AbilityKind.Grenade => "Grenade",
        AbilityKind.Melee => "Melee",
        AbilityKind.ClassAbility => "Class",
        _ => ability.ToString()
    };

    private sealed record AbilityFilter(AbilityKind? Ability, string Label)
    {
        public override string ToString() => Label;
    }

    private sealed record SessionChoice(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }
}

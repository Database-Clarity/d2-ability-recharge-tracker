namespace D2AbilityTracker;

internal sealed class MeasurementHistoryView : UserControl
{
    private MeasurementHistoryStore _history;
    private readonly FlatComboBox _abilityFilter = new();
    private readonly FlatComboBox _sessionFilter = new();
    private readonly TextBox _sessionName = new();
    private readonly Button _startSession = new();
    private readonly Button _endSession = new();
    private readonly Button _setActiveSession = new();
    private readonly Button _saveNote = new();
    private readonly Button _assignSession = new();
    private readonly DataGridView _measurements = new();
    private readonly TextBox _note = new();
    private readonly Label _activeSession = new();
    private readonly Label _status = new();
    private readonly Button _addColumn = new();
    private readonly Button _export = new();

    public MeasurementHistoryView(MeasurementHistoryStore history)
    {
        _history = history;
        Name = "MeasurementHistory";
        AccessibleName = "Measurement history";
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        Padding = new Padding(0, 1, 0, 0);

        _abilityFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _abilityFilter.Width = 120;
        _abilityFilter.Items.Add(new AbilityFilter(null, "All abilities"));
        foreach (var ability in Enum.GetValues<AbilityKind>())
        {
            _abilityFilter.Items.Add(new AbilityFilter(ability, AbilityName(ability)));
        }

        _sessionFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _sessionFilter.Width = 200;
        _sessionName.PlaceholderText = "New session name";
        _sessionName.AccessibleName = "New session name";
        _sessionName.Width = 150;
        _sessionName.BackColor = TrackerTheme.Surface;
        _startSession.Text = "Start session";
        _endSession.Text = "End session";
        _setActiveSession.Text = "Make active";

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };
        var sessionActions = new FlowLayoutPanel
        {
            Name = "SessionActions",
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            WrapContents = false,
            Margin = Padding.Empty
        };
        sessionActions.Controls.AddRange([new InputFrame(_sessionName), _startSession]);
        var sessionView = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = Padding.Empty };
        sessionView.Controls.AddRange([_sessionFilter, _setActiveSession]);
        filters.Controls.AddRange([Field("Ability", _abilityFilter), Field("Session filter", sessionView)]);

        _activeSession.AutoSize = true;
        _activeSession.AutoEllipsis = true;
        _activeSession.MaximumSize = new Size(240, 0);
        _activeSession.ForeColor = TrackerTheme.Text;
        _activeSession.Margin = new Padding(4, 9, 6, 4);
        sessionActions.Margin = new Padding(12, 0, 0, 0);
        var activeSessionRow = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = true,
            Margin = new Padding(0, 2, 0, 6)
        };
        activeSessionRow.Controls.AddRange([_activeSession, _endSession, sessionActions]);
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
        AddTextColumn("Result", "Result", 68);
        AddTextColumn("Measured", "Measured ±", 112);
        AddTextColumn("Regression", "Regression ±", 112);
        AddTextColumn("Time", "Measured at", 90);
        AddTextColumn("Note", "Note", 104, minimumWidth: 104, fill: true);
        AddCustomColumns();
        _measurements.Columns["Measured"]!.ToolTipText = "Observed event-to-event duration ± measurement uncertainty, in seconds.";
        _measurements.Columns["Regression"]!.ToolTipText = "Full duration fitted from 10–90% charge ± regression uncertainty, in seconds.";

        _note.Multiline = true;
        _note.ScrollBars = ScrollBars.None;
        _note.Dock = DockStyle.Fill;
        _note.Height = 44;
        _note.MaxLength = 240;
        _note.AccessibleName = "Measurement note";
        _note.BackColor = TrackerTheme.Surface;
        _saveNote.Text = "Save note";
        _assignSession.Text = "Add to session";
        foreach (var button in new[] { _startSession, _endSession, _setActiveSession, _saveNote, _assignSession })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(80, TrackerTheme.ControlHeight);
            button.Padding = new Padding(6, 0, 6, 0);
        }
        var noteActions = new FlowLayoutPanel
        {
            AutoSize = true,
            Dock = DockStyle.Fill,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            Padding = new Padding(6, 3, 0, 0)
        };
        noteActions.Controls.AddRange([_saveNote, _assignSession]);
        var noteRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 1,
            Padding = new Padding(0, 2, 0, 0)
        };
        noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        noteRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        var noteFrame = new InputFrame(_note) { Dock = DockStyle.Fill, Height = 44, MinimumSize = new Size(80, 44) };
        noteRow.Controls.Add(noteFrame, 0, 0);
        noteRow.Controls.Add(noteActions, 1, 0);

        _status.AutoSize = true;
        _status.ForeColor = TrackerTheme.Muted;
        _status.Padding = new Padding(0, 4, 0, 0);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(0, 4, 0, 0)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new Label
        {
            Text = "Measurement history",
            AutoSize = true,
            ForeColor = TrackerTheme.Muted,
            Margin = new Padding(3, 4, 3, 6)
        };
        TrackerTheme.SetTypography(heading, 9, FontStyle.Bold);
        root.Controls.Add(heading, 0, 0);
        root.Controls.Add(filters, 0, 1);
        root.Controls.Add(activeSessionRow, 0, 2);
        root.Controls.Add(_measurements, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        footer.Controls.Add(noteRow, 0, 0);
        var columnActions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = true };
        _addColumn.Text = "Add column…";
        _addColumn.AutoSize = true;
        _export.Text = "Export CSV…";
        _export.AutoSize = true;
        columnActions.Controls.AddRange([_addColumn, _export]);
        footer.Controls.Add(columnActions, 0, 1);
        footer.Controls.Add(_status, 0, 2);
        root.Controls.Add(footer, 0, 4);
        Controls.Add(root);
        TrackerTheme.Apply(this);
        Paint += (_, args) =>
        {
            using var pen = new Pen(TrackerTheme.Border);
            args.Graphics.DrawLine(pen, 0, 0, Width, 0);
        };

        _abilityFilter.SelectedIndexChanged += (_, _) => RefreshMeasurements();
        _sessionFilter.SelectedIndexChanged += (_, _) => RefreshMeasurements();
        _measurements.SelectionChanged += (_, _) => LoadSelectedNote();
        _startSession.Click += (_, _) => StartSession();
        _endSession.Click += (_, _) => EndSession();
        _setActiveSession.Click += (_, _) => SetActiveSession();
        _saveNote.Click += (_, _) => SaveSelectedNote();
        _assignSession.Click += (_, _) => AssignSelectedMeasurement();
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
        RefreshSessionChoices();
        RefreshMeasurements();
    }

    private Guid? SelectedMeasurementId => _measurements.SelectedRows.Count == 1
        ? _measurements.SelectedRows[0].Tag as Guid?
        : null;

    internal void UseStore(MeasurementHistoryStore history)
    {
        _history = history;
        foreach (var column in _measurements.Columns.Cast<DataGridViewColumn>().Where(item => item.Tag is string).ToArray())
            _measurements.Columns.Remove(column);
        AddCustomColumns();
        RefreshSessionChoices();
        RefreshMeasurements();
    }

    /// <summary>Re-reads the store unless the user is mid-edit, keeping filters and selection.</summary>
    internal void Reload()
    {
        if (_measurements.IsCurrentCellInEditMode || _note.Modified || _note.Focused) return;
        RefreshSessionChoices();
        RefreshMeasurements(SelectedMeasurementId);
    }

    private void AddCustomColumns()
    {
        foreach (var column in _history.CustomColumns)
        {
            if (_measurements.Columns.Cast<DataGridViewColumn>().Any(item => item.Tag as string == column)) continue;
            _measurements.Columns.Add(new DataGridViewTextBoxColumn
            {
                Name = $"Custom{_measurements.Columns.Count}",
                HeaderText = column,
                Tag = column,
                ReadOnly = !_history.CanWrite,
                Width = 80,
                MinimumWidth = 72,
                AutoSizeMode = DataGridViewAutoSizeColumnMode.Fill,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        }
    }

    /// <summary>Asks for a new column name; returns null when cancelled. Tests replace it to skip the modal dialog.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal Func<IWin32Window, string?> ColumnNamePrompt { get; set; } = owner =>
    {
        using var dialog = new ColumnNameDialog();
        return dialog.ShowDialog(owner) == DialogResult.OK ? dialog.ColumnName : null;
    };

    private void AddColumn()
    {
        if (ColumnNamePrompt(this) is not { Length: > 0 } name) return;
        try
        {
            _history.AddCustomColumn(name);
            AddCustomColumns();
            RefreshMeasurements(SelectedMeasurementId);
            SetStatus("Column added. Double-click its cells to edit values.");
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
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
        catch (Exception exception) when (exception is ArgumentException or IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Couldn't save value: {exception.Message}", error: true);
            return false;
        }
    }

    internal string ExportVisibleCsv()
    {
        if (!_measurements.EndEdit()) throw new InvalidOperationException("Finish editing the current cell before exporting.");
        if (_measurements.Rows.Cast<DataGridViewRow>().SelectMany(row => row.Cells.Cast<DataGridViewCell>())
            .Any(cell => !string.IsNullOrEmpty(cell.ErrorText)))
            throw new InvalidOperationException("A custom value could not be saved. Edit that cell to retry before exporting.");
        var ids = _measurements.Rows.Cast<DataGridViewRow>().Select(row => (Guid)row.Tag!).ToArray();
        var records = _history.Measurements.ToDictionary(item => item.Id);
        return MeasurementCsv.Create(ids.Select(id => records[id]), _history.Sessions, _history.CustomColumns);
    }

    private void ExportCsv()
    {
        using var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = "ability-measurements.csv",
            DefaultExt = "csv",
            AddExtension = true
        };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            File.WriteAllText(dialog.FileName, ExportVisibleCsv(), new System.Text.UTF8Encoding(true));
            SetStatus($"Exported visible measurements to {dialog.FileName}.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            SetStatus($"Couldn't export: {exception.Message}", error: true);
        }
    }

    private void StartSession()
    {
        try
        {
            var session = _history.CreateSession(_sessionName.Text, DateTimeOffset.UtcNow);
            _sessionName.Clear();
            RefreshSessionChoices(session.Id);
            RefreshMeasurements();
            SetStatus($"Started “{session.Name}”.");
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't start session: {exception.Message}", error: true);
        }
    }

    private void EndSession()
    {
        try
        {
            if (_history.EndActiveSession(DateTimeOffset.UtcNow))
            {
                RefreshSessionChoices();
                RefreshMeasurements();
                SetStatus("Active session ended.");
            }
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't end session: {exception.Message}", error: true);
        }
    }

    private void SetActiveSession()
    {
        if (_sessionFilter.SelectedItem is not SessionFilter { Kind: SessionFilterKind.Session, SessionId: { } sessionId })
        {
            return;
        }

        try
        {
            _history.SetActiveSession(sessionId);
            RefreshSessionChoices(sessionId);
            RefreshMeasurements();
            SetStatus("Active session updated.");
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't select active session: {exception.Message}", error: true);
        }
    }

    private void SaveSelectedNote()
    {
        if (SelectedMeasurementId is not { } id)
        {
            return;
        }

        try
        {
            _history.SaveNote(id, _note.Text);
            RefreshMeasurements(id);
            SetStatus("Note saved.");
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't save note: {exception.Message}", error: true);
        }
    }

    private void AssignSelectedMeasurement()
    {
        if (SelectedMeasurementId is not { } id)
        {
            return;
        }

        try
        {
            _history.AssignToActiveSession(id);
            RefreshSessionChoices();
            RefreshMeasurements(id);
            SetStatus("Measurement assigned to the active session.");
        }
        catch (Exception exception)
        {
            SetStatus($"Couldn't assign measurement: {exception.Message}", error: true);
        }
    }

    private void RefreshSessionChoices(Guid? preferredSessionId = null)
    {
        var previousChoice = _sessionFilter.SelectedItem as SessionFilter;
        var previousId = preferredSessionId ?? previousChoice?.SessionId;
        var previousKind = preferredSessionId is not null ? SessionFilterKind.Session : previousChoice?.Kind ?? SessionFilterKind.All;
        var sessions = _history.Sessions.OrderByDescending(session => session.StartedAt).ToArray();
        _sessionFilter.BeginUpdate();
        _sessionFilter.Items.Clear();
        _sessionFilter.Items.Add(new SessionFilter(SessionFilterKind.All, null, "All sessions"));
        _sessionFilter.Items.Add(new SessionFilter(SessionFilterKind.Unassigned, null, "Unassigned"));
        foreach (var session in sessions)
        {
            var active = session.Id == _history.ActiveSessionId ? " · active" : string.Empty;
            var ended = session.EndedAt is null ? string.Empty : " · ended";
            _sessionFilter.Items.Add(new SessionFilter(
                SessionFilterKind.Session,
                session.Id,
                $"{session.Name}{active}{ended}"));
        }

        var selection = _sessionFilter.Items.Cast<SessionFilter>()
            .FirstOrDefault(choice => choice.Kind == previousKind && choice.SessionId == previousId)
            ?? _sessionFilter.Items[0] as SessionFilter;
        _sessionFilter.SelectedItem = selection;
        _sessionFilter.EndUpdate();

        var activeSession = sessions.FirstOrDefault(session => session.Id == _history.ActiveSessionId);
        _activeSession.Text = activeSession is null
            ? "No active session"
            : $"Active session: {activeSession.Name}";
        if (_history.LoadError is { } loadError)
        {
            SetStatus($"Couldn't load measurement history; the file was left unchanged: {loadError}", error: true);
        }

        UpdateActionStates();
    }

    private void RefreshMeasurements(Guid? preferredMeasurementId = null)
    {
        var ability = (_abilityFilter.SelectedItem as AbilityFilter)?.Ability;
        var sessionFilter = _sessionFilter.SelectedItem as SessionFilter
            ?? new SessionFilter(SessionFilterKind.All, null, "All sessions");
        var records = _history.Measurements
            .Where(measurement => ability is null || measurement.Ability == ability)
            .Where(measurement => sessionFilter.Kind switch
            {
                SessionFilterKind.Unassigned => measurement.SessionId is null,
                SessionFilterKind.Session => measurement.SessionId == sessionFilter.SessionId,
                _ => true
            })
            .OrderByDescending(measurement => measurement.MeasuredAt)
            .ToArray();
        _measurements.Rows.Clear();
        foreach (var measurement in records)
        {
            var regression = measurement.RegressionSeconds is { } seconds
                ? $"{seconds:F2}"
                : "—";
            var regressionUncertainty = measurement.RegressionUncertaintySeconds is { } regressionSpread
                ? $" ± {regressionSpread:F3} s"
                : string.Empty;
            var index = _measurements.Rows.Add(
                AbilityName(measurement.Ability),
                measurement.Id == measurement.RepeatTestId ? $"Mean · {measurement.CycleCount}" :
                    measurement.RepeatTestId is not null ? "Cycle" : "Single",
                $"{measurement.DurationSeconds:F2} ± {measurement.UncertaintySeconds:F3} s",
                regression + regressionUncertainty,
                measurement.MeasuredAt.ToLocalTime().ToString("MMM d HH:mm"),
                measurement.Note ?? string.Empty);
            _measurements.Rows[index].Tag = measurement.Id;
            foreach (DataGridViewColumn column in _measurements.Columns)
                if (column.Tag is string customName)
                    _measurements.Rows[index].Cells[column.Index].Value = measurement.CustomValues?.GetValueOrDefault(customName) ?? "";
            if (measurement.Id == measurement.RepeatTestId)
            {
                var row = _measurements.Rows[index];
                row.DefaultCellStyle.ForeColor = TrackerTheme.Accent;
                row.Cells["Result"].ToolTipText = $"Repeat-test mean of {measurement.CycleCount} validated cycles. Uncertainty combines the mean per-cycle uncertainty floor with the standard error from cycle scatter.";
                row.Cells["Regression"].ToolTipText = $"Regression mean uses {measurement.RegressionCycleCount}/{measurement.CycleCount} cycles with validated 10–90% fits.";
            }
        }

        if (preferredMeasurementId is { } id)
        {
            foreach (DataGridViewRow row in _measurements.Rows)
            {
                if (row.Tag is Guid rowId && rowId == id)
                {
                    row.Selected = true;
                    _measurements.CurrentCell = row.Cells[0];
                    break;
                }
            }
        }
        else if (_measurements.Rows.Count > 0)
        {
            _measurements.Rows[0].Selected = true;
            _measurements.CurrentCell = _measurements.Rows[0].Cells[0];
        }

        LoadSelectedNote();
        if (records.Length == 0 && _history.LoadError is null)
        {
            SetStatus("No measurements match these filters.");
        }

        UpdateActionStates();
    }

    private void LoadSelectedNote()
    {
        var id = SelectedMeasurementId;
        var measurement = id is { } selectedId
            ? _history.Measurements.FirstOrDefault(item => item.Id == selectedId)
            : null;
        _note.Text = measurement?.Note ?? string.Empty;
        UpdateActionStates();
    }

    private void UpdateActionStates()
    {
        var writable = _history.CanWrite;
        var hasSelection = SelectedMeasurementId is not null;
        _startSession.Enabled = writable;
        _sessionName.Enabled = writable;
        _addColumn.Enabled = writable;
        _export.Enabled = _measurements.Rows.Count > 0;
        _endSession.Enabled = writable && _history.ActiveSessionId is not null;
        _setActiveSession.Enabled = writable &&
            _sessionFilter.SelectedItem is SessionFilter { Kind: SessionFilterKind.Session, SessionId: { } id } &&
            id != _history.ActiveSessionId &&
            _history.Sessions.Any(session => session.Id == id && session.EndedAt is null);
        _saveNote.Enabled = writable && hasSelection;
        _note.ReadOnly = !writable || !hasSelection;
        _assignSession.Enabled = writable && hasSelection && _history.ActiveSessionId is not null;
    }

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.ForeColor = error ? TrackerTheme.Red : TrackerTheme.Muted;
    }

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

    private static Control Field(string title, Control control)
    {
        var panel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            Margin = new Padding(0, 0, 8, 0)
        };
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            ForeColor = TrackerTheme.Muted,
            Margin = new Padding(2, 0, 0, 2)
        });
        panel.Controls.Add(control);
        return panel;
    }

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

    private enum SessionFilterKind
    {
        All,
        Unassigned,
        Session
    }

    private sealed record SessionFilter(SessionFilterKind Kind, Guid? SessionId, string Label)
    {
        public override string ToString() => Label;
    }
}

namespace D2AbilityTracker;

internal sealed class MeasurementHistoryForm : Form
{
    private readonly MeasurementHistoryStore _history;
    private readonly ComboBox _abilityFilter = new();
    private readonly ComboBox _sessionFilter = new();
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

    public MeasurementHistoryForm(MeasurementHistoryStore history)
    {
        _history = history;
        Text = "Measurement history";
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MinimumSize = new Size(800, 440);
        Size = new Size(980, 560);
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);

        _abilityFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _abilityFilter.Width = 125;
        _abilityFilter.Items.Add(new AbilityFilter(null, "All abilities"));
        foreach (var ability in Enum.GetValues<AbilityKind>())
        {
            _abilityFilter.Items.Add(new AbilityFilter(ability, AbilityName(ability)));
        }

        _sessionFilter.DropDownStyle = ComboBoxStyle.DropDownList;
        _sessionFilter.Width = 190;
        _sessionName.PlaceholderText = "New session name";
        _sessionName.Width = 145;
        _sessionName.BackColor = TrackerTheme.Surface;
        _startSession.Text = "Start session";
        _endSession.Text = "End active";
        _setActiveSession.Text = "Set active";

        var filters = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            WrapContents = true,
            Padding = new Padding(0, 0, 0, 4)
        };
        filters.Controls.AddRange([
            Field("Ability", _abilityFilter),
            Field("Session view", _sessionFilter),
            _sessionName,
            _startSession,
            _setActiveSession,
            _endSession
        ]);

        _activeSession.AutoSize = true;
        _activeSession.ForeColor = TrackerTheme.Muted;
        _activeSession.Margin = new Padding(4, 8, 0, 4);
        _measurements.Dock = DockStyle.Fill;
        _measurements.ReadOnly = true;
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
        _measurements.EnableHeadersVisualStyles = false;
        AddTextColumn("Ability", "Ability", 86);
        AddTextColumn("Measured", "Measured", 145);
        AddTextColumn("Regression", "Regression", 205);
        AddTextColumn("Uncertainty", "Uncertainty", 105);
        AddTextColumn("Time", "Measured at", 150);
        AddTextColumn("Note", "Note", 200, fill: true);

        _note.Multiline = true;
        _note.ScrollBars = ScrollBars.Vertical;
        _note.Dock = DockStyle.Fill;
        _note.Height = 54;
        _note.BackColor = TrackerTheme.Surface;
        _saveNote.Text = "Save note";
        _assignSession.Text = "Assign to active";
        foreach (var button in new[] { _startSession, _endSession, _setActiveSession, _saveNote, _assignSession })
        {
            button.AutoSize = true;
            button.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            button.MinimumSize = new Size(80, 28);
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
        noteRow.Controls.Add(_note, 0, 0);
        noteRow.Controls.Add(noteActions, 1, 0);

        _status.AutoSize = true;
        _status.ForeColor = TrackerTheme.Muted;
        _status.Padding = new Padding(0, 4, 0, 0);
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            Padding = new Padding(10)
        };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(filters, 0, 0);
        root.Controls.Add(_activeSession, 0, 1);
        root.Controls.Add(_measurements, 0, 2);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        footer.Controls.Add(noteRow, 0, 0);
        footer.Controls.Add(_status, 0, 1);
        root.Controls.Add(footer, 0, 3);
        Controls.Add(root);
        TrackerTheme.Apply(this);

        _abilityFilter.SelectedIndexChanged += (_, _) => RefreshMeasurements();
        _sessionFilter.SelectedIndexChanged += (_, _) => RefreshMeasurements();
        _measurements.SelectionChanged += (_, _) => LoadSelectedNote();
        _startSession.Click += (_, _) => StartSession();
        _endSession.Click += (_, _) => EndSession();
        _setActiveSession.Click += (_, _) => SetActiveSession();
        _saveNote.Click += (_, _) => SaveSelectedNote();
        _assignSession.Click += (_, _) => AssignSelectedMeasurement();

        _abilityFilter.SelectedIndex = 0;
        RefreshSessionChoices();
        RefreshMeasurements();
    }

    private Guid? SelectedMeasurementId => _measurements.SelectedRows.Count == 1
        ? _measurements.SelectedRows[0].Tag as Guid?
        : null;

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
        var previousKind = previousChoice?.Kind ?? SessionFilterKind.All;
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
                ? DurationDisplay.Format(TimeSpan.FromSeconds(seconds))
                : "—";
            var regressionUncertainty = measurement.RegressionUncertaintySeconds is { } regressionSpread
                ? $" ± {regressionSpread:F3} s"
                : string.Empty;
            var index = _measurements.Rows.Add(
                AbilityName(measurement.Ability),
                DurationDisplay.Format(TimeSpan.FromSeconds(measurement.DurationSeconds)),
                regression + regressionUncertainty,
                $"± {measurement.UncertaintySeconds:F3} s",
                measurement.MeasuredAt.ToLocalTime().ToString("g"),
                measurement.Note ?? string.Empty);
            _measurements.Rows[index].Tag = measurement.Id;
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
        _endSession.Enabled = writable && _history.ActiveSessionId is not null;
        _setActiveSession.Enabled = writable &&
            _sessionFilter.SelectedItem is SessionFilter { Kind: SessionFilterKind.Session, SessionId: { } id } &&
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

    private void AddTextColumn(string name, string header, int width, bool fill = false)
    {
        _measurements.Columns.Add(new DataGridViewTextBoxColumn
        {
            Name = name,
            HeaderText = header,
            Width = width,
            AutoSizeMode = fill ? DataGridViewAutoSizeColumnMode.Fill : DataGridViewAutoSizeColumnMode.None,
            SortMode = DataGridViewColumnSortMode.NotSortable
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

using System.Drawing;

namespace D2AbilityTracker;

/// <summary>Small dark prompt for a new custom History column name.</summary>
internal sealed class ColumnNameDialog : AppWindow
{
    private readonly TextBox _name = new();
    private readonly Button _ok = new();

    public ColumnNameDialog()
    {
        Text = "Add column";
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;

        var message = new Label
        {
            Text = "Name the new column. Double-click its cells in History to edit values; CSV exports include it.",
            AutoSize = false,
            Size = new Size(300, 36),
            Margin = new Padding(3, 0, 3, 6)
        };
        _name.Width = 294;
        _name.MaxLength = 60;
        _name.PlaceholderText = "e.g. Grenade Stat";
        _name.AccessibleName = "Custom column name";
        _name.BackColor = TrackerTheme.Surface;
        var nameFrame = new InputFrame(_name) { Margin = new Padding(3, 3, 3, 10) };

        _ok.Text = "Add column";
        _ok.DialogResult = DialogResult.OK;
        _ok.AutoSize = true;
        _ok.Margin = new Padding(6, 0, 0, 0);
        _ok.Enabled = false;
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = Padding.Empty };
        _name.TextChanged += (_, _) => _ok.Enabled = ColumnName.Length > 0;
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            Margin = new Padding(3, 0, 3, 0),
            WrapContents = false
        };
        buttons.Controls.Add(_ok);
        buttons.Controls.Add(cancel);

        var layout = new TableLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            Location = new Point(12, CaptionHeight + 4),
            Padding = new Padding(0, 0, 0, 12)
        };
        layout.Controls.Add(message);
        layout.Controls.Add(nameFrame);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        TrackerTheme.Apply(layout);
        TrackerTheme.Primary(_ok);
        TrackerTheme.Secondary(cancel);
        AcceptButton = _ok;
        CancelButton = cancel;
        var preferredSize = layout.GetPreferredSize(Size.Empty);
        ClientSize = new Size(preferredSize.Width + 24, layout.Top + preferredSize.Height);
        ActiveControl = _name;
    }

    /// <summary>The trimmed name typed by the user.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string ColumnName
    {
        get => _name.Text.Trim();
        set => _name.Text = value;
    }
}

using System.Drawing;

namespace D2AbilityTracker;

/// <summary>
/// Small dark modal for History: asks for a name (column or session) or, without a placeholder,
/// confirms an action. Opens off-screen and inactive under <see cref="HeadlessUi"/>.
/// </summary>
internal sealed class PromptDialog : AppWindow
{
    private readonly TextBox _name = new();
    private readonly Button _ok = new();

    /// <param name="placeholder">Input hint; null shows a confirmation without a text box.</param>
    public PromptDialog(string title, string message, string acceptText, string? placeholder = null,
        string initialValue = "", int maxLength = 60, bool destructive = false)
    {
        Text = title;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        AutoScaleDimensions = new SizeF(96, 96);
        AutoScaleMode = AutoScaleMode.Dpi;
        HasInput = placeholder is not null;

        var messageLabel = new Label
        {
            Text = message,
            AutoSize = true,
            MaximumSize = new Size(300, 0),
            Margin = new Padding(3, 4, 3, 8)
        };
        _name.Width = 294;
        _name.MaxLength = maxLength;
        _name.PlaceholderText = placeholder ?? "";
        _name.AccessibleName = title;
        _name.BackColor = TrackerTheme.Surface;
        _name.Text = initialValue;
        var nameFrame = new InputFrame(_name) { Margin = new Padding(3, 3, 3, 10), Visible = HasInput };

        _ok.Text = acceptText;
        _ok.DialogResult = DialogResult.OK;
        _ok.AutoSize = true;
        _ok.Margin = new Padding(6, 0, 0, 0);
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = Padding.Empty };
        void SyncAccept() => _ok.Enabled = !HasInput || Value.Length > 0;
        _name.TextChanged += (_, _) => SyncAccept();
        SyncAccept();
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
            Location = new Point(12, CaptionHeight),
            Padding = new Padding(0, 0, 0, 12),
            MinimumSize = new Size(306, 0)
        };
        layout.Controls.Add(messageLabel);
        if (HasInput) layout.Controls.Add(nameFrame);
        layout.Controls.Add(buttons);
        Controls.Add(layout);
        TrackerTheme.Apply(layout);
        TrackerTheme.Primary(_ok);
        if (destructive)
        {
            // Confirmations are always enabled, so the red treatment never needs to sync with Enabled.
            _ok.BackColor = TrackerTheme.Red;
            _ok.FlatAppearance.BorderColor = TrackerTheme.Red;
            _ok.FlatAppearance.MouseOverBackColor = Color.FromArgb(255, 165, 172);
            _ok.FlatAppearance.MouseDownBackColor = Color.FromArgb(230, 120, 130);
        }
        TrackerTheme.Secondary(cancel);
        AcceptButton = _ok;
        CancelButton = cancel;
        var preferredSize = layout.GetPreferredSize(Size.Empty);
        ClientSize = new Size(preferredSize.Width + 24, layout.Top + preferredSize.Height);
        ActiveControl = HasInput ? _name : cancel;
    }

    /// <summary>True when the dialog asks for a name rather than a confirmation.</summary>
    public bool HasInput { get; }

    /// <summary>The trimmed text typed by the user.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Value
    {
        get => _name.Text.Trim();
        set => _name.Text = value;
    }
}

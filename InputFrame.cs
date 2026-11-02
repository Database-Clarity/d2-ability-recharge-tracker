namespace D2AbilityTracker;

internal sealed class InputFrame : Panel
{
    private readonly Control _input;

    public InputFrame(Control input)
    {
        _input = input;
        Size = new Size(input.Width, TrackerTheme.ControlHeight);
        Margin = input.Margin;
        BackColor = TrackerTheme.Surface;
        DoubleBuffered = true;
        TabStop = false;
        if (input is TextBox text) text.BorderStyle = BorderStyle.None;
        if (input is NumericUpDown number) number.BorderStyle = BorderStyle.None;
        input.BackColor = TrackerTheme.Surface;
        input.ForeColor = TrackerTheme.Text;
        input.Dock = DockStyle.None;
        input.Margin = Padding.Empty;
        input.Enter += (_, _) => Invalidate();
        input.Leave += (_, _) => Invalidate();
        input.SizeChanged += (_, _) => PerformLayout();
        Controls.Add(input);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        if (_input is null) return;
        var inset = Math.Max(1, (int)Math.Round(8 * DeviceDpi / 96f));
        var multiline = _input is TextBox { Multiline: true };
        _input.SetBounds(inset, multiline ? inset : Math.Max(1, (Height - _input.Height) / 2),
            Math.Max(1, Width - inset * 2), multiline ? Math.Max(1, Height - inset * 2) : _input.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(ContainsFocus ? TrackerTheme.Accent : TrackerTheme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }
}

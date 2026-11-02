using System.Drawing.Drawing2D;

namespace D2AbilityTracker;

/// <summary>A compact on/off switch with a label and a muted shortcut hint. Clicks are routed by the owner.</summary>
internal sealed class ToggleSwitch : CheckBox
{
    private string _hint = string.Empty;
    private bool _hovered;

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        AutoCheck = false;
        AutoSize = false;
        Height = TrackerTheme.ControlHeight;
        Cursor = Cursors.Hand;
        CheckedChanged += (_, _) => Invalidate();
    }

    /// <summary>Secondary text drawn after the label, such as the global shortcut.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string Hint
    {
        get => _hint;
        set
        {
            if (_hint == value) return;
            _hint = value;
            UpdateWidth();
            Invalidate();
        }
    }

    private float DpiScale => DeviceDpi / 96f;
    private Size TrackSize => new((int)Math.Round(30 * DpiScale), (int)Math.Round(16 * DpiScale));
    private int Gap => (int)Math.Round(8 * DpiScale);

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        UpdateWidth();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateWidth();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        UpdateWidth();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        UpdateWidth();
    }

    private void UpdateWidth()
    {
        var width = 2 + TrackSize.Width + Gap + TextRenderer.MeasureText(Text, Font).Width;
        if (_hint.Length > 0) width += Gap / 2 + TextRenderer.MeasureText(_hint, Font).Width;
        Width = width + 4;
    }

    protected override void OnMouseEnter(EventArgs eventargs)
    {
        base.OnMouseEnter(eventargs);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs eventargs)
    {
        base.OnMouseLeave(eventargs);
        _hovered = false;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs pevent)
    {
        var graphics = pevent.Graphics;
        graphics.Clear(Parent?.BackColor ?? TrackerTheme.Background);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var track = TrackSize;
        var bounds = new Rectangle(2, (Height - track.Height) / 2, track.Width, track.Height);
        var fill = Checked ? (_hovered ? TrackerTheme.AccentHover : TrackerTheme.Accent)
            : _hovered ? TrackerTheme.Hover : TrackerTheme.Surface;
        using (var path = new GraphicsPath())
        {
            var diameter = bounds.Height;
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 90, 180);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 180);
            path.CloseFigure();
            using var brush = new SolidBrush(Enabled ? fill : TrackerTheme.Surface);
            graphics.FillPath(brush, path);
            using var outline = new Pen(Checked && Enabled ? fill : TrackerTheme.Border);
            graphics.DrawPath(outline, path);
        }

        var inset = Math.Max(3, (int)Math.Round(3 * DpiScale));
        var knob = bounds.Height - inset * 2;
        var knobX = Checked ? bounds.Right - inset - knob : bounds.Left + inset;
        using (var knobBrush = new SolidBrush(Checked ? TrackerTheme.Background : TrackerTheme.Muted))
        {
            graphics.FillEllipse(knobBrush, knobX, bounds.Top + inset, knob, knob);
        }

        var textLeft = bounds.Right + Gap;
        var textWidth = TextRenderer.MeasureText(Text, Font).Width;
        const TextFormatFlags flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        TextRenderer.DrawText(graphics, Text, Font, new Rectangle(textLeft, 0, textWidth + 2, Height),
            Enabled ? TrackerTheme.Text : TrackerTheme.Muted, flags);
        if (_hint.Length > 0)
        {
            var hintLeft = textLeft + textWidth + Gap / 2;
            TextRenderer.DrawText(graphics, _hint, Font,
                new Rectangle(hintLeft, 0, Math.Max(0, Width - hintLeft), Height), TrackerTheme.Muted, flags);
        }

        if (Focused && ShowFocusCues)
        {
            ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -1, -3));
        }
    }
}

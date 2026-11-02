namespace D2AbilityTracker;

/// <summary>A compact single-choice selector rendered as adjacent segments (radio-button semantics).</summary>
internal sealed class SegmentedControl : Panel
{
    private readonly List<Segment> _segments = [];
    private bool _updating;

    public SegmentedControl(params string[] options)
    {
        DoubleBuffered = true;
        Height = TrackerTheme.ControlHeight;
        Padding = new Padding(1);
        BackColor = TrackerTheme.Surface;
        foreach (var option in options)
        {
            var segment = new Segment(this, _segments.Count) { Text = option, AccessibleName = option };
            segment.CheckedChanged += (_, _) =>
            {
                if (!_updating && segment.Checked) SelectionChanged?.Invoke(this, EventArgs.Empty);
            };
            _segments.Add(segment);
            Controls.Add(segment);
        }
    }

    public event EventHandler? SelectionChanged;

    internal IReadOnlyList<RadioButton> Segments => _segments;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int SelectedIndex
    {
        get => _segments.FindIndex(segment => segment.Checked);
        set
        {
            if (SelectedIndex == value) return;
            _updating = true;
            try
            {
                for (var index = 0; index < _segments.Count; index++)
                {
                    _segments[index].Checked = index == value;
                }
            }
            finally
            {
                _updating = false;
            }
        }
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        PerformLayout();
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_segments.Count == 0) return;
        var inset = (int)Math.Round(13 * DeviceDpi / 96f);
        using var measureFont = new Font(Font, FontStyle.Bold);
        var x = Padding.Left;
        foreach (var segment in _segments)
        {
            var width = TextRenderer.MeasureText(segment.Text, measureFont).Width + inset * 2;
            segment.SetBounds(x, Padding.Top, width, Math.Max(1, Height - Padding.Vertical));
            x += width;
        }

        var desiredWidth = x + Padding.Right;
        if (Width != desiredWidth) Width = desiredWidth;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(TrackerTheme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
    }

    private sealed class Segment : RadioButton
    {
        private readonly SegmentedControl _owner;
        private readonly int _index;
        private bool _hovered;

        public Segment(SegmentedControl owner, int index)
        {
            _owner = owner;
            _index = index;
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            Margin = Padding.Empty;
            Cursor = Cursors.Hand;
            CheckedChanged += (_, _) =>
            {
                Invalidate();
                // Neighbors draw the divider next to the selected segment.
                if (_index + 1 < _owner._segments.Count) _owner._segments[_index + 1].Invalidate();
            };
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
            graphics.Clear(Checked ? TrackerTheme.Selected : _hovered ? TrackerTheme.Hover : TrackerTheme.Surface);
            if (_index > 0 && !Checked && !_owner._segments[_index - 1].Checked)
            {
                using var divider = new Pen(TrackerTheme.Border);
                var inset = Height / 4;
                graphics.DrawLine(divider, 0, inset, 0, Height - inset);
            }

            if (Checked)
            {
                using var underline = new SolidBrush(TrackerTheme.Accent);
                var thickness = Math.Max(2, (int)Math.Round(2 * DeviceDpi / 96f));
                graphics.FillRectangle(underline, 0, Height - thickness, Width, thickness);
            }

            using var font = new Font(Font, Checked ? FontStyle.Bold : FontStyle.Regular);
            TextRenderer.DrawText(graphics, Text, font, ClientRectangle,
                !Enabled ? TrackerTheme.Border : Checked ? TrackerTheme.Text : TrackerTheme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
                TextFormatFlags.NoPrefix);
            if (Focused && ShowFocusCues)
            {
                ControlPaint.DrawFocusRectangle(graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
            }
        }
    }
}

using System.ComponentModel;
using System.Drawing.Drawing2D;

namespace D2AbilityTracker;

internal enum StatusTone
{
    Neutral,
    Good,
    Warning,
    Error
}

internal sealed record StatusMessage(string Text, StatusTone Tone);

/// <summary>Single-line state banner shown above the ability results.</summary>
internal sealed class StatusBanner : Label
{
    private StatusTone _tone;

    public StatusBanner()
    {
        AutoSize = false;
        AutoEllipsis = true;
        TextAlign = ContentAlignment.MiddleLeft;
        Padding = new Padding(14, 0, 8, 0);
        DoubleBuffered = true;
    }

    [DefaultValue(StatusTone.Neutral)]
    public StatusTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value) return;
            _tone = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var tone = TrackerTheme.ToneColor(_tone);
        e.Graphics.Clear(TrackerTheme.Surface);
        using var bar = new SolidBrush(tone);
        e.Graphics.FillRectangle(bar, 0, 0, Math.Max(3, (int)Math.Round(3 * DeviceDpi / 96f)), Height);
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(Padding.Left, 0, Width - Padding.Horizontal, Height),
            _tone == StatusTone.Neutral ? TrackerTheme.Text : tone,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>Compact rounded capture-health indicator for the navigation row.</summary>
internal sealed class StatusPill : Label
{
    private StatusTone _tone;

    public StatusPill()
    {
        AutoSize = true;
        AutoEllipsis = true;
        Padding = new Padding(20, 4, 9, 4);
        DoubleBuffered = true;
    }

    [DefaultValue(StatusTone.Neutral)]
    public StatusTone Tone
    {
        get => _tone;
        set
        {
            if (_tone == value) return;
            _tone = value;
            Invalidate();
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var tone = TrackerTheme.ToneColor(_tone);
        e.Graphics.Clear(Parent?.BackColor ?? TrackerTheme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = bounds.Height / 2;
        using (var path = new GraphicsPath())
        {
            path.AddArc(bounds.Left, bounds.Top, radius * 2, radius * 2, 90, 180);
            path.AddArc(bounds.Right - radius * 2, bounds.Top, radius * 2, radius * 2, 270, 180);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(28, tone));
            e.Graphics.FillPath(fill, path);
            using var outline = new Pen(Color.FromArgb(110, tone));
            e.Graphics.DrawPath(outline, path);
        }

        var dot = Math.Max(6, (int)Math.Round(7 * DeviceDpi / 96f));
        using (var dotBrush = new SolidBrush(tone))
        {
            e.Graphics.FillEllipse(dotBrush, Padding.Left - dot - Math.Max(4, dot * 2 / 3), (Height - dot) / 2f, dot, dot);
        }

        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(Padding.Left, 0, Width - Padding.Horizontal, Height),
            _tone == StatusTone.Neutral ? TrackerTheme.Muted : tone,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

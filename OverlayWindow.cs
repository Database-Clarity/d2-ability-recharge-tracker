using System.Drawing;

namespace D2AbilityTracker;

internal sealed class OverlayWindow : Form
{
    private string _title = "ABILITY TRACKER";
    private string _detail = "Select ability slots";
    private Color _accent = Color.FromArgb(245, 172, 65);

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(18, 20, 27);
        Opacity = 0.9;
        Size = new Size(450, 112);
        DoubleBuffered = true;
        UpdatePosition("Bottom right");
    }

    public void SetStatus(string title, string detail, Color accent, Rectangle? monitorBounds = null)
    {
        var changed = _title != title || _detail != detail || _accent != accent;
        _title = title;
        _detail = detail;
        _accent = accent;
        var lineCount = Math.Max(1, detail.Count(character => character == '\n') + 1);
        Height = 48 + lineCount * 20;
        if (monitorBounds is { } bounds)
        {
            UpdatePosition(_anchor, bounds);
        }

        if (changed)
        {
            Invalidate();
        }
    }

    private string _anchor = "Bottom right";

    public void UpdatePosition(string anchor)
    {
        _anchor = anchor;
        var area = Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        PositionInArea(anchor, area);
    }

    public void UpdatePosition(string anchor, Rectangle monitorBounds)
    {
        _anchor = anchor;
        PositionInArea(anchor, Screen.AllScreens.FirstOrDefault(screen => screen.Bounds == monitorBounds)?.WorkingArea ?? monitorBounds);
    }

    private void PositionInArea(string anchor, Rectangle area)
    {
        var margin = 24;
        Location = anchor switch
        {
            "Top left" => new Point(area.Left + margin, area.Top + margin),
            "Top right" => new Point(area.Right - Width - margin, area.Top + margin),
            "Bottom left" => new Point(area.Left + margin, area.Bottom - Height - margin),
            _ => new Point(area.Right - Width - margin, area.Bottom - Height - margin)
        };
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= 0x00000020 | 0x00000080;
            return parameters;
        }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var background = new SolidBrush(BackColor);
        using var accent = new SolidBrush(_accent);
        using var titleFont = new Font("Segoe UI", 9, FontStyle.Bold);
        using var detailFont = new Font("Segoe UI", 10, FontStyle.Regular);
        using var textBrush = new SolidBrush(Color.White);
        e.Graphics.FillRoundedRectangle(background, ClientRectangle, 12);
        e.Graphics.FillRectangle(accent, 0, 0, 4, Height);
        e.Graphics.DrawString(_title, titleFont, accent, 15, 9);
        e.Graphics.DrawString(_detail, detailFont, textBrush, new RectangleF(15, 30, Width - 28, Height - 36));
    }

}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = new System.Drawing.Drawing2D.GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        graphics.FillPath(brush, path);
    }
}

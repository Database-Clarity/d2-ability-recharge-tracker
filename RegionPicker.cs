using System.Drawing;
using System.Runtime.InteropServices;

namespace D2AbilityTracker;

internal sealed class RegionPicker : Form
{
    private readonly Bitmap _snapshot;
    private readonly Rectangle _monitorBounds;
    private readonly RectangleF _imageBounds;
    private Point _start;
    private Point _current;
    private bool _dragging;

    public Rectangle? SelectedRegion { get; private set; }

    public RegionPicker(Bitmap snapshot, Rectangle monitorBounds)
    {
        _snapshot = (Bitmap)snapshot.Clone();
        _monitorBounds = monitorBounds;
        var imageScale = Math.Min((monitorBounds.Width - 48f) / snapshot.Width, (monitorBounds.Height - 112f) / snapshot.Height);
        imageScale = Math.Max(0.1f, imageScale);
        var imageWidth = snapshot.Width * imageScale;
        var imageHeight = snapshot.Height * imageScale;
        _imageBounds = new RectangleF(
            (monitorBounds.Width - imageWidth) / 2f,
            Math.Max(68f, (monitorBounds.Height - imageHeight) / 2f + 32f),
            imageWidth,
            imageHeight);

        Bounds = monitorBounds;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        BackColor = Color.FromArgb(14, 17, 24);
        DoubleBuffered = true;
        KeyPreview = true;
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                DialogResult = DialogResult.Cancel;
                Close();
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpShowWindow);
        SetForegroundWindow(Handle);
        Activate();
        SetFocus(Handle);
        ClipCursor(IntPtr.Zero);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.DrawImage(_snapshot, _imageBounds);

        using var headerBackground = new SolidBrush(Color.FromArgb(230, 14, 17, 24));
        using var textFont = new Font("Segoe UI", 13, FontStyle.Bold);
        using var hintFont = new Font("Segoe UI", 9, FontStyle.Regular);
        using var textBrush = new SolidBrush(Color.White);
        using var hintBrush = new SolidBrush(Color.FromArgb(205, 215, 225));
        using var outline = new Pen(Color.FromArgb(255, 246, 187, 67), 3);
        e.Graphics.FillRectangle(headerBackground, 0, 0, Width, 60);
        e.Graphics.DrawString("Select the square slot tightly, including its border and cooldown-line area", textFont, textBrush, 18, 8);
        e.Graphics.DrawString("The preview is frozen. Esc cancels.", hintFont, hintBrush, 20, 34);

        if (_dragging)
        {
            e.Graphics.DrawRectangle(outline, Rectangle.FromLTRB(
                Math.Min(_start.X, _current.X),
                Math.Min(_start.Y, _current.Y),
                Math.Max(_start.X, _current.X),
                Math.Max(_start.Y, _current.Y)));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _snapshot.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left || !_imageBounds.Contains(e.Location))
        {
            return;
        }

        _start = e.Location;
        _current = e.Location;
        _dragging = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging)
        {
            _current = ClampToImage(e.Location);
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging)
        {
            return;
        }

        _current = ClampToImage(e.Location);
        _dragging = false;
        var left = Math.Min(_start.X, _current.X);
        var top = Math.Min(_start.Y, _current.Y);
        var right = Math.Max(_start.X, _current.X);
        var bottom = Math.Max(_start.Y, _current.Y);
        var sourceLeft = (int)Math.Floor((left - _imageBounds.Left) * _snapshot.Width / _imageBounds.Width);
        var sourceTop = (int)Math.Floor((top - _imageBounds.Top) * _snapshot.Height / _imageBounds.Height);
        var sourceRight = (int)Math.Ceiling((right - _imageBounds.Left) * _snapshot.Width / _imageBounds.Width);
        var sourceBottom = (int)Math.Ceiling((bottom - _imageBounds.Top) * _snapshot.Height / _imageBounds.Height);
        var selected = Rectangle.FromLTRB(sourceLeft, sourceTop, sourceRight, sourceBottom);
        if (selected.Width < 18 || selected.Height < 18)
        {
            Invalidate();
            return;
        }

        SelectedRegion = new Rectangle(
            _monitorBounds.Left + selected.Left,
            _monitorBounds.Top + selected.Top,
            selected.Width,
            selected.Height);
        DialogResult = DialogResult.OK;
        Close();
    }

    private Point ClampToImage(Point point) => new(
        Math.Clamp(point.X, (int)_imageBounds.Left, (int)_imageBounds.Right),
        Math.Clamp(point.Y, (int)_imageBounds.Top, (int)_imageBounds.Bottom));

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rect);
}

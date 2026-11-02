namespace D2AbilityTracker;

internal sealed class ScrollBarSkin : NativeWindow, IDisposable
{
    private readonly ScrollBar _bar;

    internal ScrollBarSkin(ScrollBar bar)
    {
        _bar = bar;
        bar.HandleCreated += HandleCreated;
        bar.HandleDestroyed += HandleDestroyed;
        bar.ValueChanged += ValueChanged;
        bar.Disposed += Disposed;
        if (bar.IsHandleCreated) AssignHandle(bar.Handle);
    }

    private void HandleCreated(object? sender, EventArgs e) => AssignHandle(_bar.Handle);
    private void HandleDestroyed(object? sender, EventArgs e) => ReleaseHandle();
    private void ValueChanged(object? sender, EventArgs e) => _bar.Invalidate();
    private void Disposed(object? sender, EventArgs e) => Dispose();

    protected override void WndProc(ref Message m)
    {
        if (m.Msg is 0x0317 or 0x0318 && m.WParam != IntPtr.Zero)
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            Draw(graphics);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != 0x000F) return;
        using var fieldGraphics = Graphics.FromHwnd(Handle);
        Draw(fieldGraphics);
    }

    private void Draw(Graphics graphics)
    {
        using var background = new SolidBrush(TrackerTheme.Background);
        graphics.FillRectangle(background, _bar.ClientRectangle);
        var vertical = _bar is VScrollBar;
        var length = vertical ? _bar.Height : _bar.Width;
        var breadth = vertical ? _bar.Width : _bar.Height;
        var button = Math.Min(breadth, length / 2);
        var track = Math.Max(0, length - 2 * button);
        var range = Math.Max(1L, (long)_bar.Maximum - _bar.Minimum + 1);
        var thumb = Math.Min(track, Math.Max(button / 2, (int)(track * (double)_bar.LargeChange / range)));
        var travel = Math.Max(1L, range - _bar.LargeChange);
        var offset = button + (int)((track - thumb) * Math.Clamp((_bar.Value - (double)_bar.Minimum) / travel, 0, 1));
        using var fill = new SolidBrush(TrackerTheme.Border);
        if (track > 0)
            graphics.FillRectangle(fill, vertical
                ? new Rectangle(4, offset, Math.Max(1, breadth - 8), thumb)
                : new Rectangle(offset, 4, thumb, Math.Max(1, breadth - 8)));
        using var pen = new Pen(TrackerTheme.Muted, Math.Max(1, _bar.DeviceDpi / 96f));
        var arm = Math.Max(2, breadth / 5);
        var center = breadth / 2;
        foreach (var end in new[] { false, true })
        {
            var along = end ? length - button / 2 : button / 2;
            var sign = end ? 1 : -1;
            Point[] points = vertical
                ? [new Point(center - arm, along - sign * arm / 2), new Point(center, along + sign * arm / 2),
                    new Point(center + arm, along - sign * arm / 2)]
                : [new Point(along - sign * arm / 2, center - arm), new Point(along + sign * arm / 2, center),
                    new Point(along - sign * arm / 2, center + arm)];
            graphics.DrawLines(pen, points);
        }
    }

    public void Dispose()
    {
        _bar.HandleCreated -= HandleCreated;
        _bar.HandleDestroyed -= HandleDestroyed;
        _bar.ValueChanged -= ValueChanged;
        _bar.Disposed -= Disposed;
        ReleaseHandle();
    }
}

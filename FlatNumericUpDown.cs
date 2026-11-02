namespace D2AbilityTracker;

internal sealed class FlatNumericUpDown : NumericUpDown
{
    private SpinnerWindow? _spinner;

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        _spinner?.Dispose();
        var buttons = Controls.Cast<Control>().First(control => control is not TextBox);
        _spinner = new SpinnerWindow(buttons);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _spinner?.Dispose();
        base.Dispose(disposing);
    }

    private sealed class SpinnerWindow : NativeWindow, IDisposable
    {
        private readonly Control _control;

        public SpinnerWindow(Control control)
        {
            _control = control;
            control.HandleCreated += HandleCreated;
            control.HandleDestroyed += HandleDestroyed;
            if (control.IsHandleCreated) AssignHandle(control.Handle);
        }

        private void HandleCreated(object? sender, EventArgs e) => AssignHandle(_control.Handle);
        private void HandleDestroyed(object? sender, EventArgs e) => ReleaseHandle();

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
            graphics.Clear(TrackerTheme.Surface);
            var width = _control.ClientSize.Width;
            var height = _control.ClientSize.Height;
            var center = width / 2;
            var arm = Math.Max(2, width / 5);
            using var arrow = new SolidBrush(_control.Enabled ? TrackerTheme.Text : TrackerTheme.Muted);
            graphics.FillPolygon(arrow, [new Point(center, height / 4 - 2),
                new Point(center - arm, height / 4 + 2), new Point(center + arm, height / 4 + 2)]);
            graphics.FillPolygon(arrow, [new Point(center, height * 3 / 4 + 2),
                new Point(center - arm, height * 3 / 4 - 2), new Point(center + arm, height * 3 / 4 - 2)]);
        }

        public void Dispose()
        {
            _control.HandleCreated -= HandleCreated;
            _control.HandleDestroyed -= HandleDestroyed;
            ReleaseHandle();
        }
    }
}

namespace D2AbilityTracker;

internal class AppWindow : Form
{
    private readonly CaptionButton _close;
    private readonly CaptionButton _minimize;
    private readonly CaptionButton _maximize;

    protected AppWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        BackColor = TrackerTheme.Background;
        SetStyle(ControlStyles.ResizeRedraw | ControlStyles.OptimizedDoubleBuffer, true);
        _close = new CaptionButton("Close", CaptionAction.Close);
        _minimize = new CaptionButton("Minimize", CaptionAction.Minimize);
        _maximize = new CaptionButton("Maximize or restore", CaptionAction.Maximize);
        _close.Click += (_, _) => Close();
        _minimize.Click += (_, _) => WindowState = FormWindowState.Minimized;
        _maximize.Click += (_, _) => ToggleMaximize();
        Controls.AddRange([_close, _minimize, _maximize]);
        UpdateChrome();
    }

    protected virtual bool ResizeWindow => false;
    protected int CaptionHeight => (int)Math.Round(36 * DeviceDpi / 96f);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SelectNextControl(_maximize, true, true, true, true);
    }

    protected override void OnLayout(LayoutEventArgs levent)
    {
        base.OnLayout(levent);
        if (_close is not null) UpdateChrome();
    }

    private void UpdateChrome()
    {
        var height = CaptionHeight;
        var border = Math.Max(1, DeviceDpi / 96);
        var desired = new Padding(border, height, border, border);
        if (Padding != desired) Padding = desired;
        var width = (int)Math.Round(44 * DeviceDpi / 96f);
        _close.SetBounds(ClientSize.Width - width - border, border, width, height - border);
        _maximize.Visible = MaximizeBox;
        _maximize.SetBounds(_close.Left - width, border, width, height - border);
        _minimize.Visible = MinimizeBox;
        _minimize.SetBounds((MaximizeBox ? _maximize.Left : _close.Left) - width, border, width, height - border);
        _close.BringToFront();
        _maximize.BringToFront();
        _minimize.BringToFront();
        Invalidate();
    }

    protected override void OnTextChanged(EventArgs e)
    {
        base.OnTextChanged(e);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var border = new Pen(TrackerTheme.Border);
        e.Graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        var right = MinimizeBox ? _minimize.Left : MaximizeBox ? _maximize.Left : _close.Left;
        TextRenderer.DrawText(e.Graphics, Text, Font,
            new Rectangle(16, 1, Math.Max(0, right - 24), CaptionHeight - 1), TrackerTheme.Muted,
            TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
    }

    private void ToggleMaximize()
    {
        if (!MaximizeBox) return;
        MaximizedBounds = Screen.FromControl(this).WorkingArea;
        WindowState = WindowState == FormWindowState.Maximized ? FormWindowState.Normal : FormWindowState.Maximized;
    }

    protected override void WndProc(ref Message m)
    {
        const int hitTest = 0x0084;
        const int captionDoubleClick = 0x00A3;
        if (m.Msg == captionDoubleClick)
        {
            ToggleMaximize();
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != hitTest) return;
        var packed = m.LParam.ToInt64();
        var point = PointToClient(new Point((short)(packed & 0xffff), (short)((packed >> 16) & 0xffff)));
        var edge = Math.Max(5, (int)Math.Round(5 * DeviceDpi / 96f));
        if (ResizeWindow && WindowState == FormWindowState.Normal)
        {
            var left = point.X < edge;
            var right = point.X >= ClientSize.Width - edge;
            var top = point.Y < edge;
            var bottom = point.Y >= ClientSize.Height - edge;
            var hit = top ? left ? 13 : right ? 14 : 12
                : bottom ? left ? 16 : right ? 17 : 15
                : left ? 10 : right ? 11 : 0;
            if (hit != 0)
            {
                m.Result = (IntPtr)hit;
                return;
            }
        }
        if (point.Y < CaptionHeight && GetChildAtPoint(point) is null)
            m.Result = (IntPtr)2;
    }

    private enum CaptionAction { Close, Minimize, Maximize }

    private sealed class CaptionButton : Button
    {
        private readonly CaptionAction _action;
        private bool _hover;

        public CaptionButton(string name, CaptionAction action)
        {
            _action = action;
            AccessibleName = name;
            AccessibleRole = AccessibleRole.PushButton;
            TabStop = true;
            FlatStyle = FlatStyle.Flat;
            FlatAppearance.BorderSize = 0;
            BackColor = TrackerTheme.Background;
        }

        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hover = true; Invalidate(); }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hover = false; Invalidate(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            e.Graphics.Clear(_hover ? _action == CaptionAction.Close ? Color.FromArgb(180, 45, 55) : TrackerTheme.Surface : BackColor);
            var size = Math.Max(8, (int)Math.Round(10 * DeviceDpi / 96f));
            var x = (Width - size) / 2;
            var y = (Height - size) / 2;
            using var pen = new Pen(TrackerTheme.Text, DeviceDpi / 96f);
            if (_action == CaptionAction.Close)
            {
                e.Graphics.DrawLine(pen, x, y, x + size, y + size);
                e.Graphics.DrawLine(pen, x + size, y, x, y + size);
            }
            else if (_action == CaptionAction.Minimize)
                e.Graphics.DrawLine(pen, x, y + size / 2, x + size, y + size / 2);
            else
                e.Graphics.DrawRectangle(pen, x, y, size, size);
            if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -5, -5));
        }
    }
}

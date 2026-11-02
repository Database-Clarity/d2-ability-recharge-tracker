using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace D2AbilityTracker;

/// <summary>
/// Always-on-top, click-through status overlay. A slim header carries the state (tracking, paused, move mode or
/// repeat-test progress). Every ability row has a fixed height (one or two lines),
/// and the window width is fixed, so the overlay never resizes or shifts while tracking.
/// In move mode the overlay becomes interactive and can be dragged to a custom location.
/// </summary>
internal sealed class OverlayWindow : Form
{
    private const int WsExTransparent = 0x00000020;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int GwlExStyle = -20;
    private const int WmSetCursor = 0x0020;
    private const int WmNcHitTest = 0x0084;
    private const int WmNcLButtonDblClk = 0x00A3;
    private const int WmNcRButtonDown = 0x00A4;
    private const int WmNcRButtonUp = 0x00A5;
    private const int WmExitSizeMove = 0x0232;
    private const int HtClient = 1;
    private const int HtCaption = 2;

    private const int LogicalWidth = 340;
    private const int HeaderHeight = 22;
    private const int Line1Height = 20;
    private const int Line2Height = 17;
    private const int RowGap = 6;
    private const int BottomPadding = 10;
    private const int PaddingX = 16;
    private const int DotSize = 6;
    private const int DotGap = 8;
    private const int ColumnGap = 12;
    private const int CornerRadius = 8;
    private const double NormalOpacity = 0.92;
    private const double MoveOpacity = 0.98;

    private static readonly Color Background = Color.FromArgb(20, 22, 28);
    private static readonly Color BorderColor = Color.FromArgb(46, 50, 61);
    private static readonly Color LabelColor = Color.FromArgb(150, 157, 172);
    private static readonly Color PrimaryTextColor = Color.FromArgb(236, 238, 243);
    private static readonly Color SecondaryTextColor = Color.FromArgb(132, 139, 154);
    private static readonly Color NeutralDotColor = Color.FromArgb(92, 98, 112);

    private const TextFormatFlags TextFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
        TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private string _title = "ABILITY TRACKER";
    private IReadOnlyList<OverlayRow> _rows = [new OverlayRow(string.Empty, "Select ability slots")];
    private bool _twoLines = true;
    private Color _accent = Color.FromArgb(245, 172, 65);
    private string _anchor = "Bottom right";
    private Rectangle? _monitorBounds;
    private Point? _customLocation;
    private bool _moveMode;
    private Rectangle _doneBounds;
    private readonly List<CopyResultButton> _copyButtons = [];

    private Font _titleFont = null!;
    private Font _labelFont = null!;
    private Font _line1Font = null!;
    private Font _line2Font = null!;
    private Font _buttonFont = null!;

    public OverlayWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ControlBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        Opacity = HeadlessUi.Enabled ? 0 : NormalOpacity;
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        CreateFonts();
        UpdateSize();
        Reposition();
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Raised when the user leaves move mode from the overlay (Done, double-click, Esc or Enter).</summary>
    public event Action<Point>? MoveFinished;

    /// <summary>Raised after the user finishes a drag; carries the new top-left in screen pixels.</summary>
    public event Action<Point>? LocationCommitted;

    public bool MoveMode => _moveMode;

    /// <summary>Custom top-left in screen pixels, or null to use the anchor.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Point? CustomLocation
    {
        get => _customLocation;
        set
        {
            _customLocation = value;
            Reposition();
        }
    }

    protected override bool ShowWithoutActivation => HeadlessUi.Enabled || !_moveMode;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= WsExToolWindow;
            if (!_moveMode)
            {
                parameters.ExStyle |= WsExTransparent | WsExNoActivate;
            }

            return HeadlessUi.Apply(parameters);
        }
    }

    /// <summary>Replaces the overlay content. Height depends only on the row count and <paramref name="twoLines"/>.</summary>
    public void SetContent(string title, IReadOnlyList<OverlayRow> rows, bool twoLines, Color accent, Rectangle? monitorBounds = null, string? anchor = null)
    {
        var changed = _title != title || _twoLines != twoLines || _accent != accent || !_rows.SequenceEqual(rows);
        _title = title;
        _rows = rows.Count > 0 ? rows.ToArray() : [new OverlayRow(string.Empty, "No abilities tracked")];
        _twoLines = twoLines;
        _accent = accent;
        _monitorBounds = monitorBounds ?? _monitorBounds;
        _anchor = anchor ?? _anchor;
        UpdateSize();
        Reposition();
        if (changed)
        {
            Invalidate();
        }
    }

    public void UpdatePosition(string anchor)
    {
        _anchor = anchor;
        Reposition();
    }

    public void UpdatePosition(string anchor, Rectangle monitorBounds)
    {
        _anchor = anchor;
        _monitorBounds = monitorBounds;
        Reposition();
    }

    /// <summary>Turns drag-to-move on or off. Off restores the click-through, non-activating overlay.</summary>
    public void SetMoveMode(bool enabled)
    {
        if (_moveMode == enabled)
        {
            return;
        }

        _moveMode = enabled;
        if (IsHandleCreated)
        {
            var exStyle = GetWindowLongPtr(Handle, GwlExStyle).ToInt64();
            exStyle = enabled
                ? exStyle & ~(long)(WsExTransparent | WsExNoActivate)
                : exStyle | WsExTransparent | WsExNoActivate;
            SetWindowLongPtr(Handle, GwlExStyle, new IntPtr(exStyle));
        }

        Opacity = HeadlessUi.Enabled ? 0 : enabled ? MoveOpacity : NormalOpacity;
        UpdateCopyButtons();
        if (!enabled)
        {
            if (ClampToScreens(Location) is { } clamped)
            {
                Location = clamped;
            }

            Reposition();
        }
        else if (Visible && !HeadlessUi.Enabled)
        {
            Activate();
        }

        Invalidate();
    }

    /// <summary>Returns the location moved fully onto the screen it overlaps most, or null when it is on no screen.</summary>
    public Point? ClampToScreens(Point location)
    {
        var bounds = new Rectangle(location, Size);
        var best = Screen.AllScreens
            .Select(screen => (Area: screen.WorkingArea, Overlap: Rectangle.Intersect(screen.WorkingArea, bounds)))
            .Where(item => item.Overlap.Width > 0 && item.Overlap.Height > 0)
            .OrderByDescending(item => (long)item.Overlap.Width * item.Overlap.Height)
            .Select(item => (Rectangle?)item.Area)
            .FirstOrDefault();
        if (best is not { } area)
        {
            return null;
        }

        var x = Math.Clamp(location.X, area.Left, Math.Max(area.Left, area.Right - Width));
        var y = Math.Clamp(location.Y, area.Top, Math.Max(area.Top, area.Bottom - Height));
        return new Point(x, y);
    }

    private void Reposition()
    {
        if (_moveMode)
        {
            return;
        }

        if (_customLocation is { } custom && ClampToScreens(custom) is { } clamped)
        {
            Location = clamped;
            return;
        }

        var area = _monitorBounds is { } monitor
            ? Screen.AllScreens.FirstOrDefault(screen => screen.Bounds == monitor)?.WorkingArea
            : null;
        area ??= Screen.PrimaryScreen?.WorkingArea ?? Screen.FromPoint(Cursor.Position).WorkingArea;
        var margin = Scale(24);
        var target = area.Value;
        Location = _anchor switch
        {
            "Top left" => new Point(target.Left + margin, target.Top + margin),
            "Top right" => new Point(target.Right - Width - margin, target.Top + margin),
            "Bottom left" => new Point(target.Left + margin, target.Bottom - Height - margin),
            _ => new Point(target.Right - Width - margin, target.Bottom - Height - margin)
        };
    }

    private void UpdateSize()
    {
        var rowHeight = Line1Height + (_twoLines ? Line2Height : 0);
        var logicalHeight = HeaderHeight + _rows.Count * rowHeight + (_rows.Count - 1) * RowGap + BottomPadding;
        var size = new Size(Scale(LogicalWidth), Scale(logicalHeight));
        if (Size != size)
        {
            Size = size;
            UpdateRegion();
        }
        UpdateCopyButtons();
    }

    private void UpdateCopyButtons()
    {
        while (_copyButtons.Count > _rows.Count)
        {
            _copyButtons[^1].Dispose();
            _copyButtons.RemoveAt(_copyButtons.Count - 1);
        }
        while (_copyButtons.Count < _rows.Count)
        {
            var index = _copyButtons.Count;
            var button = new CopyResultButton("Copy overlay result", () => $"{_rows[index].Label}: {_rows[index].Line1}\n{_rows[index].Line2}")
            {
                BackColor = Background,
                ForeColor = PrimaryTextColor
            };
            _copyButtons.Add(button);
            Controls.Add(button);
        }
        for (var index = 0; index < _copyButtons.Count; index++)
        {
            var button = _copyButtons[index];
            button.Visible = _moveMode;
            button.AccessibleName = $"Copy {_rows[index].Label} overlay result";
            button.SetBounds(Width - Scale(PaddingX + 24),
                Scale(HeaderHeight + index * (Line1Height + (_twoLines ? Line2Height : 0) + RowGap)), Scale(24), Scale(Line1Height));
        }
    }

    private int Scale(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    private void CreateFonts()
    {
        float Pixels(float logical) => (float)(logical * DeviceDpi / 96.0);
        _titleFont = new Font("Segoe UI", Pixels(11), FontStyle.Bold, GraphicsUnit.Pixel);
        _labelFont = new Font("Segoe UI Semibold", Pixels(13), FontStyle.Regular, GraphicsUnit.Pixel);
        _line1Font = new Font("Segoe UI", Pixels(13.5f), FontStyle.Regular, GraphicsUnit.Pixel);
        _line2Font = new Font("Segoe UI", Pixels(12), FontStyle.Regular, GraphicsUnit.Pixel);
        _buttonFont = new Font("Segoe UI Semibold", Pixels(11), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private void DisposeFonts()
    {
        _titleFont?.Dispose();
        _labelFont?.Dispose();
        _line1Font?.Dispose();
        _line2Font?.Dispose();
        _buttonFont?.Dispose();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        DisposeFonts();
        CreateFonts();
        UpdateSize();
        Reposition();
        Invalidate();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        var preference = 2; // DWMWCP_ROUND
        var roundedByDwm = DwmSetWindowAttribute(Handle, 33, ref preference, sizeof(int)) == 0;
        if (!roundedByDwm)
        {
            _useRegion = true;
            UpdateRegion();
        }
    }

    private bool _useRegion;

    private void UpdateRegion()
    {
        if (!_useRegion)
        {
            return;
        }

        using var path = RoundedRectangle(new Rectangle(Point.Empty, Size), Scale(CornerRadius));
        var old = Region;
        Region = new Region(path);
        old?.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (IsHandleCreated && !IsDisposed)
        {
            BeginInvoke(Reposition);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (_moveMode)
        {
            switch (m.Msg)
            {
                case WmNcHitTest:
                    var point = PointToClient(new Point(unchecked((short)(long)m.LParam), unchecked((short)((long)m.LParam >> 16))));
                    m.Result = _doneBounds.Contains(point) || GetChildAtPoint(point) is CopyResultButton ? HtClient : HtCaption;
                    return;
                case WmSetCursor when ((int)(long)m.LParam & 0xFFFF) == HtCaption:
                    Cursor.Current = Cursors.SizeAll;
                    m.Result = 1;
                    return;
                case WmNcLButtonDblClk:
                    FinishMove();
                    return;
                case WmNcRButtonDown:
                case WmNcRButtonUp:
                    return;
            }
        }

        base.WndProc(ref m);
        if (m.Msg == WmExitSizeMove && _moveMode)
        {
            if (ClampToScreens(Location) is { } clamped)
            {
                Location = clamped;
            }

            _customLocation = Location;
            LocationCommitted?.Invoke(Location);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        Cursor = _moveMode && _doneBounds.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
    }

    protected override void OnMouseClick(MouseEventArgs e)
    {
        base.OnMouseClick(e);
        if (_moveMode && e.Button == MouseButtons.Left && _doneBounds.Contains(e.Location))
        {
            FinishMove();
        }
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (_moveMode && keyData is Keys.Escape or Keys.Enter)
        {
            FinishMove();
            return true;
        }

        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_moveMode && e.KeyCode is Keys.Escape or Keys.Enter)
        {
            e.Handled = true;
            FinishMove();
        }
    }

    private void FinishMove()
    {
        if (ClampToScreens(Location) is { } clamped)
        {
            Location = clamped;
        }

        _customLocation = Location;
        MoveFinished?.Invoke(Location);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var graphics = e.Graphics;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Background);

        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var border = new Pen(_moveMode ? _accent : BorderColor, _moveMode ? Scale(2) : 1))
        {
            if (_moveMode)
            {
                border.DashStyle = DashStyle.Dash;
                bounds.Inflate(-Scale(1), -Scale(1));
            }

            using var path = RoundedRectangle(bounds, Scale(CornerRadius));
            graphics.DrawPath(border, path);
        }

        using (var accent = new SolidBrush(_accent))
        {
            var barTop = Scale(12);
            graphics.FillRectangle(accent, Scale(_moveMode ? 3 : 1), barTop, Scale(3), Height - barTop * 2);
        }

        var padX = Scale(PaddingX);
        var headerText = _moveMode ? "DRAG TO MOVE · CLICK DONE" : _title;
        var headerRight = Width - padX;
        _doneBounds = Rectangle.Empty;
        if (_moveMode)
        {
            var buttonSize = TextRenderer.MeasureText("Done", _buttonFont);
            _doneBounds = new Rectangle(Width - padX - buttonSize.Width - Scale(16) + Scale(6), Scale(3), buttonSize.Width + Scale(16), Scale(17));
            using var buttonBrush = new SolidBrush(_accent);
            using var buttonPath = RoundedRectangle(_doneBounds, Scale(5));
            graphics.FillPath(buttonBrush, buttonPath);
            TextRenderer.DrawText(graphics, "Done", _buttonFont, _doneBounds, Background,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding);
            headerRight = _doneBounds.Left - Scale(8);
        }

        TextRenderer.DrawText(graphics, headerText, _titleFont,
            new Rectangle(padX, Scale(4), headerRight - padX, Scale(HeaderHeight - 7)),
            _accent, TextFlags);

        var labelX = padX + Scale(DotSize + DotGap);
        var labelWidth = _rows
            .Select(row => row.Label)
            .Append("Grenade")
            .Max(label => TextRenderer.MeasureText(graphics, label, _labelFont, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix).Width);
        var valueX = labelX + labelWidth + Scale(ColumnGap);
        var valueWidth = Width - padX - valueX - (_moveMode ? Scale(28) : 0);
        var line1Height = Scale(Line1Height);
        var line2Height = Scale(Line2Height);
        var y = Scale(HeaderHeight);
        using var dotBrush = new SolidBrush(NeutralDotColor);
        foreach (var row in _rows)
        {
            var dotSize = Scale(DotSize);
            dotBrush.Color = row.StatusColor ?? NeutralDotColor;
            graphics.FillEllipse(dotBrush, padX, y + (line1Height - dotSize) / 2f, dotSize, dotSize);
            TextRenderer.DrawText(graphics, row.Label, _labelFont, new Rectangle(labelX, y, labelWidth + Scale(2), line1Height), LabelColor, TextFlags);
            TextRenderer.DrawText(graphics, row.Line1, _line1Font, new Rectangle(valueX, y, valueWidth, line1Height), PrimaryTextColor, TextFlags);
            if (row.Progress is { } progress)
            {
                // Thin recharge fill under the first line; drawn without anti-aliasing so it stays crisp.
                var barHeight = Math.Max(1, Scale(2));
                var barTop = y + line1Height - barHeight + Scale(1);
                graphics.SmoothingMode = SmoothingMode.None;
                using (var track = new SolidBrush(BorderColor))
                {
                    graphics.FillRectangle(track, valueX, barTop, valueWidth, barHeight);
                }

                dotBrush.Color = row.StatusColor ?? NeutralDotColor;
                graphics.FillRectangle(dotBrush, valueX, barTop, (int)Math.Round(valueWidth * Math.Clamp(progress, 0f, 1f)), barHeight);
                graphics.SmoothingMode = SmoothingMode.AntiAlias;
            }
            y += line1Height;
            if (_twoLines)
            {
                if (!string.IsNullOrEmpty(row.Line2))
                {
                    TextRenderer.DrawText(graphics, row.Line2, _line2Font, new Rectangle(valueX, y, valueWidth, line2Height), SecondaryTextColor, TextFlags);
                }

                y += line2Height;
            }

            y += Scale(RowGap);
        }
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
            DisposeFonts();
        }

        base.Dispose(disposing);
    }

    internal static GraphicsPath RoundedRectangle(Rectangle bounds, int radius)
    {
        var path = new GraphicsPath();
        var diameter = Math.Max(1, radius * 2);
        path.AddArc(bounds.X, bounds.Y, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Y, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.X, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static IntPtr GetWindowLongPtr(IntPtr handle, int index) =>
        IntPtr.Size == 8 ? GetWindowLongPtr64(handle, index) : new IntPtr(GetWindowLong32(handle, index));

    private static IntPtr SetWindowLongPtr(IntPtr handle, int index, IntPtr value) =>
        IntPtr.Size == 8 ? SetWindowLongPtr64(handle, index, value) : new IntPtr(SetWindowLong32(handle, index, value.ToInt32()));

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr64(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong32(IntPtr handle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr64(IntPtr handle, int index, IntPtr value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong32(IntPtr handle, int index, int value);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr handle, int attribute, ref int value, int size);
}

internal static class GraphicsExtensions
{
    public static void FillRoundedRectangle(this Graphics graphics, Brush brush, Rectangle bounds, int radius)
    {
        using var path = OverlayWindow.RoundedRectangle(bounds, radius);
        graphics.FillPath(brush, path);
    }
}
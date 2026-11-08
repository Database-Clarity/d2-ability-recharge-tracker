namespace D2AbilityTracker;

/// <summary>
/// A label whose text can change several times a second without flashing. A WinForms Label is a native owner-drawn
/// STATIC control: on every text change Windows makes it draw itself at once through WM_DRAWITEM, straight to the
/// screen and bypassing double buffering, so the clear and the redraw show as separate frames. Those immediate draws
/// are dropped here and the control repaints through its normal double-buffered WM_PAINT instead.
/// </summary>
internal class LiveLabel : Label
{
    private const int ReflectedDrawItem = 0x2000 + 0x002B; // WM_REFLECT | WM_DRAWITEM
    private const int SetText = 0x000C;

    /// <summary>Immediate unbuffered draws dropped so far (for tests).</summary>
    internal int DroppedDirectDraws { get; private set; }

    public LiveLabel()
    {
        DoubleBuffered = true;
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
    }

    protected override void OnPaintBackground(PaintEventArgs pevent)
    {
        // The whole control, background included, is drawn in OnPaint from one off-screen image.
    }

    /// <summary>
    /// Renders the control into a private off-screen image and copies it to the screen in one step. Framework double
    /// buffering was not taking effect for these labels on the user's system: rows were seen half-drawn on screen.
    /// </summary>
    protected sealed override void OnPaint(PaintEventArgs e)
    {
        if (Width <= 0 || Height <= 0) return;
        using var image = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppRgb);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.Clear(EffectiveBackColor());
            Render(new PaintEventArgs(graphics, ClientRectangle));
        }

        e.Graphics.DrawImageUnscaled(image, 0, 0);
    }

    /// <summary>Draws the control's content; the default draws it as a standard label.</summary>
    protected virtual void Render(PaintEventArgs e) => base.OnPaint(e);

    private Color EffectiveBackColor()
    {
        for (Control? control = this; control is not null; control = control.Parent)
        {
            if (control.BackColor.A == 255) return control.BackColor;
        }

        return TrackerTheme.Background;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == ReflectedDrawItem)
        {
            DroppedDirectDraws++;
            m.Result = 1;
            return;
        }

        // Redraw is off while the native control takes the new text, so it can't paint by itself (WS_VISIBLE is only
        // touched when it was set: turning redraw back on makes a window visible).
        var suppress = m.Msg == SetText && IsHandleCreated && (GetWindowLong(Handle, WindowStyle) & WindowVisible) != 0;
        if (suppress) SendMessage(Handle, SetRedraw, IntPtr.Zero, IntPtr.Zero);
        try
        {
            base.WndProc(ref m);
        }
        finally
        {
            if (suppress)
            {
                SendMessage(Handle, SetRedraw, 1, IntPtr.Zero);
                Invalidate();
            }
        }
    }

    private const int SetRedraw = 0x000B;
    private const int WindowStyle = -16;
    private const int WindowVisible = 0x10000000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr window, int message, IntPtr wParam, IntPtr lParam);
}

internal sealed class TwoLineStatusLabel : LiveLabel
{

    protected override void Render(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var lines = Text.ReplaceLineEndings("\n").Split('\n', 2);
        for (var index = 0; index < 2; index++)
        {
            var bounds = new Rectangle(0, index * Height / 2, Width, Height / 2);
            TextRenderer.DrawText(e.Graphics, index < lines.Length ? lines[index] : string.Empty, Font, bounds,
                ForeColor, TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>A label that repaints off-screen, for text that changes several times a second.</summary>
internal sealed class BufferedLabel : LiveLabel
{
}

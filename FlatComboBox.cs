namespace D2AbilityTracker;

internal sealed class FlatComboBox : ComboBox
{
    public FlatComboBox()
    {
        DropDownStyle = ComboBoxStyle.DropDownList;
        FlatStyle = FlatStyle.Flat;
        BackColor = TrackerTheme.Surface;
        ForeColor = TrackerTheme.Text;
        DrawMode = DrawMode.OwnerDrawFixed;
        ItemHeight = 24;
        IntegralHeight = false;
        DropDownHeight = 240;
    }

    protected override void OnDrawItem(DrawItemEventArgs e)
    {
        using var background = new SolidBrush((e.State & DrawItemState.Selected) != 0
            ? Color.FromArgb(55, 75, 90) : BackColor);
        e.Graphics.FillRectangle(background, e.Bounds);
        var text = e.Index >= 0 ? GetItemText(Items[e.Index]) : Text;
        TextRenderer.DrawText(e.Graphics, text, Font, Rectangle.Inflate(e.Bounds, -Scale(10), 0), ForeColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        base.OnDrawItem(e);
    }

    protected override void WndProc(ref Message m)
    {
        // WM_PRINT/WM_PRINTCLIENT must not redraw the native arrow over our field.
        if (m.Msg is 0x0317 or 0x0318 && m.WParam != IntPtr.Zero)
        {
            using var graphics = Graphics.FromHdc(m.WParam);
            DrawField(graphics);
            m.Result = IntPtr.Zero;
            return;
        }
        base.WndProc(ref m);
        if (m.Msg != 0x000F) return;
        using var fieldGraphics = Graphics.FromHwnd(Handle);
        DrawField(fieldGraphics);
    }

    private void DrawField(Graphics graphics)
    {
        using var background = new SolidBrush(BackColor);
        graphics.FillRectangle(background, ClientRectangle);
        using var border = new Pen(Focused ? TrackerTheme.Accent : TrackerTheme.Border, Scale(1));
        graphics.DrawRectangle(border, 0, 0, Width - 1, Height - 1);
        TextRenderer.DrawText(graphics, Text, Font, new Rectangle(Scale(10), 0, Math.Max(0, Width - Scale(40)), Height),
            Enabled ? ForeColor : TrackerTheme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
        using var arrow = new Pen(Enabled ? ForeColor : TrackerTheme.Muted, 1.5f * DeviceDpi / 96f);
        var x = Width - Scale(18);
        var y = Height / 2;
        graphics.DrawLines(arrow, [new Point(x - Scale(4), y - Scale(2)), new Point(x, y + Scale(2)), new Point(x + Scale(4), y - Scale(2))]);
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96f);

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
}

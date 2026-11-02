using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace D2AbilityTracker;

internal static class TrackerTheme
{
    internal static readonly Color Background = Color.FromArgb(19, 22, 29);
    internal static readonly Color Card = Color.FromArgb(28, 33, 43);
    internal static readonly Color Surface = Color.FromArgb(38, 45, 58);
    internal static readonly Color Border = Color.FromArgb(52, 62, 78);
    internal static readonly Color Text = Color.FromArgb(233, 239, 248);
    internal static readonly Color Muted = Color.FromArgb(152, 167, 188);
    internal static readonly Color Accent = Color.FromArgb(123, 198, 240);
    internal static readonly Color Green = Color.FromArgb(117, 218, 164);
    internal static readonly Color Orange = Color.FromArgb(246, 187, 105);
    internal static readonly Color Red = Color.FromArgb(255, 139, 148);

    internal static void Apply(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor = control is Label ? control.ForeColor : Text;
            switch (control)
            {
                case Button button:
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = Border;
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 65, 83);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(67, 83, 106);
                    button.BackColor = Surface;
                    button.UseVisualStyleBackColor = false;
                    button.Cursor = Cursors.Hand;
                    break;
                case CheckBox toggle when toggle.Appearance == Appearance.Button:
                    toggle.FlatStyle = FlatStyle.Flat;
                    toggle.FlatAppearance.BorderColor = Border;
                    toggle.FlatAppearance.CheckedBackColor = Color.FromArgb(49, 88, 110);
                    toggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(53, 65, 83);
                    toggle.BackColor = Surface;
                    toggle.UseVisualStyleBackColor = false;
                    toggle.Cursor = Cursors.Hand;
                    break;
                case ComboBox combo:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Surface;
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    combo.ItemHeight = 24;
                    combo.DrawItem += (_, args) =>
                    {
                        using var brush = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Color.FromArgb(49, 88, 110) : Surface);
                        args.Graphics.FillRectangle(brush, args.Bounds);
                        var text = args.Index >= 0 ? combo.GetItemText(combo.Items[args.Index]) : combo.Text;
                        TextRenderer.DrawText(args.Graphics, text, combo.Font, Rectangle.Inflate(args.Bounds, -6, 0), Text,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    };
                    break;
                case NumericUpDown number:
                    number.BackColor = Surface;
                    number.BorderStyle = BorderStyle.FixedSingle;
                    break;
            }
            Apply(control);
        }
    }

    private sealed record Typography(float Size, FontStyle Style);

    internal static void SetTypography(Control control, float size, FontStyle style)
    {
        control.Tag = new Typography(size, style);
        control.Font = new Font("Segoe UI", size, style);
    }

    internal static void ScaleTypography(Control parent, float scale)
    {
        if (parent.Tag is Typography typography)
        {
            var previous = parent.Font;
            parent.Font = new Font("Segoe UI", typography.Size * scale, typography.Style);
            previous.Dispose();
        }
        foreach (Control child in parent.Controls)
        {
            ScaleTypography(child, scale);
        }
    }

    internal static void Primary(Button button)
    {
        button.BackColor = Accent;
        button.ForeColor = Background;
        button.FlatAppearance.BorderColor = Accent;
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(157, 215, 248);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(95, 169, 210);
        SetTypography(button, 9, FontStyle.Bold);
    }

    internal static void DarkTitleBar(IntPtr handle)
    {
        var enabled = 1;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        var caption = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

internal sealed class TrackerCard : Panel
{
    internal TrackerCard()
    {
        DoubleBuffered = true;
        BackColor = TrackerTheme.Card;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        var radius = Math.Max(8, (int)(12 * DeviceDpi / 96f));
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new Size(radius, radius));
        using var pen = new Pen(TrackerTheme.Border);
        e.Graphics.DrawPath(pen, path);
    }
}

internal sealed class TrackerButton : Button
{
    private bool _hovered;
    private bool _pressed;

    internal TrackerButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { _pressed = true; Invalidate(); } base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) { _pressed = false; Invalidate(); } base.OnKeyUp(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = !Enabled ? TrackerTheme.Surface : _pressed ? FlatAppearance.MouseDownBackColor
            : _hovered ? FlatAppearance.MouseOverBackColor : BackColor;
        ActionPainter.Paint(this, e.Graphics, fill, Focused ? TrackerTheme.Accent : FlatAppearance.BorderColor,
            Enabled ? ForeColor : TrackerTheme.Muted, pill: false);
    }
}

internal sealed class TrackerToggle : CheckBox
{
    private bool _hovered;

    internal TrackerToggle()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { _hovered = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hovered = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnCheckedChanged(EventArgs e) { Invalidate(); base.OnCheckedChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var fill = Checked ? FlatAppearance.CheckedBackColor : _hovered ? FlatAppearance.MouseOverBackColor : BackColor;
        ActionPainter.Paint(this, e.Graphics, fill, Checked || Focused ? TrackerTheme.Accent : TrackerTheme.Border,
            Checked ? TrackerTheme.Accent : ForeColor, pill: true);
    }
}

internal static class ActionPainter
{
    internal static void Paint(Control control, Graphics graphics, Color fill, Color border, Color text, bool pill)
    {
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, control.Width - 1, control.Height - 1);
        var radius = pill ? bounds.Height : Math.Max(6, (int)(6 * control.DeviceDpi / 96f));
        using var path = new GraphicsPath();
        path.AddRoundedRectangle(bounds, new Size(radius, radius));
        graphics.Clear(control.Parent?.BackColor ?? TrackerTheme.Card);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(border);
        graphics.FillPath(brush, path);
        graphics.DrawPath(pen, path);
        TextRenderer.DrawText(graphics, control.Text, control.Font, Rectangle.Inflate(bounds, -5, 0), text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

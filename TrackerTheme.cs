using System.Runtime.InteropServices;

namespace D2AbilityTracker;

internal static class TrackerTheme
{
    internal const int ControlHeight = 30;
    internal static readonly Color Background = Color.FromArgb(30, 30, 30);
    internal static readonly Color Surface = Color.FromArgb(43, 43, 43);
    internal static readonly Color Border = Color.FromArgb(72, 72, 72);
    internal static readonly Color Text = Color.FromArgb(235, 235, 235);
    internal static readonly Color Muted = Color.FromArgb(175, 175, 175);
    internal static readonly Color Accent = Color.FromArgb(130, 180, 218);
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
                    button.MinimumSize = new Size(button.MinimumSize.Width, ControlHeight);
                    button.Height = ControlHeight;
                    button.FlatStyle = FlatStyle.Flat;
                    button.FlatAppearance.BorderColor = button.Enabled ? Border : Color.FromArgb(55, 55, 55);
                    button.EnabledChanged += (_, _) => button.FlatAppearance.BorderColor =
                        button.Enabled ? Border : Color.FromArgb(55, 55, 55);
                    button.FlatAppearance.MouseOverBackColor = Color.FromArgb(57, 57, 57);
                    button.FlatAppearance.MouseDownBackColor = Color.FromArgb(68, 68, 68);
                    button.BackColor = Surface;
                    button.UseVisualStyleBackColor = false;
                    button.Cursor = Cursors.Hand;
                    break;
                case CheckBox toggle when toggle.Appearance == Appearance.Button:
                    toggle.Height = ControlHeight;
                    toggle.FlatStyle = FlatStyle.Flat;
                    toggle.FlatAppearance.BorderColor = Border;
                    toggle.FlatAppearance.CheckedBackColor = Color.FromArgb(55, 75, 90);
                    toggle.FlatAppearance.MouseOverBackColor = Color.FromArgb(57, 57, 57);
                    toggle.BackColor = Surface;
                    toggle.UseVisualStyleBackColor = false;
                    toggle.Cursor = Cursors.Hand;
                    break;
                case ComboBox combo when combo is not FlatComboBox:
                    combo.FlatStyle = FlatStyle.Flat;
                    combo.BackColor = Surface;
                    combo.DrawMode = DrawMode.OwnerDrawFixed;
                    combo.ItemHeight = 20;
                    combo.DrawItem += (_, args) =>
                    {
                        using var brush = new SolidBrush((args.State & DrawItemState.Selected) != 0 ? Color.FromArgb(55, 75, 90) : Surface);
                        args.Graphics.FillRectangle(brush, args.Bounds);
                        var text = args.Index >= 0 ? combo.GetItemText(combo.Items[args.Index]) : combo.Text;
                        TextRenderer.DrawText(args.Graphics, text, combo.Font, Rectangle.Inflate(args.Bounds, -6, 0), Text,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
                    };
                    break;
                case NumericUpDown number:
                    number.BackColor = Surface;
                    number.BorderStyle = number.Parent is InputFrame ? BorderStyle.None : BorderStyle.FixedSingle;
                    break;
            }
            Apply(control);
        }
    }

    internal static void SetTypography(Control control, float size, FontStyle style)
    {
        control.Font = new Font("Segoe UI", size, style);
    }

    internal static void Primary(Button button)
    {
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.FlatAppearance.BorderColor = button.Enabled ? Accent : Color.FromArgb(55, 55, 55);
        button.EnabledChanged += (_, _) => button.FlatAppearance.BorderColor =
            button.Enabled ? Accent : Color.FromArgb(55, 55, 55);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(57, 57, 57);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(68, 68, 68);
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

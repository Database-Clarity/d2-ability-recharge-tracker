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

    internal static Color ToneColor(StatusTone tone) => tone switch
    {
        StatusTone.Good => Green,
        StatusTone.Warning => Orange,
        StatusTone.Error => Red,
        _ => Muted
    };

    internal static void Apply(Control parent)
    {
        foreach (Control control in parent.Controls)
        {
            control.ForeColor = control is Label ? control.ForeColor : Text;
            switch (control)
            {
                case Button button when button is not PillButton:
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
                    button.Paint += (_, args) =>
                    {
                        if (button.Enabled || button is CopyResultButton) return;
                        using var background = new SolidBrush(Surface);
                        args.Graphics.FillRectangle(background, button.ClientRectangle);
                        using var outline = new Pen(Border);
                        args.Graphics.DrawRectangle(outline, 0, 0, button.Width - 1, button.Height - 1);
                        TextRenderer.DrawText(args.Graphics, button.Text, button.Font, button.ClientRectangle, Muted,
                            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                    };
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

    internal static readonly Color AccentHover = Color.FromArgb(158, 200, 232);
    internal static readonly Color AccentPressed = Color.FromArgb(108, 158, 196);
    internal static readonly Color Selected = Color.FromArgb(55, 75, 90);
    internal static readonly Color Hover = Color.FromArgb(57, 57, 57);

    /// <summary>Primary tier: the single most important action on a page, filled with the accent color.</summary>
    internal static void Primary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.FlatAppearance.MouseOverBackColor = AccentHover;
        button.FlatAppearance.MouseDownBackColor = AccentPressed;
        button.Cursor = Cursors.Hand;
        SetTypography(button, 9, FontStyle.Bold);
        // Disabled primary actions fall back to the muted outlined look so they never read as available.
        void Sync()
        {
            button.BackColor = button.Enabled ? Accent : Surface;
            button.ForeColor = button.Enabled ? Background : Text;
            button.FlatAppearance.BorderSize = button.Enabled ? 0 : 1;
            button.FlatAppearance.BorderColor = button.Enabled ? Accent : Color.FromArgb(55, 55, 55);
        }

        Sync();
        button.EnabledChanged += (_, _) => Sync();
        button.Paint += (_, args) =>
        {
            if (button.Enabled) return;
            using var background = new SolidBrush(Surface);
            args.Graphics.FillRectangle(background, button.ClientRectangle);
            using var outline = new Pen(Color.FromArgb(55, 55, 55));
            args.Graphics.DrawRectangle(outline, 0, 0, button.Width - 1, button.Height - 1);
            TextRenderer.DrawText(args.Graphics, button.Text, button.Font, button.ClientRectangle, Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        };
    }

    /// <summary>Secondary tier: the standard outlined button applied by <see cref="Apply"/>.</summary>
    internal static void Secondary(Button button)
    {
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = Surface;
        button.ForeColor = Text;
        button.FlatAppearance.BorderSize = 1;
        button.FlatAppearance.BorderColor = Border;
        button.FlatAppearance.MouseOverBackColor = Hover;
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(68, 68, 68);
        button.Cursor = Cursors.Hand;
    }

    /// <summary>Tertiary tier: a borderless link-style action that blends into its surface.</summary>
    internal static void Link(Button button, Color? surface = null)
    {
        var background = surface ?? Background;
        button.FlatStyle = FlatStyle.Flat;
        button.UseVisualStyleBackColor = false;
        button.BackColor = background;
        button.ForeColor = Accent;
        button.FlatAppearance.BorderSize = 0;
        button.FlatAppearance.BorderColor = background;
        button.FlatAppearance.MouseOverBackColor = Blend(background, Text, 0.08f);
        button.FlatAppearance.MouseDownBackColor = Blend(background, Text, 0.14f);
        button.Cursor = Cursors.Hand;
    }

    internal static void SectionHeading(Label heading)
    {
        SetTypography(heading, 10, FontStyle.Bold);
        heading.ForeColor = Text;
    }

    internal static Color Blend(Color from, Color to, float amount) => Color.FromArgb(
        (int)Math.Round(from.R + (to.R - from.R) * amount),
        (int)Math.Round(from.G + (to.G - from.G) * amount),
        (int)Math.Round(from.B + (to.B - from.B) * amount));

    /// <summary>Creates a dark-themed dropdown menu that matches the app surfaces.</summary>
    internal static ContextMenuStrip Menu(string name)
    {
        var menu = new ContextMenuStrip
        {
            Name = name,
            Renderer = new DarkMenuRenderer(),
            BackColor = Surface,
            ForeColor = Text,
            Font = new Font("Segoe UI", 9),
            ShowImageMargin = false,
            ShowCheckMargin = false,
            DropShadowEnabled = true,
            Padding = new Padding(1, 4, 1, 4)
        };
        return menu;
    }

    private sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new DarkMenuColors()) => RoundedEdges = false;

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            // Shortcut-display text (for example an area size) is secondary information.
            e.TextColor = !e.Item.Enabled || e.Text != e.Item.Text ? Muted : Text;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Pen(Border);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 8, y, e.Item.Width - 8, y);
        }
    }

    private sealed class DarkMenuColors : ProfessionalColorTable
    {
        public DarkMenuColors() => UseSystemColors = false;
        public override Color ToolStripDropDownBackground => Surface;
        public override Color ImageMarginGradientBegin => Surface;
        public override Color ImageMarginGradientMiddle => Surface;
        public override Color ImageMarginGradientEnd => Surface;
        public override Color MenuBorder => Border;
        public override Color MenuItemBorder => Selected;
        public override Color MenuItemSelected => Selected;
        public override Color MenuItemSelectedGradientBegin => Selected;
        public override Color MenuItemSelectedGradientEnd => Selected;
        public override Color MenuItemPressedGradientBegin => Selected;
        public override Color MenuItemPressedGradientEnd => Selected;
        public override Color CheckBackground => Surface;
        public override Color CheckSelectedBackground => Selected;
        public override Color CheckPressedBackground => Selected;
        public override Color SeparatorDark => Border;
        public override Color SeparatorLight => Border;
    }

    internal static void DarkTitleBar(IntPtr handle)
    {
        var enabled = 1;
        DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int));
        var caption = ColorTranslator.ToWin32(Background);
        DwmSetWindowAttribute(handle, 35, ref caption, sizeof(int));
    }

    /// <summary>Gives a native control (such as a multiline TextBox) dark scroll bars.</summary>
    internal static void DarkScrollBars(Control control)
    {
        void Apply() => SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        if (control.IsHandleCreated) Apply();
        control.HandleCreated += (_, _) => Apply();
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string? subAppName, string? subIdList);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}

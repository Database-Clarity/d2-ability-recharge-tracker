using System.Drawing;
using System.Drawing.Drawing2D;

namespace D2AbilityTracker;

internal sealed class SelectionGuide : AppWindow
{
    public SelectionGuide()
    {
        Text = "Ability region selection guide";
        ClientSize = new Size(740, 560 + CaptionHeight);
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = TrackerTheme.Background;
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        var close = new Button
        {
            Text = "Got it",
            DialogResult = DialogResult.OK,
            Location = new Point(610, 514 + CaptionHeight),
            Size = new Size(108, 32),
            ForeColor = Color.White,
            BackColor = Color.FromArgb(42, 48, 60),
            FlatStyle = FlatStyle.Flat,
            UseVisualStyleBackColor = false
        };
        Controls.Add(close);
        AcceptButton = close;
        CancelButton = close;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        var state = graphics.Save();
        graphics.TranslateTransform(0, CaptionHeight);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(200, 211, 224));
        using var good = new SolidBrush(Color.FromArgb(112, 223, 160));
        using var title = new Font(Font, FontStyle.Bold);
        using var selection = new Pen(Color.FromArgb(246, 187, 67), 3) { DashStyle = DashStyle.Dash };
        graphics.DrawString("1  Outline the ability square", title, white, 22, 18);
        DrawSlot(graphics, 65, 65, true);
        graphics.DrawRectangle(selection, 63, 63, 144, 144);
        graphics.DrawString(
            "•  Line the box up with the square's outer border.\n\n" +
            "•  Auto-detect places all three boxes for you; adjust\n    only a box that looks off.\n\n" +
            "•  A thin margin above and below the box is added\n    automatically, so you don't need to include it.\n\n" +
            "•  Any subclass or element works.",
            Font, muted, 260, 70);

        graphics.DrawString("2  How the timer starts", title, white, 22, 305);
        DrawSlot(graphics, 40, 345, false, 76);
        DrawSlot(graphics, 245, 345, false, 76);
        using var flash = new Pen(Color.FromArgb(255, 246, 187, 67), 5);
        graphics.DrawLine(flash, 247, 424, 319, 424);
        graphics.DrawString("Normal border", Font, muted, 25, 439);
        graphics.DrawString("Brief thickening", Font, good, 228, 439);
        graphics.DrawString("Timing starts on the frame where the\nbottom border briefly thickens, which\nis when the game begins the recharge.", Font, muted, 410, 357);
        graphics.DrawString("3  Wheel / +/-: zoom   |   Right/middle-drag: pan\nDrag inside: move   |   Drag corners: resize   |   Use all three / Use selection: confirm", Font, white, 22, 481);
        graphics.Restore(state);
    }

    private static void DrawSlot(Graphics graphics, int x, int y, bool charged, int size = 140)
    {
        using var background = new SolidBrush(charged ? Color.FromArgb(161, 75, 39) : Color.FromArgb(36, 41, 51));
        using var border = new Pen(Color.FromArgb(210, 214, 222), 2);
        using var artwork = new Pen(Color.FromArgb(225, 225, 230), 3);
        graphics.FillRectangle(background, x, y, size, size);
        graphics.DrawRectangle(border, x, y, size, size);
        graphics.DrawEllipse(artwork, x + size * 0.34f, y + size * 0.34f, size * 0.32f, size * 0.32f);
    }
}

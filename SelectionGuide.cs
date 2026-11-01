using System.Drawing;
using System.Drawing.Drawing2D;

namespace D2AbilityTracker;

internal sealed class SelectionGuide : Form
{
    public SelectionGuide()
    {
        Text = "Ability region selection guide";
        ClientSize = new Size(740, 560);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        TopMost = true;
        BackColor = Color.FromArgb(18, 22, 30);
        ForeColor = Color.White;
        Font = new Font("Segoe UI", 10);
        DoubleBuffered = true;
        var close = new Button
        {
            Text = "Got it",
            DialogResult = DialogResult.OK,
            Location = new Point(610, 514),
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
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var white = new SolidBrush(Color.White);
        using var muted = new SolidBrush(Color.FromArgb(200, 211, 224));
        using var good = new SolidBrush(Color.FromArgb(112, 223, 160));
        using var bad = new SolidBrush(Color.FromArgb(255, 130, 130));
        using var title = new Font(Font, FontStyle.Bold);
        using var selection = new Pen(Color.FromArgb(246, 187, 67), 3) { DashStyle = DashStyle.Dash };
        graphics.DrawString("1  Select the WHOLE square, not just the artwork", title, white, 22, 18);
        DrawSlot(graphics, 65, 65, true);
        DrawSlot(graphics, 405, 65, true);
        graphics.DrawRectangle(selection, 63, 63, 144, 144);
        graphics.DrawRectangle(selection, 448, 106, 54, 54);
        graphics.DrawString("YES: background + border", title, good, 35, 222);
        graphics.DrawString("NO: icon only", title, bad, 410, 222);
        graphics.DrawString("Keep the outline tight. The strip below it\nis captured automatically.", Font, muted, 35, 250);
        graphics.DrawString("Artwork and element colour are not\nused to identify the ability.", Font, muted, 410, 250);

        graphics.DrawString("2  The flash UNDER the bottom border starts the timer", title, white, 22, 305);
        DrawSlot(graphics, 40, 345, false, 76);
        DrawSlot(graphics, 245, 345, false, 76);
        using var flash = new Pen(Color.FromArgb(255, 246, 187, 67), 5);
        graphics.DrawLine(flash, 247, 424, 319, 424);
        graphics.DrawString("Normal border", Font, muted, 25, 439);
        graphics.DrawString("Brief thickening", Font, good, 228, 439);
        graphics.DrawString("Start at this captured frame.\nThe moving line inside the square\nis NOT the start trigger.", Font, muted, 410, 357);
        graphics.DrawString("3  Wheel / +/-: zoom   |   Right/middle-drag: pan\nDrag inside: move   |   Drag corners: resize   |   Use selection: save", Font, white, 22, 481);
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

namespace D2AbilityTracker;

internal sealed class TwoLineStatusLabel : Label
{
    protected override void OnPaint(PaintEventArgs e)
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

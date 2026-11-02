namespace D2AbilityTracker;

internal sealed class NavigationButton : Button
{
    private bool _selected;

    public void SetSelected(bool selected)
    {
        _selected = selected;
        ForeColor = selected ? TrackerTheme.Text : TrackerTheme.Muted;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        if (!_selected) return;
        using var underline = new SolidBrush(TrackerTheme.Accent);
        var thickness = Math.Max(2, (int)Math.Round(2 * DeviceDpi / 96f));
        e.Graphics.FillRectangle(underline, 8, Height - thickness, Math.Max(0, Width - 16), thickness);
    }
}

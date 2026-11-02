namespace D2AbilityTracker;

internal sealed class CopyResultButton : Button
{
    private readonly Func<string> _text;
    private bool _copied;
    private bool _hovered;
    private bool _revealed = true;
    internal string ResultText => _text();

    internal CopyResultButton(string name, Func<string> text)
    {
        _text = text;
        AccessibleName = name;
        AccessibleDescription = "Copy the complete displayed result to the clipboard.";
        Size = new Size(30, 30);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(ResultText);
                _copied = true;
                AccessibleDescription = "Result copied to clipboard.";
                Invalidate();
            }
            catch (Exception exception) when (exception is System.Runtime.InteropServices.ExternalException or ThreadStateException)
            {
                MessageBox.Show(this, $"Couldn't copy the result: {exception.Message}", "Copy result",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _copied = false; _hovered = false; Invalidate(); };
        GotFocus += (_, _) => Invalidate();
        LostFocus += (_, _) => Invalidate();
    }

    /// <summary>
    /// When false, the icon is only drawn while hovered or focused; the button stays clickable and keyboard focusable.
    /// </summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    internal bool Revealed
    {
        get => _revealed;
        set
        {
            if (_revealed == value) return;
            _revealed = value;
            Invalidate();
        }
    }

    internal bool IconVisible => _revealed || _hovered || Focused || _copied;

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(_hovered && !FlatAppearance.MouseOverBackColor.IsEmpty ? FlatAppearance.MouseOverBackColor : BackColor);
        if (!IconVisible) return;
        var scale = DeviceDpi / 96f;
        var x = (Width - 16 * scale) / 2;
        var y = (Height - 16 * scale) / 2;
        using var pen = new Pen(Enabled ? TrackerTheme.Text : TrackerTheme.Muted, 1.4f * scale);
        if (_copied)
            e.Graphics.DrawLines(pen, [new PointF(x + 2 * scale, y + 8 * scale),
                new PointF(x + 6 * scale, y + 12 * scale), new PointF(x + 14 * scale, y + 3 * scale)]);
        else
        {
            e.Graphics.DrawRectangle(pen, x + 5 * scale, y + 5 * scale, 10 * scale, 11 * scale);
            e.Graphics.DrawLines(pen, [new PointF(x + 2 * scale, y + 11 * scale),
                new PointF(x, y + 11 * scale), new PointF(x, y), new PointF(x + 10 * scale, y),
                new PointF(x + 10 * scale, y + 2 * scale)]);
        }
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, Rectangle.Inflate(ClientRectangle, -3, -3));
    }
}

using System.Drawing;
using System.Drawing.Drawing2D;

namespace D2AbilityTracker;

internal sealed class RegionPicker : Form
{
    private readonly Bitmap _snapshot;
    private readonly Rectangle _sourceBounds;
    private readonly string _sourceDescription;
    private readonly Panel _toolbar = new();
    private readonly Panel _footer = new();
    private readonly Label _instructions = new();
    private readonly Label _zoomLabel = new();
    private readonly Button _zoomOutButton = new();
    private readonly Button _zoomInButton = new();
    private readonly Button _guideButton = new();
    private readonly Button _applyButton = new();
    private readonly Button _selectionApplyButton = new();
    private readonly List<Button> _legendButtons = [];
    private readonly RectangleF _viewport;
    private PointF _sourceCenter;
    private PointF _startSource;
    private PointF _lastSource;
    private PointF _resizeAnchor;
    private readonly RectangleF?[] _selections;
    private readonly Color[] _selectionColors = [TrackerTheme.Orange, TrackerTheme.Accent, TrackerTheme.Green];
    private int _activeSelection;
    private RectangleF? _selection
    {
        get => _selections[_activeSelection];
        set => _selections[_activeSelection] = value;
    }
    private bool _draggingSelection;
    private bool _movingSelection;
    private bool _resizingSelection;
    private bool _panning;
    private float _zoom = 1f;
    private float _scale;

    public Rectangle? SelectedRegion { get; private set; }
    public Rectangle[] SelectedRegions { get; private set; } = [];

    public RegionPicker(
        Bitmap snapshot,
        Rectangle sourceBounds,
        Rectangle monitorBounds,
        string sourceDescription,
        Rectangle? initialSelection = null,
        Rectangle[]? initialSelections = null)
    {
        if (snapshot.Width != sourceBounds.Width || snapshot.Height != sourceBounds.Height)
        {
            throw new ArgumentException("The preview dimensions must match its source bounds.", nameof(snapshot));
        }

        if (initialSelections is not null && initialSelections.Length != 3)
        {
            throw new ArgumentException("All three ability regions are required.", nameof(initialSelections));
        }
        _snapshot = (Bitmap)snapshot.Clone();
        _selections = new RectangleF?[initialSelections?.Length ?? 1];
        _sourceBounds = sourceBounds;
        _sourceDescription = sourceDescription;
        _sourceCenter = new PointF(snapshot.Width / 2f, snapshot.Height / 2f);
        var toolbarHeight = initialSelections is null ? 86 : 110;
        _viewport = new RectangleF(0, toolbarHeight, monitorBounds.Width, Math.Max(1, monitorBounds.Height - toolbarHeight - 52));

        Bounds = monitorBounds;
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Cursor = Cursors.Cross;
        BackColor = Color.FromArgb(14, 17, 24);
        DoubleBuffered = true;
        KeyPreview = true;
        Font = new Font("Segoe UI", 9);

        _toolbar.BackColor = Color.FromArgb(14, 17, 24);
        _toolbar.Dock = DockStyle.Top;
        _toolbar.Height = toolbarHeight;
        _toolbar.Padding = new Padding(18, 8, 18, 8);
        _instructions.Text = $"Select inside {_sourceDescription}. Drag to mark the ability slot; adjust it before applying.";
        _instructions.ForeColor = Color.White;
        _instructions.Font = new Font("Segoe UI", 9, FontStyle.Bold);
        _instructions.AutoEllipsis = true;
        _instructions.SetBounds(18, 8, Math.Max(200, monitorBounds.Width - 250), 24);
        var hint = new Label
        {
            Text = "Drag inside the box to move it; drag a corner to resize. Scroll or use +/- to zoom; right/middle-drag to pan. Esc cancels.",
            ForeColor = Color.FromArgb(205, 215, 225),
            AutoEllipsis = true
        };
        hint.SetBounds(20, 38, Math.Max(180, monitorBounds.Width - 250), 22);
        _zoomOutButton.Text = "−";
        _zoomOutButton.SetBounds(Math.Max(20, monitorBounds.Width - 210), 25, 34, 30);
        _zoomOutButton.Click += (_, _) => SetZoom(_zoom / 1.25f, ViewportCenter());
        _zoomLabel.SetBounds(Math.Max(56, monitorBounds.Width - 172), 25, 55, TrackerTheme.ControlHeight);
        _zoomLabel.ForeColor = Color.White;
        _zoomLabel.TextAlign = ContentAlignment.MiddleCenter;
        _zoomInButton.Text = "+";
        _zoomInButton.SetBounds(Math.Max(112, monitorBounds.Width - 112), 25, 34, 30);
        _zoomInButton.Click += (_, _) => SetZoom(_zoom * 1.25f, ViewportCenter());
        _guideButton.Text = "Guide";
        _guideButton.SetBounds(Math.Max(152, monitorBounds.Width - 70), 25, 60, 30);
        _guideButton.Click += (_, _) => ShowGuide();
        _zoomLabel.Text = $"{_zoom:P0}";
        _toolbar.Controls.AddRange([_instructions, hint, _zoomOutButton, _zoomLabel, _zoomInButton, _guideButton]);
        if (initialSelections is not null)
        {
            _instructions.Text = "Include all three full borders and bottom lines. Select a box or legend to adjust it.";
            hint.Text = "Drag boxes/corners to adjust · Scroll to zoom · Right/middle-drag to pan · Esc cancels";
            hint.Top = 78;
            string[] names = ["Grenade", "Melee", "Class ability"];
            for (var index = 0; index < names.Length; index++)
            {
                var selectionIndex = index;
                var legend = new Button
                {
                    Text = names[index],
                    ForeColor = _selectionColors[index],
                    BackColor = BackColor,
                    FlatStyle = FlatStyle.Flat
                };
                legend.SetBounds(18 + index * 138, 40, 130, 28);
                legend.FlatAppearance.BorderColor = _selectionColors[index];
                legend.Click += (_, _) =>
                {
                    _activeSelection = selectionIndex;
                    UpdateLegend();
                    Invalidate();
                };
                _legendButtons.Add(legend);
                _toolbar.Controls.Add(legend);
            }
        }

        _footer.BackColor = Color.FromArgb(14, 17, 24);
        _footer.Dock = DockStyle.Bottom;
        _footer.Height = 52;
        _footer.Padding = new Padding(18, 8, 18, 8);
        var footerHint = new Label
        {
            Text = initialSelections is null
                ? "Include the full square border; the strip below it is sampled automatically."
                : string.Empty,
            ForeColor = Color.FromArgb(205, 215, 225),
            AutoEllipsis = true
        };
        footerHint.SetBounds(18, 15, Math.Max(180, monitorBounds.Width - 250), 22);
        _applyButton.Text = initialSelections is null ? "Use selection" : "Use all three";
        _applyButton.Enabled = false;
        _applyButton.SetBounds(Math.Max(20, monitorBounds.Width - 216), 9, 112, 32);
        _applyButton.Click += (_, _) => ApplySelection();
        var cancelButton = new Button { Text = "Cancel" };
        cancelButton.SetBounds(Math.Max(140, monitorBounds.Width - 94), 9, 76, 32);
        cancelButton.Click += (_, _) => CancelSelection();
        _footer.Controls.AddRange([footerHint, _applyButton, cancelButton]);
        Controls.AddRange([_toolbar, _footer]);
        _selectionApplyButton.Text = "Use selection";
        _selectionApplyButton.Size = new Size(112, 32);
        _selectionApplyButton.Cursor = Cursors.Hand;
        _selectionApplyButton.Visible = false;
        _selectionApplyButton.Click += (_, _) => ApplySelection();
        Controls.Add(_selectionApplyButton);
        foreach (var button in new[] { _zoomOutButton, _zoomInButton, _guideButton, _applyButton, cancelButton, _selectionApplyButton })
        {
            button.ForeColor = TrackerTheme.Text;
            button.BackColor = TrackerTheme.Surface;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = TrackerTheme.Border;
            button.UseVisualStyleBackColor = false;
            button.Height = TrackerTheme.ControlHeight;
        }
        TrackerTheme.Primary(_applyButton);
        UpdateLegend();

        _scale = FitScale();
        var initial = initialSelections ?? (initialSelection is { } single ? [single] : Array.Empty<Rectangle>());
        for (var index = 0; index < initial.Length; index++)
        {
            var existing = initial[index];
            var clipped = Rectangle.Intersect(existing, sourceBounds);
            if (clipped.Width >= 18 && clipped.Height >= 18)
            {
                _selections[index] = new RectangleF(
                    clipped.Left - sourceBounds.Left,
                    clipped.Top - sourceBounds.Top,
                    clipped.Width,
                    clipped.Height);
            }
        }
        _applyButton.Enabled = ValidSelections();
        if (initialSelections is not null && _selections.All(selection => selection is not null))
        {
            ZoomToSelections();
        }

        MouseWheel += (_, e) => SetZoom(_zoom * (e.Delta > 0 ? 1.2f : 1f / 1.2f), e.Location);
        KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Escape)
            {
                CancelSelection();
            }
            else if (e.KeyCode is Keys.Add or Keys.Oemplus)
            {
                SetZoom(_zoom * 1.25f, ViewportCenter());
            }
            else if (e.KeyCode is Keys.Subtract or Keys.OemMinus)
            {
                SetZoom(_zoom / 1.25f, ViewportCenter());
            }
        };
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        SetWindowPos(Handle, HwndTopmost, Left, Top, Width, Height, SwpShowWindow);
        SetForegroundWindow(Handle);
        Activate();
        Focus();
        ClipCursor(IntPtr.Zero);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var destination = ImageDestination();
        UpdateSelectionApplyButton();
        using var oldClip = e.Graphics.Clip;
        e.Graphics.SetClip(_viewport);
        e.Graphics.DrawImage(_snapshot, destination);
        if (_selections.Any(selection => selection is not null))
        {
            using var shade = new SolidBrush(Color.FromArgb(145, 0, 0, 0));
            using var outside = new Region(_viewport);
            foreach (var selection in _selections)
            {
                if (selection is { } area) outside.Exclude(SourceToScreen(area));
            }

            e.Graphics.FillRegion(shade, outside);
        }
        for (var index = 0; index < _selections.Length; index++)
        {
            if (_selections[index] is not { } selected) continue;
            var selectionBounds = SourceToScreen(selected);
            var color = _selectionColors[index];
            using var outline = new Pen(color, index == _activeSelection ? 3 : 2);
            using var handleBrush = new SolidBrush(color);
            e.Graphics.DrawRectangle(outline, selectionBounds.X, selectionBounds.Y, selectionBounds.Width, selectionBounds.Height);
            if (index == _activeSelection)
            {
                foreach (var corner in SelectionCorners(selectionBounds))
                {
                    e.Graphics.FillEllipse(handleBrush, corner.X - 6, corner.Y - 6, 12, 12);
                }
            }
        }

        e.Graphics.Clip = oldClip;
    }

    private void UpdateLegend()
    {
        for (var index = 0; index < _legendButtons.Count; index++)
        {
            var button = _legendButtons[index];
            button.Height = TrackerTheme.ControlHeight;
            button.FlatAppearance.BorderSize = index == _activeSelection ? 2 : 1;
            button.BackColor = index == _activeSelection ? TrackerTheme.Surface : BackColor;
        }
    }

    private void UpdateSelectionApplyButton()
    {
        if (_selections.Length > 1 || _selection is not { } selected || !IsValidSelection(selected) ||
            _draggingSelection || _movingSelection || _resizingSelection || _panning)
        {
            _selectionApplyButton.Visible = false;
            return;
        }

        var visibleSelection = RectangleF.Intersect(SourceToScreen(selected), _viewport);
        if (visibleSelection.Width <= 0 || visibleSelection.Height <= 0)
        {
            _selectionApplyButton.Visible = false;
            return;
        }

        _selectionApplyButton.Location = SelectionSubmitLocation(visibleSelection, _viewport, _selectionApplyButton.Size);
        _selectionApplyButton.Visible = true;
    }

    internal static Point SelectionSubmitLocation(RectangleF selection, RectangleF viewport, Size buttonSize)
    {
        const int gap = 14;
        var x = selection.Right + gap;
        if (x + buttonSize.Width > viewport.Right)
        {
            x = selection.Left - gap - buttonSize.Width;
        }

        var y = selection.Top;
        if (x < viewport.Left)
        {
            x = selection.Left;
            y = selection.Bottom + gap;
            if (y + buttonSize.Height > viewport.Bottom)
            {
                y = selection.Top - gap - buttonSize.Height;
            }
        }

        return new Point(
            (int)Math.Clamp(x, viewport.Left, Math.Max(viewport.Left, viewport.Right - buttonSize.Width)),
            (int)Math.Clamp(y, viewport.Top, Math.Max(viewport.Top, viewport.Bottom - buttonSize.Height)));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!_viewport.Contains(e.Location))
        {
            return;
        }

        if (e.Button is MouseButtons.Right or MouseButtons.Middle)
        {
            _panning = true;
            _lastSource = new PointF(e.X, e.Y);
            Cursor = Cursors.Hand;
            return;
        }

        if (e.Button != MouseButtons.Left)
        {
            return;
        }

        _startSource = ClampSource(ScreenToSource(e.Location));
        _lastSource = _startSource;
        if (_selections.Length > 1 &&
            (_selection is not { } active || HitSelectionCorner(active, e.Location) < 0))
        {
            for (var index = 0; index < _selections.Length; index++)
            {
                if (_selections[index] is { } area && area.Contains(_startSource))
                {
                    _activeSelection = index;
                    UpdateLegend();
                    break;
                }
            }
        }
        if (_selection is { } selection)
        {
            var corner = HitSelectionCorner(selection, e.Location);
            if (corner >= 0)
            {
                _resizeAnchor = corner switch
                {
                    0 => new PointF(selection.Right, selection.Bottom),
                    1 => new PointF(selection.Left, selection.Bottom),
                    2 => new PointF(selection.Left, selection.Top),
                    _ => new PointF(selection.Right, selection.Top)
                };
                _resizingSelection = true;
            }
            else if (selection.Contains(_startSource))
            {
                _movingSelection = true;
            }
            else
            {
                _selection = null;
                _draggingSelection = true;
                _selection = RectangleF.FromLTRB(_startSource.X, _startSource.Y, _startSource.X, _startSource.Y);
            }
        }
        else
        {
            _draggingSelection = true;
            _selection = RectangleF.FromLTRB(_startSource.X, _startSource.Y, _startSource.X, _startSource.Y);
        }

        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_panning)
        {
            var deltaX = (e.X - _lastSource.X) / _scale;
            var deltaY = (e.Y - _lastSource.Y) / _scale;
            _sourceCenter = ClampCenter(new PointF(_sourceCenter.X - deltaX, _sourceCenter.Y - deltaY));
            _lastSource = e.Location;
            Invalidate();
            return;
        }

        var sourcePoint = ClampSource(ScreenToSource(e.Location));
        if (_draggingSelection)
        {
            _selection = RectangleF.FromLTRB(
                Math.Min(_startSource.X, sourcePoint.X),
                Math.Min(_startSource.Y, sourcePoint.Y),
                Math.Max(_startSource.X, sourcePoint.X),
                Math.Max(_startSource.Y, sourcePoint.Y));
            _applyButton.Enabled = ValidSelections();
            Invalidate();
        }
        else if (_movingSelection && _selection is { } moving)
        {
            var delta = new PointF(sourcePoint.X - _lastSource.X, sourcePoint.Y - _lastSource.Y);
            _selection = ClampSelection(new RectangleF(moving.X + delta.X, moving.Y + delta.Y, moving.Width, moving.Height));
            _lastSource = sourcePoint;
            Invalidate();
        }
        else if (_resizingSelection && _selection is not null)
        {
            _selection = RectangleF.FromLTRB(
                Math.Min(_resizeAnchor.X, sourcePoint.X),
                Math.Min(_resizeAnchor.Y, sourcePoint.Y),
                Math.Max(_resizeAnchor.X, sourcePoint.X),
                Math.Max(_resizeAnchor.Y, sourcePoint.Y));
            _applyButton.Enabled = ValidSelections();
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button is MouseButtons.Right or MouseButtons.Middle)
        {
            _panning = false;
            Cursor = Cursors.Cross;
            Invalidate();
        }

        if (e.Button == MouseButtons.Left)
        {
            _draggingSelection = false;
            _movingSelection = false;
            _resizingSelection = false;
            if (_selection is { } selection && !IsValidSelection(selection))
            {
                _selection = null;
            }

            _applyButton.Enabled = ValidSelections();
            Invalidate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _snapshot.Dispose();
        }

        base.Dispose(disposing);
    }

    private void ApplySelection()
    {
        if (!ValidSelections())
        {
            return;
        }

        SelectedRegions = _selections.Select(selection =>
        {
            var pixels = ToPixelBounds(selection!.Value);
            pixels.Offset(_sourceBounds.Location);
            return Rectangle.Intersect(pixels, _sourceBounds);
        }).ToArray();
        SelectedRegion = SelectedRegions[0];
        DialogResult = DialogResult.OK;
        Close();
    }

    private void CancelSelection()
    {
        DialogResult = DialogResult.Cancel;
        Close();
    }

    private void ShowGuide()
    {
        using var guide = new SelectionGuide();
        guide.ShowDialog(this);
    }

    internal RectangleF SelectionClusterOnScreen =>
        SourceToScreen(_selections.Where(selection => selection is not null).Select(selection => selection!.Value).Aggregate(RectangleF.Union));

    private void ZoomToSelections()
    {
        var cluster = _selections.Select(selection => selection!.Value).Aggregate(RectangleF.Union);
        _sourceCenter = new PointF(cluster.Left + cluster.Width / 2, cluster.Top + cluster.Height / 2);
        _zoom = Math.Clamp(Math.Min(_viewport.Width / (cluster.Width * 1.5f),
            _viewport.Height / (cluster.Height * 2)) / FitScale(), 1, 32);
        _scale = FitScale() * _zoom;
        _zoomLabel.Text = $"{_zoom:P0}";
    }

    private RectangleF ImageDestination()
    {
        _scale = FitScale() * _zoom;
        var width = _snapshot.Width * _scale;
        var height = _snapshot.Height * _scale;
        return new RectangleF(
            _viewport.Left + _viewport.Width / 2 - _sourceCenter.X * _scale,
            _viewport.Top + _viewport.Height / 2 - _sourceCenter.Y * _scale,
            width,
            height);
    }

    private RectangleF SourceToScreen(RectangleF source) => new(
        _viewport.Left + _viewport.Width / 2 + (source.Left - _sourceCenter.X) * _scale,
        _viewport.Top + _viewport.Height / 2 + (source.Top - _sourceCenter.Y) * _scale,
        source.Width * _scale,
        source.Height * _scale);

    private PointF ScreenToSource(Point point) => new(
        _sourceCenter.X + (point.X - (_viewport.Left + _viewport.Width / 2)) / _scale,
        _sourceCenter.Y + (point.Y - (_viewport.Top + _viewport.Height / 2)) / _scale);

    private PointF ClampSource(PointF point) => new(
        Math.Clamp(point.X, 0, _snapshot.Width),
        Math.Clamp(point.Y, 0, _snapshot.Height));

    private PointF ViewportCenter() => new(_viewport.Left + _viewport.Width / 2, _viewport.Top + _viewport.Height / 2);

    private float FitScale() => Math.Min(_viewport.Width / _snapshot.Width, _viewport.Height / _snapshot.Height);

    private void SetZoom(float zoom, PointF screenAnchor)
    {
        var nextZoom = Math.Clamp(zoom, 0.5f, 32f);
        if (Math.Abs(nextZoom - _zoom) < 0.001f)
        {
            return;
        }

        var sourceAnchor = ScreenToSource(Point.Round(screenAnchor));
        _zoom = nextZoom;
        _scale = FitScale() * _zoom;
        _sourceCenter = new PointF(
            sourceAnchor.X - (screenAnchor.X - (_viewport.Left + _viewport.Width / 2)) / _scale,
            sourceAnchor.Y - (screenAnchor.Y - (_viewport.Top + _viewport.Height / 2)) / _scale);
        _sourceCenter = ClampCenter(_sourceCenter);
        _zoomLabel.Text = $"{_zoom:P0}";
        Invalidate();
    }

    private PointF ClampCenter(PointF center)
    {
        var visibleWidth = _viewport.Width / _scale;
        var visibleHeight = _viewport.Height / _scale;
        return new PointF(
            _snapshot.Width <= visibleWidth
                ? _snapshot.Width / 2f
                : Math.Clamp(center.X, visibleWidth / 2, _snapshot.Width - visibleWidth / 2),
            _snapshot.Height <= visibleHeight
                ? _snapshot.Height / 2f
                : Math.Clamp(center.Y, visibleHeight / 2, _snapshot.Height - visibleHeight / 2));
    }

    private RectangleF ClampSelection(RectangleF selection)
    {
        var x = Math.Clamp(selection.X, 0, _snapshot.Width - selection.Width);
        var y = Math.Clamp(selection.Y, 0, _snapshot.Height - selection.Height);
        return new RectangleF(x, y, selection.Width, selection.Height);
    }

    private int HitSelectionCorner(RectangleF selection, Point point)
    {
        var bounds = SourceToScreen(selection);
        var corners = SelectionCorners(bounds);
        for (var index = 0; index < corners.Length; index++)
        {
            if (Math.Abs(point.X - corners[index].X) <= 12 && Math.Abs(point.Y - corners[index].Y) <= 12)
            {
                return index;
            }
        }

        return -1;
    }

    private static PointF[] SelectionCorners(RectangleF bounds) =>
    [
        new(bounds.Left, bounds.Top),
        new(bounds.Right, bounds.Top),
        new(bounds.Right, bounds.Bottom),
        new(bounds.Left, bounds.Bottom)
    ];

    private bool IsValidSelection(RectangleF selection) => selection.Width >= 18 && selection.Height >= 18;

    private static Rectangle ToPixelBounds(RectangleF selection) => Rectangle.FromLTRB(
        (int)Math.Floor(selection.Left), (int)Math.Floor(selection.Top),
        (int)Math.Ceiling(selection.Right), (int)Math.Ceiling(selection.Bottom));

    private bool ValidSelections()
    {
        if (_selections.Any(selection => selection is not { } area || !IsValidSelection(area))) return false;
        for (var index = 0; index < _selections.Length; index++)
        {
            for (var other = index + 1; other < _selections.Length; other++)
            {
                if (ToPixelBounds(_selections[index]!.Value).IntersectsWith(ToPixelBounds(_selections[other]!.Value))) return false;
            }
        }
        return true;
    }

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpShowWindow = 0x0040;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr rect);
}

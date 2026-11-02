namespace D2AbilityTracker;

internal sealed class PathLinkLabel : LinkLabel
{
    private string _fullPath = string.Empty;

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public string FullPath
    {
        get => _fullPath;
        set
        {
            _fullPath = value;
            AccessibleName = value;
            UpdateDisplay();
        }
    }

    public PathLinkLabel()
    {
        AutoSize = false;
        UseMnemonic = false;
        TextAlign = ContentAlignment.MiddleLeft;
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        UpdateDisplay();
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdateDisplay();
    }

    private void UpdateDisplay() =>
        Text = CompactPath(_fullPath ?? string.Empty, Font, Math.Max(0, ClientSize.Width - Padding.Horizontal));

    internal static string CompactPath(string path, Font font, int width)
    {
        bool Fits(string text) => TextRenderer.MeasureText(text, font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width <= width;

        if (Fits(path)) return path;
        const string ellipsis = "…";
        if (!Fits(ellipsis)) return string.Empty;

        var filename = Path.GetFileName(path);
        var directory = path[..(path.Length - filename.Length)];
        while (directory.Length > 0)
        {
            directory = directory.TrimEnd('\\');
            var separator = directory.LastIndexOf('\\');
            directory = separator >= 0 ? directory[..(separator + 1)] : string.Empty;
            var compact = directory + ellipsis + "\\" + filename;
            if (Fits(compact)) return compact;
        }
        if (Fits(filename)) return filename;

        var extension = Path.GetExtension(filename);
        var stem = filename[..(filename.Length - extension.Length)];
        for (var length = stem.Length - 1; length >= 0; length--)
        {
            var compact = stem[..length] + ellipsis + extension;
            if (Fits(compact)) return compact;
        }
        for (var length = extension.Length - 1; length >= 0; length--)
        {
            var compact = ellipsis + extension[^length..];
            if (Fits(compact)) return compact;
        }
        return ellipsis;
    }
}

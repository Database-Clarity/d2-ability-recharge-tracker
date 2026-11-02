namespace D2AbilityTracker;

internal sealed class ChangelogView : UserControl
{
    public event EventHandler? CloseRequested;

    public ChangelogView(string summary, IReadOnlyList<ChangelogRelease> releases)
    {
        Name = "ChangelogView";
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        Dock = DockStyle.Fill;

        var heading = new Label
        {
            Text = summary,
            AutoSize = true,
            ForeColor = TrackerTheme.Muted,
            Margin = new Padding(0, 0, 0, 8)
        };
        var content = new RichTextBox
        {
            ReadOnly = true,
            HideSelection = true,
            DetectUrls = true,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.None,
            BackColor = TrackerTheme.Background,
            ForeColor = TrackerTheme.Text,
            Font = new Font("Segoe UI", 9),
            Dock = DockStyle.Fill,
            Name = "ReleaseNotes"
        };
        void RenderNotes(bool includeHistory)
        {
            content.Clear();
            var firstRelease = true;
            foreach (var release in releases)
            {
                // Blank lines in the Markdown source are ignored; spacing is emitted explicitly with short spacer
                // lines so headings, bullets and releases keep consistent, compact gaps.
                if (!firstRelease) Spacer(content, 12);
                firstRelease = false;
                Append(content, release.Tag + "\n", 14, FontStyle.Bold);
                var body = string.IsNullOrWhiteSpace(release.Body) ? "No release notes were provided." : release.Body;
                var firstLine = true;
                foreach (var rawLine in body.ReplaceLineEndings("\n").Split('\n'))
                {
                    var line = rawLine.Trim();
                    if (line.Length == 0) continue;
                    if (!includeHistory && IsHistoryLine(line)) break;
                    var isHeading = line.StartsWith('#');
                    if (isHeading) line = line.TrimStart('#', ' ');
                    if (line.StartsWith("- ") || line.StartsWith("* ")) line = "•  " + line[2..];
                    if (isHeading || firstLine) Spacer(content, isHeading ? 6 : 3);
                    else if (!line.StartsWith('•')) Spacer(content, 3);
                    AppendMarkdown(content, line + "\n", isHeading);
                    if (isHeading) Spacer(content, 2);
                    firstLine = false;
                }
            }
            content.SelectionStart = 0;
            content.SelectionLength = 0;
            content.ScrollToCaret();
        }
        RenderNotes(includeHistory: false);
        var bodyPanel = new Panel { Dock = DockStyle.Fill, Padding = new Padding(4, 8, 4, 8) };
        bodyPanel.Controls.Add(content);
        content.LinkClicked += (_, args) =>
        {
            if (!Uri.TryCreate(args.LinkText, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("https" or "http")) return;
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch (System.ComponentModel.Win32Exception exception)
            {
                MessageBox.Show(this, $"Couldn't open the release link: {exception.Message}", "Changelog",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        var closeButton = new Button
        {
            Text = "Back",
            AutoSize = true,
            MinimumSize = new Size(88, 28),
            Anchor = AnchorStyles.Right
        };
        closeButton.Click += (_, _) => CloseRequested?.Invoke(this, EventArgs.Empty);
        Load += (_, _) => closeButton.Select();

        var footer = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0)
        };
        footer.Controls.Add(closeButton);
        if (releases.Any(release => release.Body.ReplaceLineEndings("\n").Split('\n').Any(IsHistoryLine)))
        {
            var historyToggle = new Button
            {
                Text = "Show commit history",
                AutoSize = true,
                Name = "ToggleCommitHistory"
            };
            var expanded = false;
            historyToggle.Click += (_, _) =>
            {
                expanded = !expanded;
                RenderNotes(expanded);
                historyToggle.Text = expanded ? "Hide commit history" : "Show commit history";
            };
            footer.Controls.Add(historyToggle);
        }

        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(12),
            ColumnCount = 1,
            RowCount = 3
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(heading, 0, 0);
        layout.Controls.Add(bodyPanel, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        TrackerTheme.Apply(this);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape) return base.ProcessCmdKey(ref msg, keyData);
        CloseRequested?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static bool IsHistoryLine(string line)
    {
        var text = line.Trim().TrimStart('#', ' ');
        return text.Equals("Commit history", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Full comparison]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("[Full changelog]", StringComparison.OrdinalIgnoreCase) ||
            text.StartsWith("**Full Changelog**:", StringComparison.OrdinalIgnoreCase);
    }

    private static void AppendMarkdown(RichTextBox content, string line, bool heading)
    {
        line = System.Text.RegularExpressions.Regex.Replace(line,
            @"\[([^\]]+)\]\((https?://[^)\s]+)\)", "$1: $2");
        var parts = System.Text.RegularExpressions.Regex.Split(line, @"(\*\*[^*]+\*\*|`[^`]+`)");
        foreach (var part in parts)
        {
            var bold = part.StartsWith("**") && part.EndsWith("**") && part.Length > 4;
            var code = part.StartsWith('`') && part.EndsWith('`') && part.Length > 2;
            Append(content, bold ? part[2..^2] : code ? part[1..^1] : part,
                9, heading || bold ? FontStyle.Bold : FontStyle.Regular, code, heading);
        }
    }

    private static void Spacer(RichTextBox content, float size) => Append(content, "\n", size, FontStyle.Regular);

    private static void Append(RichTextBox content, string text, float size, FontStyle style, bool code = false, bool muted = false)
    {
        content.SelectionStart = content.TextLength;
        using var font = new Font(code ? "Consolas" : "Segoe UI", size, style);
        content.SelectionFont = font;
        content.SelectionColor = muted ? TrackerTheme.Muted : TrackerTheme.Text;
        content.AppendText(text);
    }
}

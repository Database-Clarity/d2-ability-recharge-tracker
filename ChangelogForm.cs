namespace D2AbilityTracker;

internal sealed class ChangelogForm : Form
{
    public ChangelogForm(string title, string summary, IReadOnlyList<ChangelogRelease> releases)
    {
        Text = title;
        BackColor = TrackerTheme.Background;
        ForeColor = TrackerTheme.Text;
        Font = new Font("Segoe UI", 9);
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        MinimumSize = new Size(480, 360);
        Size = new Size(700, 540);
        Icon = AppIcon.Load();

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
            DetectUrls = false,
            WordWrap = true,
            ScrollBars = RichTextBoxScrollBars.Vertical,
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = TrackerTheme.Surface,
            ForeColor = TrackerTheme.Text,
            Font = new Font("Segoe UI", 9),
            Dock = DockStyle.Fill,
            Text = FormatReleases(releases)
        };
        var closeButton = new Button
        {
            Text = "Close",
            DialogResult = DialogResult.OK,
            AutoSize = true,
            MinimumSize = new Size(88, 28),
            Anchor = AnchorStyles.Right
        };
        TrackerTheme.Primary(closeButton);

        var footer = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 8, 0, 0)
        };
        footer.Controls.Add(closeButton);

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
        layout.Controls.Add(content, 0, 1);
        layout.Controls.Add(footer, 0, 2);
        Controls.Add(layout);

        AcceptButton = closeButton;
        CancelButton = closeButton;
    }

    private static string FormatReleases(IReadOnlyList<ChangelogRelease> releases)
    {
        return string.Join(
            $"{Environment.NewLine}{Environment.NewLine}{new string('─', 48)}{Environment.NewLine}{Environment.NewLine}",
            releases.Select(release =>
                $"{release.Tag}{Environment.NewLine}{Environment.NewLine}" +
                (string.IsNullOrWhiteSpace(release.Body) ? "No release notes were provided." : release.Body.Trim())));
    }
}

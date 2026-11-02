using System.Drawing;

namespace D2AbilityTracker;

// Fallback display choice for area selection when Destiny 2's window isn't found.
internal sealed partial class MainForm
{
    private sealed class DisplayPickerDialog : AppWindow
    {
        private readonly FlatComboBox _displays = new();

        public DisplayPickerDialog(DisplayChoice[] choices, string? preferredDeviceName)
        {
            Text = "Choose a display";
            StartPosition = FormStartPosition.CenterParent;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            BackColor = TrackerTheme.Background;
            ForeColor = TrackerTheme.Text;
            Font = new Font("Segoe UI", 9);
            AutoScaleDimensions = new SizeF(96, 96);
            AutoScaleMode = AutoScaleMode.Dpi;

            var message = new Label
            {
                Text = "Destiny 2's window wasn't found. Pick the display it's on to select the area from a frozen preview.",
                AutoSize = false,
                Size = new Size(360, 36),
                Margin = new Padding(3, 0, 3, 6)
            };
            _displays.DropDownStyle = ComboBoxStyle.DropDownList;
            _displays.Width = 360;
            _displays.Margin = new Padding(3, 3, 3, 10);
            _displays.AccessibleName = "Display";
            _displays.Items.AddRange(choices);
            var preferred = Array.FindIndex(choices, choice =>
                string.Equals(choice.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase));
            var primary = Array.FindIndex(choices, choice => choice.Label.EndsWith("primary", StringComparison.Ordinal));
            _displays.SelectedIndex = choices.Length == 0 ? -1 : preferred >= 0 ? preferred : Math.Max(0, primary);

            var ok = new Button { Text = "Use display", DialogResult = DialogResult.OK, AutoSize = true, Margin = new Padding(6, 0, 0, 0) };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true, Margin = Padding.Empty };
            ok.Enabled = choices.Length > 0;
            var buttons = new FlowLayoutPanel
            {
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Anchor = AnchorStyles.Right,
                Margin = new Padding(3, 0, 3, 0),
                WrapContents = false
            };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);

            var layout = new TableLayoutPanel
            {
                AutoSize = true,
                AutoSizeMode = AutoSizeMode.GrowAndShrink,
                ColumnCount = 1,
                Location = new Point(12, CaptionHeight + 4),
                Padding = new Padding(0, 0, 0, 12)
            };
            layout.Controls.Add(message);
            layout.Controls.Add(_displays);
            layout.Controls.Add(buttons);
            Controls.Add(layout);
            TrackerTheme.Apply(layout);
            TrackerTheme.Primary(ok);
            TrackerTheme.Secondary(cancel);
            AcceptButton = ok;
            CancelButton = cancel;
            var preferredSize = layout.GetPreferredSize(Size.Empty);
            ClientSize = new Size(preferredSize.Width + 24, layout.Top + preferredSize.Height);
        }

        public DisplayChoice? SelectedDisplay => _displays.SelectedItem as DisplayChoice;
    }
}

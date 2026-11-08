using D2AbilityTracker;

/// <summary>Logs the window messages a live text control and its parent receive while its Text changes.</summary>
internal static class LabelProbe
{
    private sealed class Spy : NativeWindow
    {
        public readonly List<int> Messages = [];
        public bool Recording;
        public Spy(Control control) => AssignHandle(control.Handle);
        protected override void WndProc(ref Message m)
        {
            if (Recording) Messages.Add(m.Msg);
            base.WndProc(ref m);
        }
    }

    public static List<string> Run(Func<Control> create)
    {
        var results = new List<string>();
        var thread = new Thread(() =>
        {
            using var form = new Form
            {
                ShowInTaskbar = false,
                StartPosition = FormStartPosition.Manual,
                Location = HeadlessUi.OffscreenLocation,
                Size = new Size(400, 200),
                ShowIcon = false
            };
            var control = create();
            control.SetBounds(10, 10, 300, 44);
            form.Controls.Add(control);
            form.Show();
            Application.DoEvents();
            var paints = 0;
            control.Paint += (_, _) => paints++;
            var self = new Spy(control);
            var parent = new Spy(form);
            self.Recording = parent.Recording = true;
            control.Text = "Recharging · 35.14 s (00:35.14) · 43%\nEstimated 81.20 ± 1.80 s";
            self.Recording = parent.Recording = false;
            results.Add($"{control.GetType().Name} during Text set: paints {paints}, dropped {(control as LiveLabel)?.DroppedDirectDraws}, self [{string.Join(",", self.Messages.Select(m => $"0x{m:X}"))}] parent [{string.Join(",", parent.Messages.Select(m => $"0x{m:X}"))}]");
            self.ReleaseHandle();
            parent.ReleaseHandle();
            form.Close();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        return results;
    }
}

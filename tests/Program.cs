using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing;
using System.Diagnostics;
using D2AbilityTracker;

var start = DateTime.UnixEpoch;
var ability = AbilityKind.Grenade;
var useBinding = new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.Control | InputModifiers.Shift);
var session = new RepeatTestSession();
session.Start(ability, 2, TimeSpan.FromSeconds(1), useBinding);
Assert(session.Phase == RepeatTestPhase.AwaitingInitialUse, "Test should wait for the user to begin the first recharge.");

session.RecordRecharge(ability, TimeSpan.FromSeconds(20), start);
Assert(session.CompletedCycles == 0, "A recharge must not count before the initial ability use.");
session.ObserveState(ability, AbilityState.Recharging, start.AddSeconds(1));
session.RecordRecharge(ability, TimeSpan.FromSeconds(10), start.AddSeconds(11));
Assert(session.Phase == RepeatTestPhase.WaitingForNextUse, "The first recharge should schedule another use.");
Assert(!session.TryGetDueUse(start.AddSeconds(12), false, out _), "The test must wait while Destiny 2 is not foreground.");
Assert(session.TryGetDueUse(start.AddSeconds(12), true, out var scheduledBinding), "The due input should be sent while Destiny 2 is foreground.");
Assert(scheduledBinding == useBinding, "The configured key and modifiers should be preserved.");

session.ObserveState(ability, AbilityState.Recharging, start.AddSeconds(13));
Assert(session.Phase == RepeatTestPhase.WaitingForRecharge, "The next cooldown should follow the automatic ability input.");
session.RecordRecharge(ability, TimeSpan.FromSeconds(12), start.AddSeconds(25));
Assert(session.Phase == RepeatTestPhase.Completed, "The test should finish at the requested cycle count.");
Assert(session.AverageDuration == TimeSpan.FromSeconds(11), "The final result should average all measured recharges.");
Assert(session.Status(start.AddSeconds(25)).Contains("average 00:11.0", StringComparison.Ordinal), "The summary should show the mean duration.");

var stopped = new RepeatTestSession();
stopped.Start(ability, 3, TimeSpan.Zero, useBinding);
stopped.Stop();
Assert(stopped.Phase == RepeatTestPhase.Stopped, "Stopping should terminate an active repeat test.");

var missedUse = new RepeatTestSession();
missedUse.Start(ability, 2, TimeSpan.Zero, useBinding);
missedUse.ObserveState(ability, AbilityState.Recharging, start);
missedUse.RecordRecharge(ability, TimeSpan.FromSeconds(10), start.AddSeconds(10));
Assert(missedUse.TryGetDueUse(start.AddSeconds(10), true, out _), "The configured reuse input should be scheduled.");
missedUse.ObserveState(ability, AbilityState.Ready, start.AddSeconds(19));
Assert(missedUse.Phase == RepeatTestPhase.Failed, "A missing cooldown after automatic use should fail clearly.");

var tracker = new AbilitySlotTracker(ability);
var empty = new AbilitySignal(false, false, 0, 0);
var line = new AbilitySignal(false, true, 0, 0.21f);
var charged = new AbilitySignal(true, false, 0.8f, 0);
tracker.Observe(charged, start);
var flash = new AbilitySignal(false, false, 0, 0, 0.04f);
tracker.Observe(flash, start.AddSeconds(1));
Assert(tracker.State == AbilityState.Recharging, "One bottom-border flash should start timing immediately.");
tracker.Observe(line, start.AddSeconds(1).AddMilliseconds(33));
tracker.Observe(empty, start.AddSeconds(4));
Assert(tracker.Observe(charged, start.AddSeconds(4).AddMilliseconds(33)), "The ready fill should confirm the recharge end.");
Assert(tracker.LastDuration == TimeSpan.FromSeconds(3), "Duration should end at the first blank-frame timestamp.");
Assert(tracker.IsCharged, "The HUD charged state should be available for the automatic-use safety guard.");
tracker.Observe(empty, start.AddSeconds(5));
Assert(!tracker.IsCharged, "The charged-state guard should clear when the HUD fill disappears.");

var wrongLineTracker = new AbilitySlotTracker(ability);
wrongLineTracker.Observe(charged, start);
wrongLineTracker.Observe(line, start.AddSeconds(1));
wrongLineTracker.Observe(line, start.AddSeconds(2));
Assert(wrongLineTracker.State != AbilityState.Recharging,
    "A line inside the square must not start timing without the bottom-border flash.");
var earlyFlashTracker = new AbilitySlotTracker(ability);
earlyFlashTracker.Observe(charged, start);
earlyFlashTracker.Observe(flash with { Charged = true }, start.AddSeconds(1));
earlyFlashTracker.Observe(charged, start.AddSeconds(1).AddMilliseconds(10));
Assert(earlyFlashTracker.LastDuration is null, "Ready fill immediately after a start flash is not a completed cooldown.");
var dropoutTracker = new AbilitySlotTracker(AbilityKind.Melee);
dropoutTracker.Observe(charged, start);
dropoutTracker.Observe(flash, start.AddSeconds(1));
dropoutTracker.Observe(line, start.AddSeconds(2));
dropoutTracker.Observe(empty, start.AddSeconds(3));
dropoutTracker.Observe(line, start.AddSeconds(4));
dropoutTracker.Observe(empty, start.AddSeconds(7));
Assert(dropoutTracker.Observe(charged, start.AddSeconds(7).AddMilliseconds(10)) &&
    dropoutTracker.LastDuration == TimeSpan.FromSeconds(6),
    "A temporary internal-line dropout must not replace the later blank completion endpoint.");

foreach (var size in new[] { 40, 80, 160 })
{
    var normalBorder = ReadSyntheticSlot(size, false);
    var thickBorder = ReadSyntheticSlot(size, true);
    var interiorLine = ReadSyntheticSlot(size, false, true);
    Assert(interiorLine.BottomBorderThickness == normalBorder.BottomBorderThickness,
        "An interior horizontal line must not count toward bottom-border thickness.");
    Assert(thickBorder.BottomBorderThickness > normalBorder.BottomBorderThickness + 0.012f,
        $"The bottom-border thickening must be detected at {size}px, including pixels below the selected outline.");
    var pixelTracker = new AbilitySlotTracker(ability);
    pixelTracker.Observe(normalBorder with { Charged = true }, start);
    pixelTracker.Observe(thickBorder, start.AddSeconds(1));
    Assert(pixelTracker.State == AbilityState.Recharging, "Pixel-based bottom flash should start recharge.");
}

Assert(CaptureSchedule.NextFrame(0, 10, 120, 1000) == 40, "Slow capture must get three times its work duration as rest.");
Assert(CaptureSchedule.NextFrame(100, 101, 120, 1000) == 108, "Fast captures should respect the configured target.");
Assert(CaptureSchedule.NextFrame(100, 150, 240, 1000) == 300, "Overruns must not cause a burst of catch-up captures.");

var repeatShortcut = new InputBinding(InputDevice.Keyboard, 0x79, InputModifiers.Alt);
var settings = new TrackerSettings
{
    PauseBinding = useBinding,
    AbilityUseBinding = new InputBinding(InputDevice.Mouse, MouseInputCode.X2, InputModifiers.Shift),
    RepeatTestBinding = repeatShortcut,
    SelectionBindings = new() { [AbilityKind.Melee] = useBinding },
    CaptureRate = 60
};
var restoredSettings = JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(settings));
Assert(restoredSettings?.PauseBinding == useBinding, "Modified pause bindings should round-trip in settings.");
Assert(restoredSettings?.AbilityUseBinding == settings.AbilityUseBinding, "Modified ability-use bindings should round-trip in settings.");
Assert(restoredSettings?.RepeatTestBinding == repeatShortcut && restoredSettings.SelectionBindings[AbilityKind.Melee] == useBinding,
    "Task shortcuts should round-trip in settings.");
Assert(restoredSettings?.CaptureRate == 60, "The configured capture target should persist.");
Assert(useBinding.ToString() == "Ctrl+Shift+G", "Key names and modifiers should be presented clearly.");
var mouseBinding = new InputBinding(InputDevice.Mouse, MouseInputCode.X1, InputModifiers.Alt);
Assert(mouseBinding.ToString() == "Alt+Mouse X1", "Mouse buttons should retain their modifier and readable label.");
Assert(Marshal.SizeOf<InputBindingSender.InputEvent>() == (IntPtr.Size == 8 ? 40 : 28), "SendInput structure must match the Windows ABI.");

var viewport = new RectangleF(0, 86, 1080, 900);
var submitSize = new Size(112, 32);
Assert(RegionPicker.SelectionSubmitLocation(new RectangleF(100, 200, 50, 50), viewport, submitSize) == new Point(164, 200),
    "Submit should sit beside the selection with clearance for its resize handles.");
Assert(RegionPicker.SelectionSubmitLocation(new RectangleF(1000, 200, 50, 50), viewport, submitSize) == new Point(874, 200),
    "Submit should switch to the left near the viewport's right edge.");
Assert(RegionPicker.SelectionSubmitLocation(new RectangleF(0, 900, 1080, 86), viewport, submitSize) == new Point(0, 854),
    "Submit should fit above a wide selection at the bottom edge.");
Assert(RegionPicker.SelectionSubmitLocation(viewport, viewport, submitSize) == new Point(0, 86),
    "Submit must stay accessible even when the selection fills the viewport.");

using (var preview = new Bitmap(40, 40))
using (var picker = new RegionPicker(preview, new Rectangle(0, 0, 40, 40), new Rectangle(0, 0, 1080, 900), "test preview"))
{
    AssertReadableButtons(picker);
}

using (var listener = new GlobalInputListener())
using (var hookThreadResponded = new ManualResetEventSlim())
{
    listener.Start();
    var dispatcher = typeof(GlobalInputListener)
        .GetField("_hookDispatcher", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
        ?.GetValue(listener) as System.Windows.Forms.Control
        ?? throw new InvalidOperationException("The global hooks need a dedicated message dispatcher.");
    dispatcher.BeginInvoke((Action)(() => hookThreadResponded.Set()));
    Assert(hookThreadResponded.Wait(TimeSpan.FromSeconds(2)),
        "The hook message pump must keep running while the calling/UI thread is blocked.");
}

Console.WriteLine("All bottom-flash detection, capture scheduling, selection, repeat-test, and input-hook checks passed.");

if (args.Contains("--capture-benchmark"))
{
    using var desktop = ScreenCapture.OpenDesktop();
    using var surface = new ScreenCaptureSurface(400, 170, desktop.DeviceContext);
    var clock = Stopwatch.StartNew();
    var captures = 0;
    long workTicks = 0;
    var nextFrame = Stopwatch.GetTimestamp();
    while (clock.Elapsed < TimeSpan.FromSeconds(2))
    {
        var now = Stopwatch.GetTimestamp();
        if (now < nextFrame)
        {
            Thread.Sleep(Math.Max(1, (int)Math.Ceiling((nextFrame - now) * 1000d / Stopwatch.Frequency)));
            continue;
        }

        var started = Stopwatch.GetTimestamp();
        surface.Capture(desktop.DeviceContext, new Rectangle(0, 0, 400, 170));
        AbilitySignalDetector.Read(surface.Pixels, surface.Stride, 0, 0, 60, 60, 6);
        var finished = Stopwatch.GetTimestamp();
        workTicks += finished - started;
        nextFrame = CaptureSchedule.NextFrame(started, finished, 120, Stopwatch.Frequency);
        captures++;
    }

    Console.WriteLine($"Load-limited 400x170 capture: {captures / clock.Elapsed.TotalSeconds:F1} Hz, " +
        $"{workTicks / (double)Stopwatch.Frequency / clock.Elapsed.TotalSeconds:P1} capture duty.");
}

static AbilitySignal ReadSyntheticSlot(int size, bool thickBorder, bool interiorLine = false)
{
    var padding = AbilitySignalDetector.BottomPadding(size);
    var data = new byte[size * (size + padding) * 4];
    var borderRows = Math.Max(1, size / 40);
    for (var y = size - borderRows; y < size + (thickBorder ? borderRows : 0); y++)
    {
        for (var x = 0; x < size; x++)
        {
            var offset = (y * size + x) * 4;
            data[offset] = data[offset + 1] = data[offset + 2] = 255;
        }

    }

    if (interiorLine)
    {
        for (var x = 0; x < size; x++)
        {
            var offset = ((int)(size * 0.88) * size + x) * 4;
            data[offset] = data[offset + 1] = data[offset + 2] = 255;
        }
    }

    var buffer = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, buffer, data.Length);
        return AbilitySignalDetector.Read(buffer, size * 4, 0, 0, size, size, padding);
    }
    finally
    {
        Marshal.FreeHGlobal(buffer);
    }
}

static void AssertReadableButtons(System.Windows.Forms.Control control)
{
    foreach (System.Windows.Forms.Control child in control.Controls)
    {
        if (child is System.Windows.Forms.Button button)
        {
            Assert(button.ForeColor == Color.White && button.BackColor.GetBrightness() < 0.3f,
                $"Selector button '{button.Text}' should have explicit light-on-dark text.");
        }

        AssertReadableButtons(child);
    }
}

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

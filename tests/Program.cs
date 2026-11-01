using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing;
using D2AbilityTracker;

var start = DateTime.UnixEpoch;
var ability = AbilityKind.Grenade;
var useBinding = new InputBinding(InputDevice.Keyboard, 0x47, InputModifiers.Control | InputModifiers.Shift);
var session = new RepeatTestSession();
session.Start(ability, 2, TimeSpan.FromSeconds(1), useBinding);
Assert(session.Phase == RepeatTestPhase.AwaitingInitialUse, "Test should wait for the user to begin the first recharge.");

session.RecordRecharge(ability, TimeSpan.FromSeconds(20), start);
Assert(session.CompletedCycles == 0, "A recharge must not count before the initial ability use.");
session.ObserveState(ability, AbilityState.Consumed, start.AddSeconds(1));
session.RecordRecharge(ability, TimeSpan.FromSeconds(10), start.AddSeconds(11));
Assert(session.Phase == RepeatTestPhase.WaitingForNextUse, "The first recharge should schedule another use.");
Assert(!session.TryGetDueUse(start.AddSeconds(12), false, out _), "The test must wait while Destiny 2 is not foreground.");
Assert(session.TryGetDueUse(start.AddSeconds(12), true, out var scheduledBinding), "The due input should be sent while Destiny 2 is foreground.");
Assert(scheduledBinding == useBinding, "The configured key and modifiers should be preserved.");

session.ObserveState(ability, AbilityState.Consumed, start.AddSeconds(13));
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
missedUse.ObserveState(ability, AbilityState.Consumed, start);
missedUse.RecordRecharge(ability, TimeSpan.FromSeconds(10), start.AddSeconds(10));
Assert(missedUse.TryGetDueUse(start.AddSeconds(10), true, out _), "The configured reuse input should be scheduled.");
missedUse.ObserveState(ability, AbilityState.Ready, start.AddSeconds(19));
Assert(missedUse.Phase == RepeatTestPhase.Failed, "A missing cooldown after automatic use should fail clearly.");

var tracker = new AbilitySlotTracker(ability);
var empty = new AbilitySignal(false, false, 0, 0);
var line = new AbilitySignal(false, true, 0, 0.21f);
var charged = new AbilitySignal(true, false, 0.8f, 0);
tracker.Observe(empty, start);
tracker.Observe(line, start.AddSeconds(1));
tracker.Observe(line, start.AddSeconds(1).AddMilliseconds(33));
tracker.Observe(empty, start.AddSeconds(4));
Assert(tracker.Observe(charged, start.AddSeconds(4).AddMilliseconds(33)), "The ready fill should confirm the recharge end.");
Assert(tracker.LastDuration == TimeSpan.FromSeconds(3), "Duration should end at the first blank-frame timestamp.");
Assert(tracker.IsCharged, "The HUD charged state should be available for the automatic-use safety guard.");
tracker.Observe(empty, start.AddSeconds(5));
Assert(!tracker.IsCharged, "The charged-state guard should clear when the HUD fill disappears.");

var settings = new TrackerSettings { PauseBinding = useBinding, AbilityUseBinding = useBinding };
var restoredSettings = JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(settings));
Assert(restoredSettings?.PauseBinding == useBinding, "Modified pause bindings should round-trip in settings.");
Assert(restoredSettings?.AbilityUseBinding == useBinding, "Modified ability-use bindings should round-trip in settings.");
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

Console.WriteLine("All recharge, repeat-test, input-binding, and native-input layout checks passed.");

static void Assert(bool condition, string message)
{
    if (!condition)
    {
        throw new InvalidOperationException(message);
    }
}

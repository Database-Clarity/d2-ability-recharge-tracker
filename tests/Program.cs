using System.Runtime.InteropServices;
using System.Text.Json;
using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using D2AbilityTracker;

if (args.Length > 0 && args[0] == "--replay")
{
    FootageReplay.Run(args);
    return;
}

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
Assert(session.Status(start.AddSeconds(25)).Contains("average 11.00 s (00:11.00)", StringComparison.Ordinal), "The summary should show the mean in seconds and clock format.");
Assert(DurationDisplay.Format(TimeSpan.FromMilliseconds(73456)) == "73.46 s (01:13.46)",
    "Recharge durations should include total seconds rounded to two decimals.");
Assert(DurationDisplay.Format(null) == "--.-- s (--:--.--)", "Missing durations should show placeholders for both formats.");
Assert(DurationDisplay.Format(TimeSpan.FromMilliseconds(59999)) == "60.00 s (01:00.00)", "Both formats should agree when rounding crosses a minute.");

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
    Assert(pixelTracker.State != AbilityState.Recharging,
        "Border thickening alone in pixel footage must not mistake a bright scene edge for ability use.");
    var progressSignal = ReadSyntheticProgress(size, 0.6f);
    Assert(progressSignal.ChargeProgress is { } observed && Math.Abs(observed - 0.6f) < 0.08f,
        "Matching side-strip boundaries should expose resolution-scaled charge progress.");
}

var resilient = new AbilitySlotTracker(AbilityKind.ClassAbility);
resilient.Observe(charged, start);
resilient.Observe(flash, start.AddSeconds(1));
resilient.Observe(line with { ChargeProgress = 0.3f }, start.AddSeconds(4));
resilient.Observe(line with { ChargeProgress = 0.4f }, start.AddSeconds(5));
resilient.Observe(empty, start.AddSeconds(6));
Assert(resilient.TrackingWarning is not null && resilient.CurrentDuration == TimeSpan.FromSeconds(5),
    "Losing contrast mid-charge must keep the original timer and expose recovery status.");
Assert(!resilient.Observe(charged, start.AddSeconds(6).AddMilliseconds(10)) && resilient.MeasurementDiscarded,
    "A charged-looking frame during recovery must not manufacture an endpoint.");

var recovered = new AbilitySlotTracker(AbilityKind.Melee);
recovered.Observe(charged, start);
recovered.Observe(flash, start.AddSeconds(1));
recovered.Observe(line with { ChargeProgress = 0.3f }, start.AddSeconds(4));
recovered.Observe(line with { ChargeProgress = 0.4f }, start.AddSeconds(5));
recovered.Observe(empty, start.AddSeconds(6));
recovered.Observe(line with { ChargeProgress = 0.65f }, start.AddSeconds(8));
Assert(recovered.TrackingWarning is null && recovered.CurrentDuration == TimeSpan.FromSeconds(7),
    "Recovered forward progress must resume the same measurement without resetting its start.");
recovered.Observe(line with { ChargeProgress = 0.95f }, start.AddSeconds(10));
recovered.Observe(empty, start.AddSeconds(10).AddMilliseconds(100));
Assert(recovered.Observe(charged, start.AddSeconds(10).AddMilliseconds(150)) &&
    recovered.LastDuration == TimeSpan.FromSeconds(9.1),
    "A visible endpoint after recovery must retain the original start timestamp.");
var calibrated = new AbilitySlotTracker(ability);
var greenReady = charged with { FillHue = 127.5f, FillColor = Color.FromArgb(60, 160, 80), PixelEvidence = true };
calibrated.Observe(greenReady, start);
calibrated.Observe(greenReady, start.AddMilliseconds(200));
calibrated.Observe(greenReady with { BottomBorderThickness = 0.2f }, start.AddMilliseconds(300));
Assert(calibrated.State == AbilityState.Ready,
    "A bright scene edge under an already charged pixel slot must not start another cycle.");
calibrated.Observe(empty with { UseFlash = true, PixelEvidence = true }, start.AddSeconds(1));
Assert(calibrated.State == AbilityState.Consumed && calibrated.CurrentDuration is null,
    "The use animation must arm start detection without including its animation delay in recharge timing.");
calibrated.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.01f },
    start.AddSeconds(1).AddMilliseconds(100));
calibrated.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.04f },
    start.AddSeconds(1).AddMilliseconds(500));
Assert(calibrated.State == AbilityState.Recharging && calibrated.CurrentDuration == TimeSpan.Zero,
    "The later consumed-slot border pulse must establish the actual recharge start.");
calibrated.Observe(line with { ChargeProgress = 0.4f, PixelEvidence = true }, start.AddSeconds(4));
calibrated.Observe(greenReady with { FillHue = 247.5f, FillColor = Color.FromArgb(80, 80, 180) }, start.AddSeconds(5));
Assert(!calibrated.IsCharged && calibrated.State == AbilityState.Recharging && !calibrated.MeasurementDiscarded,
    "A saturated scene of another color must not become the slot's charged fill or discard its timer.");
Assert(calibrated.ProgressSearchTolerance(start.AddSeconds(8)) > 0.12f,
    "Recovery must expand the boundary search using elapsed time and observed charge rate.");
calibrated.Observe(line with { ChargeProgress = 0.95f, PixelEvidence = true }, start.AddSeconds(10));
calibrated.Observe(greenReady, start.AddSeconds(10).AddMilliseconds(100));
Assert(!calibrated.IsCharged, "Automatic ability use must wait for confirmed ready fill.");
Assert(calibrated.Observe(greenReady, start.AddSeconds(10).AddMilliseconds(300)) &&
    calibrated.LastDuration == TimeSpan.FromSeconds(8.6),
    "Debouncing the returning color must preserve its first endpoint timestamp.");
var contrastEndpoint = new AbilitySlotTracker(ability);
contrastEndpoint.Observe(greenReady, start);
contrastEndpoint.Observe(empty with { UseFlash = true, PixelEvidence = true }, start.AddSeconds(1));
contrastEndpoint.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.01f }, start.AddSeconds(1.1));
contrastEndpoint.Observe(empty with { Blank = true, PixelEvidence = true, BottomBorderThickness = 0.04f }, start.AddSeconds(1.5));
contrastEndpoint.Observe(line with { PixelEvidence = true, ChargeProgress = 0.8f, FillLuminance = 0.5f }, start.AddSeconds(8));
contrastEndpoint.Observe(line with { PixelEvidence = true, ChargeProgress = 0.95f, FillLuminance = 0.5f }, start.AddSeconds(10));
contrastEndpoint.Observe(empty with { PixelEvidence = true, FillLuminance = 0.25f }, start.AddSeconds(10.1));
Assert(contrastEndpoint.LastDuration is null, "A luminance drop alone must not complete a measurement.");
contrastEndpoint.Observe(greenReady, start.AddSeconds(10.2));
Assert(contrastEndpoint.Observe(greenReady, start.AddSeconds(10.4)) &&
    contrastEndpoint.LastDuration is { } contrastDuration && Math.Abs(contrastDuration.TotalSeconds - 8.6) < 0.000001,
    "A relative fill disappearance on a bright scene must preserve the blank endpoint, confirmed by ready color.");
Assert(MainForm.MapRegion(new Rectangle(100, 100, 40, 40), new Rectangle(0, 0, 1000, 1000),
    new Rectangle(200, 300, 500, 500)) == new Rectangle(250, 350, 20, 20),
    "Window-relative regions should follow movement and resizing.");

var model = new RechargeModel();
var noise = new Random(7);
for (var sample = 0; sample < 200; sample++)
{
    var seconds = sample * 0.05;
    var outlier = sample % 23 == 5 ? 0.3 : 0;
    model.Add(seconds, 0.01 + seconds / 50 + (noise.NextDouble() - 0.5) * 0.004 + outlier);
}
Assert(model.IsReady && model.SecondsAt(1) is { } modelEnd && Math.Abs(modelEnd - 49.5) < 0.6 &&
    model.SecondsUncertaintyAt(1) is { } modelSpread && modelSpread > 0 && modelSpread < 2 &&
    Math.Abs(modelEnd - 49.5) < 3 * modelSpread + 0.05,
    "The robust model should reject outliers and predict the end within its stated uncertainty.");
model.Reset();
Assert(!model.IsReady && model.SecondsAt(1) is null, "Resetting the model should clear the prediction.");
for (var sample = 0; sample < 40; sample++)
{
    model.Add(sample * 0.1, 0.1);
}
Assert(model.SecondsAt(1) is null, "Stationary progress must not produce a finish estimate.");
var estimatingTracker = new AbilitySlotTracker(ability);
estimatingTracker.Observe(charged, start);
estimatingTracker.Observe(flash, start.AddSeconds(1));
for (var sample = 1; sample <= 40; sample++)
{
    estimatingTracker.Observe(line with { ChargeProgress = sample * 0.01f },
        start.AddSeconds(1 + sample * 0.1));
}
Assert(estimatingTracker.EstimatedDuration is { } trackerEstimate && Math.Abs(trackerEstimate.TotalSeconds - 10) < 0.1 &&
    estimatingTracker.EstimateUncertainty is { } trackerSpread && trackerSpread < TimeSpan.FromSeconds(1),
    "The active tracker should expose its fitted estimate with an uncertainty.");
estimatingTracker.MarkSignalLost(start.AddSeconds(6));
Assert(estimatingTracker.EstimatedDuration is not null && estimatingTracker.CurrentDuration == TimeSpan.FromSeconds(5),
    "Signal loss should retain elapsed time and the whole-cycle estimate for fast recovery.");
Assert(estimatingTracker.Observe(line with { ChargeProgress = 0.55f }, start.AddSeconds(6.5)) == false &&
    estimatingTracker.TrackingWarning is null,
    "A single consistent progress sample should end recovery.");
Assert(CaptureSchedule.NextFrame(0, 10, 120, 1000) == 40, "Slow capture must get three times its work duration as rest.");
Assert(CaptureSchedule.NextFrame(100, 101, 120, 1000) == 108, "Fast captures should respect the configured target.");
Assert(CaptureSchedule.NextFrame(100, 150, 240, 1000) == 300, "Overruns must not cause a burst of catch-up captures.");

var repeatShortcut = new InputBinding(InputDevice.Keyboard, 0x79, InputModifiers.Alt);
var settings = new TrackerSettings
{
    PauseBinding = useBinding,
    AbilityUseBinding = new InputBinding(InputDevice.Mouse, MouseInputCode.X2, InputModifiers.Shift),
    RepeatTestBinding = repeatShortcut,
    SoloBindings = new() { [AbilityKind.Melee] = useBinding },
    SoloAbility = AbilityKind.Melee,
    OverlayShortcutCleared = true,
    CaptureRate = 60
};
var restoredSettings = JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(settings));
Assert(restoredSettings?.PauseBinding == useBinding, "Modified pause bindings should round-trip in settings.");
Assert(restoredSettings?.AbilityUseBinding == settings.AbilityUseBinding, "Modified ability-use bindings should round-trip in settings.");
Assert(restoredSettings?.RepeatTestBinding == repeatShortcut && restoredSettings.SoloBindings[AbilityKind.Melee] == useBinding &&
    restoredSettings.SoloAbility == AbilityKind.Melee && restoredSettings.OverlayShortcutCleared,
    "Task shortcuts should round-trip in settings.");
Assert(restoredSettings?.CaptureRate == 60, "The configured capture target should persist.");
Assert(useBinding.ToString() == "Ctrl+Shift+G", "Key names and modifiers should be presented clearly.");
var mouseBinding = new InputBinding(InputDevice.Mouse, MouseInputCode.X1, InputModifiers.Alt);
Assert(mouseBinding.ToString() == "Alt+Mouse X1", "Mouse buttons should retain their modifier and readable label.");
Assert(new InputBinding(InputDevice.Keyboard, (int)Keys.Escape, InputModifiers.None).IsClearRequest &&
    !new InputBinding(InputDevice.Keyboard, (int)Keys.Escape, InputModifiers.Shift).IsClearRequest,
    "Plain Escape should clear a shortcut while recording it.");
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

using (var guide = new SelectionGuide())
using (var illustration = new Bitmap(guide.ClientSize.Width, guide.ClientSize.Height))
{
    guide.DrawToBitmap(illustration, guide.ClientRectangle);
    Assert(illustration.GetPixel(100, 100) != illustration.GetPixel(300, 100),
        "The guide should render an illustrated slot against its background.");
    AssertReadableButtons(guide);
}

using (var hudPreview = new Bitmap(581, 182))
using (var graphics = Graphics.FromImage(hudPreview))
{
    graphics.Clear(Color.FromArgb(25, 24, 32));
    using var pink = new SolidBrush(Color.FromArgb(244, 115, 170));
    for (var x = 110; x < 570; x++)
    {
        var y = (int)Math.Round(75 - (x - 110) * 0.055);
        graphics.FillRectangle(pink, x, y - 2, 1, 5);
    }

    using var outline = new Pen(Color.FromArgb(220, 220, 225), 1.5f);
    for (var index = 0; index < 3; index++)
    {
        var left = 110 + index * 67;
        var top = 83 - index * 4;
        var side = 61;
        var corners = new[]
        {
            new PointF(left, top), new PointF(left + side, top - side * 0.055f),
            new PointF(left + side, top + side - side * 0.055f), new PointF(left, top + side)
        };
        graphics.DrawPolygon(outline, corners);
    }

    var window = new Rectangle(500, 300, hudPreview.Width, hudPreview.Height);
    var suggestions = AbilitySlotRegionFinder.Find(hudPreview, window);
    Assert(suggestions.Length == 3, "The pink HUD anchor and outlined ability squares should produce three regions.");
    var expected = new[]
    {
        new Rectangle(610, 380, 61, 65), new Rectangle(677, 376, 61, 65), new Rectangle(744, 372, 61, 65)
    };
    for (var index = 0; index < expected.Length; index++)
    {
        Assert(Math.Abs(suggestions[index].Left - expected[index].Left) <= 3 &&
            Math.Abs(suggestions[index].Top - expected[index].Top) <= 3 &&
            Math.Abs(suggestions[index].Width - expected[index].Width) <= 3 &&
            Math.Abs(suggestions[index].Height - expected[index].Height) <= 3,
            $"Synthetic slot {index} should be refined to its visible border; got {suggestions[index]}.");
    }
}

var recordingDisplayBounds = new Rectangle(100, 50, 1000, 800);
var recordingSlots = new[]
{
        new AbilitySlotSettings
        {
            Kind = AbilityKind.Grenade,
            Region = new Rectangle(300, 200, 60, 60),
            MonitorBounds = recordingDisplayBounds,
            MonitorDeviceName = "DISPLAY1"
        },
        new AbilitySlotSettings
        {
            Kind = AbilityKind.Melee,
            Region = new Rectangle(370, 195, 60, 60),
            MonitorBounds = recordingDisplayBounds,
            MonitorDeviceName = "DISPLAY1"
        },
        new AbilitySlotSettings
        {
            Kind = AbilityKind.ClassAbility,
            Region = new Rectangle(440, 190, 60, 60),
            MonitorBounds = recordingDisplayBounds,
            MonitorDeviceName = "DISPLAY1"
        }
    };
Assert(AbilityHudRecorder.GetCaptureBounds(recordingSlots) == new Rectangle(284, 174, 232, 102),
    "The debug recording should capture the tracked ability areas with a small surrounding margin.");
Assert(AbilityHudRecorder.GetCaptureBounds(recordingSlots.Where(slot => slot.Kind == AbilityKind.Melee)) == new Rectangle(354, 179, 92, 92),
    "A solo recording should capture only the tracked ability area.");
var recordingPath = Path.Combine(Path.GetTempPath(), $"ability-hud-test-{Guid.NewGuid():N}.zip");
using (var recorder = new AbilityHudRecorder(new Rectangle(0, 0, 8, 8), "DISPLAY1", recordingPath))
{
    var magenta = Enumerable.Repeat(unchecked((int)0xFFFF00FF), 64).ToArray();
    var recordingStart = DateTime.UtcNow;
    var handle = System.Runtime.InteropServices.GCHandle.Alloc(magenta, System.Runtime.InteropServices.GCHandleType.Pinned);
    try
    {
        for (var frame = 0; frame < 6; frame++)
        {
            // 120 Hz capture ticks must be thinned to the 60 FPS recording rate.
            var timestamp = recordingStart.AddSeconds(frame / 120d);
            if (recorder.IsDue(timestamp))
            {
                recorder.Submit(handle.AddrOfPinnedObject(), 32, timestamp, "Ready,,,,,");
            }
        }
    }
    finally
    {
        handle.Free();
    }

    recorder.Stop();
    var recordingResult = recorder.Completion.GetAwaiter().GetResult();
    Assert(recordingResult.Frames == 3 && recordingResult.DroppedFrames == 0 && File.Exists(recordingPath),
        "A stopped recording should keep 60 FPS frames from the capture loop and produce a complete archive.");
}
try
{
    using var recordingArchive = ZipFile.OpenRead(recordingPath);
    var firstFrame = recordingArchive.GetEntry("frames/000001.png");
    Assert(firstFrame is not null &&
        recordingArchive.GetEntry("frames.csv") is not null &&
        recordingArchive.GetEntry("recording.txt") is not null,
        "A recording archive should include its lossless frame, timing index, and metadata.");
    using var frameStream = firstFrame!.Open();
    using var decodedFrame = new Bitmap(frameStream);
    Assert(decodedFrame.GetPixel(0, 0).ToArgb() == Color.Magenta.ToArgb(),
        "The archived PNG frame should preserve the captured HUD pixels losslessly.");
}
finally
{
    File.Delete(recordingPath);
}

const string releaseJson = """
{
  "tag_name": "v0.1.12",
  "draft": false,
  "prerelease": false,
  "html_url": "https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/tag/v0.1.12",
  "assets": [
    { "name": "notes.txt", "browser_download_url": "https://example.com/notes.txt", "size": 4 },
    {
      "name": "D2AbilityTracker-v0.1.12-Setup.exe",
      "browser_download_url": "https://github.com/Database-Clarity/d2-ability-recharge-tracker/releases/download/v0.1.12/D2AbilityTracker-v0.1.12-Setup.exe",
      "size": 1234,
      "digest": "sha256:ABCDEF"
    }
  ]
}
""";
var newerRelease = UpdateChecker.ParseLatestRelease(releaseJson, new Version(0, 1, 9));
Assert(newerRelease is { Tag: "v0.1.12", AssetName: "D2AbilityTracker-v0.1.12-Setup.exe", Size: 1234, Sha256: "abcdef" } &&
    newerRelease.Version == new Version(0, 1, 12),
    "The updater should pick the setup asset and digest from a newer GitHub release.");
Assert(UpdateChecker.ParseLatestRelease(releaseJson, new Version(0, 1, 12)) is null &&
    UpdateChecker.ParseLatestRelease(releaseJson, new Version(0, 2, 0)) is null,
    "The updater should ignore releases that are not newer than the running build.");
Assert(UpdateChecker.ParseLatestRelease(releaseJson.Replace("\"prerelease\": false", "\"prerelease\": true"), new Version(0, 1, 0)) is null &&
    UpdateChecker.ParseLatestRelease(releaseJson.Replace("D2AbilityTracker-v0.1.12-Setup.exe\",", "Other.exe\","), new Version(0, 1, 0)) is null,
    "The updater should skip prereleases and releases without the expected installer.");
Assert(UpdateChecker.TryParseTag("v0.1.10", out var parsedTag) && parsedTag > new Version(0, 1, 9) &&
    !UpdateChecker.TryParseTag("latest", out _),
    "Release tags should compare numerically rather than as text.");

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

var autoDetectFixtures = new[]
{
    "hud-2-5.png", "hud-30.png", "hud-60.png", "hud-100.png", "hud-140.png", "hud-163.png"
};
var expectedAbilityRegions = new[]
{
    new Rectangle(110, 82, 60, 63),
    new Rectangle(176, 77, 60, 63),
    new Rectangle(242, 73, 61, 63)
};
foreach (var fixtureName in autoDetectFixtures)
{
    var fixturePath = Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName);
    using var fixture = new Bitmap(fixturePath);
    var detected = AbilitySlotRegionFinder.Find(fixture, new Rectangle(0, 0, fixture.Width, fixture.Height));
    Console.WriteLine($"{fixtureName}: {string.Join("; ", detected)}");
    Assert(detected.Length == expectedAbilityRegions.Length,
        $"Auto-detection should find three square borders in {fixtureName}; found {detected.Length}.");
    for (var index = 0; index < expectedAbilityRegions.Length; index++)
    {
        var expectedRegion = expectedAbilityRegions[index];
        var foundRegion = detected[index];
        Assert(Math.Abs(foundRegion.Left - expectedRegion.Left) <= 3 &&
            Math.Abs(foundRegion.Top - expectedRegion.Top) <= 3 &&
            Math.Abs(foundRegion.Width - expectedRegion.Width) <= 3 &&
            Math.Abs(foundRegion.Height - expectedRegion.Height) <= 3,
            $"Auto-detection of {fixtureName} slot {index} should match the visible border within 3 px; expected {expectedRegion}, found {foundRegion}.");
    }
}

using (var yellowFixture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-solar-yellow.png")))
{
    var detected = AbilitySlotRegionFinder.Find(
        yellowFixture, new Rectangle(0, 0, yellowFixture.Width, yellowFixture.Height));
    Console.WriteLine($"hud-solar-yellow.png: {string.Join("; ", detected)}");
    Assert(detected.Length == 3, "Auto-detection should find the three slots below the yellow subclass strip.");
    var expected = new[]
    {
        new Rectangle(123, 72, 60, 64),
        new Rectangle(189, 67, 60, 64),
        new Rectangle(255, 63, 59, 63)
    };
    for (var index = 0; index < expected.Length; index++)
    {
        var found = detected[index];
        var region = expected[index];
        Assert(Math.Abs(found.Left - region.Left) <= 3 &&
            Math.Abs(found.Top - region.Top) <= 3 &&
            Math.Abs(found.Width - region.Width) <= 3 &&
            Math.Abs(found.Height - region.Height) <= 3,
            $"Yellow-strip slot {index} should match the visible border within 3 px; expected {region}, found {found}.");
    }
}

using (var pickerFixture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png")))
{
    var expected = new[]
    {
        new Rectangle(136, 131, 77, 80),
        new Rectangle(219, 125, 77, 80),
        new Rectangle(302, 119, 77, 80)
    };
    foreach (var fullFrame in new[] { false, true })
    {
        using var frame = new Bitmap(fullFrame ? 1920 : pickerFixture.Width, fullFrame ? 1080 : pickerFixture.Height);
        var offset = fullFrame ? new Point(40, 750) : Point.Empty;
        using (var graphics = Graphics.FromImage(frame))
        {
            graphics.Clear(Color.FromArgb(18, 19, 24));
            graphics.DrawImageUnscaled(pickerFixture, offset);
        }

        var source = new Rectangle(500, 300, frame.Width, frame.Height);
        var detected = AbilitySlotRegionFinder.Find(frame, source);
        Console.WriteLine($"Picker screenshot (full frame: {fullFrame}): {string.Join("; ", detected)}");
        Assert(detected.Length == 3, "The dim HUD screenshot must find all three abilities, not the weapon panel.");
        for (var index = 0; index < expected.Length; index++)
        {
            var region = expected[index];
            region.Offset(source.X + offset.X, source.Y + offset.Y);
            var found = detected[index];
            Assert(Math.Abs(found.X - region.X) <= 3 && Math.Abs(found.Y - region.Y) <= 3 &&
                Math.Abs(found.Width - region.Width) <= 3 && Math.Abs(found.Height - region.Height) <= 3,
                $"Picker screenshot slot {index} must cover its ability border; expected {region}, found {found}.");
        }
    }
}

using (var withoutStrips = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png")))
{
    using (var graphics = Graphics.FromImage(withoutStrips))
    {
        graphics.FillPolygon(Brushes.Black,
            [new Point(0, 0), new Point(749, 0), new Point(749, 84), new Point(0, 136)]);
    }

    for (var y = 0; y < withoutStrips.Height; y++)
    {
        for (var x = 0; x < withoutStrips.Width; x++)
        {
            var pixel = withoutStrips.GetPixel(x, y);
            var light = (pixel.R * 77 + pixel.G * 150 + pixel.B * 29) >> 8;
            withoutStrips.SetPixel(x, y, Color.FromArgb(light, light, light));
        }
    }

    var detected = AbilitySlotRegionFinder.Find(withoutStrips,
        new Rectangle(0, 0, withoutStrips.Width, withoutStrips.Height));
    Assert(detected.Length == 3, "The three-slot pattern must be detected without colored strips or element colors.");
    Assert(Math.Abs(detected[0].X - 136) <= 3 && Math.Abs(detected[1].X - 219) <= 3 &&
        Math.Abs(detected[2].X - 302) <= 3,
        "Removing strip and element colors must not move detection onto the weapon panel.");
}

using (var pickerFixture = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-dim-picker.png")))
using (var weaponsOnly = pickerFixture.Clone(new Rectangle(385, 0, 364, 296), pickerFixture.PixelFormat))
{
    var detected = AbilitySlotRegionFinder.Find(weaponsOnly, new Rectangle(0, 0, weaponsOnly.Width, weaponsOnly.Height));
    Assert(detected.Length == 0, "The weapon/ammo panel and colored strips alone must not count as an ability row.");
}

foreach (var slotCount in new[] { 0, 1, 2, 3 })
{
    using var pattern = new Bitmap(500, 220);
    using (var graphics = Graphics.FromImage(pattern))
    using (var border = new Pen(Color.LightGray, 2))
    {
        graphics.Clear(Color.FromArgb(25, 24, 32));
        for (var index = 0; index < slotCount; index++)
        {
            var left = 60 + index * 75;
            var top = 90 - index * 5;
            graphics.DrawPolygon(border, [
                new Point(left, top), new Point(left + 65, top - 4),
                new Point(left + 65, top + 61), new Point(left, top + 65)
            ]);
        }
    }

    var detected = AbilitySlotRegionFinder.Find(pattern, new Rectangle(0, 0, pattern.Width, pattern.Height));
    Assert(detected.Length == (slotCount == 3 ? 3 : 0),
        $"A complete three-slot pattern is required; {slotCount} visible slots produced {detected.Length} regions.");
}

var scaleFixturePaths = new[] { "hud-2-5.png" };
var resolutionScales = new[] { ("720p", 2f / 3), ("1080p", 1f), ("1440p", 4f / 3), ("4K", 2f) };
foreach (var fixtureName in scaleFixturePaths)
{
    using var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", fixtureName));
    foreach (var (resolution, scale) in resolutionScales)
    {
        var scaledWidth = (int)Math.Round(original.Width * scale);
        var scaledHeight = (int)Math.Round(original.Height * scale);
        using var scaled = new Bitmap(scaledWidth, scaledHeight);
        using (var graphics = Graphics.FromImage(scaled))
        {
            graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            graphics.DrawImage(original, new Rectangle(0, 0, scaledWidth, scaledHeight));
        }

        var detected = AbilitySlotRegionFinder.Find(scaled, new Rectangle(0, 0, scaled.Width, scaled.Height));
        Assert(detected.Length == expectedAbilityRegions.Length,
            $"Auto-detection should find all slot borders in {fixtureName} scaled for {resolution}.");
        for (var index = 0; index < expectedAbilityRegions.Length; index++)
        {
            var baseRegion = expectedAbilityRegions[index];
            var expectedRegion = new Rectangle(
                (int)Math.Round(baseRegion.X * scale), (int)Math.Round(baseRegion.Y * scale),
                (int)Math.Round(baseRegion.Width * scale), (int)Math.Round(baseRegion.Height * scale));
            var foundRegion = detected[index];
            const int tolerance = 3;
            var error = Math.Max(
                Math.Max(Math.Abs(foundRegion.Left - expectedRegion.Left), Math.Abs(foundRegion.Top - expectedRegion.Top)),
                Math.Max(Math.Abs(foundRegion.Width - expectedRegion.Width), Math.Abs(foundRegion.Height - expectedRegion.Height)));
            Assert(error <= tolerance,
                $"{resolution} scaling should preserve slot {index} within {tolerance} px; expected {expectedRegion}, found {foundRegion}.");
            Console.WriteLine($"{fixtureName} at {resolution}: slot {index} max error {error} px.");
        }
    }
}

using (var performanceFrame = new Bitmap(1920, 1080))
using (var original = new Bitmap(Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-2-5.png")))
using (var graphics = Graphics.FromImage(performanceFrame))
{
    graphics.DrawImageUnscaled(original, 100, 400);
    var stopwatch = Stopwatch.StartNew();
    var detected = AbilitySlotRegionFinder.Find(performanceFrame, new Rectangle(0, 0, 1920, 1080));
    stopwatch.Stop();
    Assert(detected.Length == expectedAbilityRegions.Length, "The 1920×1080 detection timing check should find all three slots.");
    Assert(stopwatch.Elapsed.TotalMilliseconds < 300,
        $"Ability slot detection on a 1920×1080 frame should finish under 300 ms; took {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
    Console.WriteLine($"Ability slot detection on a 1920×1080 frame: {stopwatch.Elapsed.TotalMilliseconds:F1} ms.");
}

Console.WriteLine("All bottom-flash detection, capture scheduling, selection, repeat-test, and input-hook checks passed.");

if (args.Contains("--update-smoke"))
{
    Task.Run(async () =>
    {
        var latest = await UpdateChecker.CheckAsync(CancellationToken.None);
        Console.WriteLine(latest is null ? "Running build is current." : $"Latest update: {latest.Tag} ({latest.AssetName})");
        using var smokeClient = new HttpClient();
        smokeClient.DefaultRequestHeaders.UserAgent.ParseAdd("D2AbilityTracker-tests");
        var smokeRelease = UpdateChecker.ParseLatestRelease(
            await smokeClient.GetStringAsync($"https://api.github.com/repos/{UpdateChecker.Repository}/releases/latest"),
            new Version(0, 0, 0)) ?? throw new InvalidOperationException("The latest release has no installer asset.");
        var downloaded = await UpdateChecker.DownloadAsync(smokeRelease, null, CancellationToken.None);
        Console.WriteLine($"Downloaded and verified {new FileInfo(downloaded).Length} bytes to {downloaded}.");
        File.Delete(downloaded);
    }).GetAwaiter().GetResult();
}

if (args.Contains("--capture-benchmark"))
{
    var benchmarkScreen = Screen.PrimaryScreen!;
    var benchmarkBounds = new Rectangle(benchmarkScreen.Bounds.Left + 200, benchmarkScreen.Bounds.Top + 200, 400, 170);
    using var desktop = ScreenCapture.OpenDesktop();
    using var duplication = new DesktopDuplicationCapture();
    using var gdiSurface = new ScreenCaptureSurface(400, 170, desktop.DeviceContext);
    using var dxgiSurface = new ScreenCaptureSurface(400, 170, desktop.DeviceContext);
    gdiSurface.Capture(desktop.DeviceContext, benchmarkBounds);
    var before = new byte[gdiSurface.Stride * 170];
    Marshal.Copy(gdiSurface.Pixels, before, 0, before.Length);
    var duplicated = false;
    for (var attempt = 0; attempt < 100 && !duplicated; attempt++)
    {
        // Duplication only serves frames once something has presented after it started.
        duplication.BeginTick();
        duplicated = duplication.TryCapture(benchmarkScreen.DeviceName, benchmarkBounds, dxgiSurface, out _);
        Thread.Sleep(duplicated ? 0 : 20);
    }

    if (duplicated)
    {
        gdiSurface.Capture(desktop.DeviceContext, benchmarkBounds);
        int stable = 0, differing = 0;
        for (var offset = 0; offset < before.Length; offset++)
        {
            if (offset % 4 != 3 && Marshal.ReadByte(gdiSurface.Pixels, offset) == before[offset])
            {
                stable++;
                differing += Marshal.ReadByte(dxgiSurface.Pixels, offset) != before[offset] ? 1 : 0;
            }
        }

        Console.WriteLine($"DXGI duplication available; {differing} of {stable} stable color bytes differ from GDI.");
        RunCaptureBenchmark("DXGI", () =>
        {
            duplication.BeginTick();
            return duplication.TryCapture(benchmarkScreen.DeviceName, benchmarkBounds, dxgiSurface, out _) ? dxgiSurface : null;
        });
    }
    else
    {
        Console.WriteLine($"DXGI duplication unavailable or no frame presented within 2 s: {duplication.LastError}");
    }

    RunCaptureBenchmark("GDI", () =>
    {
        gdiSurface.Capture(desktop.DeviceContext, benchmarkBounds);
        return gdiSurface;
    });
}

static void RunCaptureBenchmark(string name, Func<ScreenCaptureSurface?> capture)
{
    var clock = Stopwatch.StartNew();
    var captures = 0;
    long workTicks = 0;
    var nextFrame = Stopwatch.GetTimestamp();
    using var wait = new PrecisionWait();
    using var never = new ManualResetEvent(false);
    while (clock.Elapsed < TimeSpan.FromSeconds(2))
    {
        var now = Stopwatch.GetTimestamp();
        if (now < nextFrame)
        {
            wait.Wait(TimeSpan.FromSeconds((nextFrame - now) / (double)Stopwatch.Frequency), never);
            continue;
        }

        var started = Stopwatch.GetTimestamp();
        var surface = capture() ?? throw new InvalidOperationException($"{name} capture failed.");
        AbilitySignalDetector.Read(surface.Pixels, surface.Stride, 0, 0, 60, 60, 6);
        var finished = Stopwatch.GetTimestamp();
        workTicks += finished - started;
        nextFrame = CaptureSchedule.NextFrame(started, finished, 120, Stopwatch.Frequency);
        captures++;
    }

    Console.WriteLine($"Load-limited 400x170 {name} capture: {captures / clock.Elapsed.TotalSeconds:F1} Hz, " +
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

static AbilitySignal ReadSyntheticProgress(int size, float progress)
{
    var data = new byte[size * size * 4];
    for (var y = (int)(size * (1 - progress)); y < size; y++)
    {
        for (var x = 0; x < size; x++)
        {
            var offset = (y * size + x) * 4;
            data[offset] = data[offset + 1] = data[offset + 2] = 255;
        }
    }

    var buffer = Marshal.AllocHGlobal(data.Length);
    try
    {
        Marshal.Copy(data, 0, buffer, data.Length);
        return AbilitySignalDetector.Read(buffer, size * 4, 0, 0, size, size);
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

using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using D2AbilityTracker;

/// <summary>
/// Replays a recorded HUD zip (AbilityHudRecorder output) through the real detector and trackers and compares
/// recharge start/complete frames with human annotations.
/// Usage: --replay-hud recording.zip annotations.json [regions.json]
///        --sweep-hud recording.zip annotations.json [regions.json]
/// Slot regions come from the explicit file, else the recording's own regions.json, else the current AppData settings.
/// </summary>
internal static class HudReplay
{
    private sealed record Cycle(int Slot, int Start, int? End);
    private sealed record Detected(int Start, int? End, bool Estimated = false, double Uncertainty = 0);
    internal readonly record struct SlotArea(Rectangle Region, int Top, int Bottom);
    private static readonly string[] Names = ["grenade", "melee", "class"];

    /// <summary>One replay from a startup frame, optionally after garbage frames, with its own trackers.</summary>
    private sealed class Session(int firstFrame, int garbage)
    {
        public int FirstFrame { get; } = firstFrame;
        public int Garbage { get; } = garbage;
        public AbilitySlotTracker[] Trackers { get; } = Enum.GetValues<AbilityKind>().Select(kind => new AbilitySlotTracker(kind)).ToArray();
        public List<Detected>[] Found { get; } = [[], [], []];
        public int[] FirstReady { get; } = new int[3];
        private readonly int?[] _open = new int?[3];

        public (AbilitySignal Signal, bool Completed) Step(int frame, DateTime timestamp, IntPtr pixels, int stride, int slot,
            SlotArea area, DateTime[] times)
        {
            var tracker = Trackers[slot];
            var before = tracker.State;
            var signal = AbilitySignalDetector.Read(pixels, stride, area.Region.X, area.Region.Y, area.Region.Width,
                area.Region.Height, area.Bottom, tracker.ChargeProgress, tracker.ProgressSearchTolerance(timestamp), area.Top,
                tracker.Guide(timestamp));
            var completed = tracker.Observe(signal, timestamp);
            var found = Found[slot];
            if (FirstReady[slot] == 0 && tracker.IsCharged && frame >= FirstFrame + Garbage) { FirstReady[slot] = frame; }
            if (before != AbilityState.Recharging && tracker.State == AbilityState.Recharging)
            {
                found.Add(new Detected(frame, null, tracker.Estimating));
                _open[slot] = found.Count - 1;
            }
            else if (before == AbilityState.Recharging && tracker.State != AbilityState.Recharging && !completed)
            {
                _open[slot] = null; // measurement abandoned
            }

            if (completed && tracker.LastDuration is { } measured && Environment.GetEnvironmentVariable("HUD_REGRESSION") == "1")
            {
                // Regression totals against the event-timed duration, for checking the regression uncertainty.
                Console.WriteLine($"REG slot={slot} source={tracker.LastSource} duration={measured.TotalSeconds:F4} " +
                    $"regression={tracker.LastRegressionDuration?.TotalSeconds:F4} sigma={tracker.LastRegressionUncertainty?.TotalSeconds:F4}");
            }

            if (completed && _open[slot] is { } index && tracker.LastDuration is { } duration && tracker.LastStartedAt is { } started)
            {
                found[index] = new Detected(NearestFrame(times, started), NearestFrame(times, started + duration),
                    tracker.LastSource == MeasurementSource.Estimated, tracker.LastUncertainty?.TotalSeconds ?? 0);
                _open[slot] = null;
            }

            if (completed && tracker.State == AbilityState.Recharging)
            {
                // A recast's start flash closed one recharge and opened the next (confirmed a second later).
                found.Add(new Detected(frame, null));
                _open[slot] = found.Count - 1;
            }

            if (!completed && _open[slot] is { } running && tracker.State == AbilityState.Recharging &&
                tracker.CurrentStartedAt is { } current)
            {
                // The tracker refines its start (a use pulse before the real bar, or a late start bar).
                found[running] = found[running] with { Start = NearestFrame(times, current) };
            }

            return (signal, completed);
        }
    }

    /// <summary>A cycle whose start bar comes this soon after a mid-recording startup may have been used before it (the
    /// longest use-to-bar delay seen is 2.6 s): not measuring it is correct.</summary>
    internal const double UnmeasurableSeconds = 3;

    // Errors are scored in seconds from frames.csv capture times (the recordings are not a steady 60 FPS); frame
    // counts are a secondary figure. CeilingSeconds is the user's limit for any start or completion.
    internal const double CeilingSeconds = 0.1;

    private sealed record Score(int FalseStarts, int MissedStarts, int MissedEnds, List<int> StartErrors, List<int> EndErrors,
        List<double> StartSeconds, List<double> EndSeconds, List<string> Lines,
        List<(double Observed, double Error)> Estimates);

    public static int Run(string[] args)
    {
        if (args.Length < 3) { throw new ArgumentException("Usage: --replay-hud <recording.zip> <annotations.json> [regions.json]"); }
        using var archive = ZipFile.OpenRead(args[1]);
        var (bounds, areas, times, source, regionsJson) = Open(archive, args);
        using var fixture = args[0] == "--extract-hud-fixtures" ? HudSequences.CreateWriter(args[1], args[2], regionsJson) : null;
        // HUD_START=N skips frames before N (startup at an arbitrary point); HUD_GARBAGE=K replaces the first K
        // replayed frames with a solid wrong-colour fill, like a stale or invalid capture at startup.
        var firstFrame = int.TryParse(Environment.GetEnvironmentVariable("HUD_START"), out var startAt) ? Math.Max(1, startAt) : 1;
        var garbageFrames = int.TryParse(Environment.GetEnvironmentVariable("HUD_GARBAGE"), out var garbage) ? Math.Max(0, garbage) : 0;
        var session = new Session(firstFrame, garbageFrames);
        // HUD_TRACE=slot,fromFrame,toFrame prints per-frame signals for debugging.
        var traceText = Environment.GetEnvironmentVariable("HUD_TRACE")?.Split(',');
        (int Slot, int From, int To)? trace = traceText is { Length: 3 }
            ? (int.Parse(traceText[0]), int.Parse(traceText[1]), int.Parse(traceText[2])) : null;
        Console.WriteLine($"regions: {source}");
        Stream(archive, bounds, firstFrame, garbageFrames, (frame, pixels, stride) =>
        {
            var timestamp = times[frame - 1];
            for (var slot = 0; slot < 3; slot++)
            {
                var (signal, completed) = session.Step(frame, timestamp, pixels, stride, slot, areas[slot], times);
                if (fixture is not null)
                {
                    HudSequences.SaveCrop(args[1], frame, slot, pixels, stride, areas[slot], signal);
                    fixture.WriteLine(JsonSerializer.Serialize(new HudSequences.Sample(frame, slot, timestamp, signal), HudSequences.Options));
                }

                if (trace is { } t && t.Slot == slot && frame >= t.From && frame <= t.To)
                {
                    var tracker = session.Trackers[slot];
                    Console.WriteLine($"f{frame} s{slot} {tracker.State,-10} ch={signal.Charged,-5} lum={signal.FillLuminance:F2} " +
                        $"line={signal.CooldownLineRatio:F2} prog={signal.ChargeProgress:F2} bb={signal.BottomBorderThickness:F3} " +
                        $"flash={signal.UseFlash} blank={signal.Blank} sat={signal.ChargedBackgroundRatio:F2} fs={signal.FillSaturation:F3} " +
                        $"bar={signal.BottomBarOpacity:F2} hue={signal.FillHue} done={completed} isCh={tracker.IsCharged} warn={tracker.TrackingWarning}");
                }
            }
        });

        var score = Evaluate(ReadCycles(args[2]), session, times);
        Console.WriteLine($"== {Path.GetFileName(args[1])}");
        Console.WriteLine("slot     cycle  gtStart  detStart  dStart  gtEnd  detEnd  dEnd");
        score.Lines.ForEach(Console.WriteLine);
        Console.WriteLine($"false starts: {score.FalseStarts}; mid-recharge estimates (observed share, error s): " +
            string.Join(' ', score.Estimates.Select(e => $"{e.Observed:P0}:{e.Error:+0.000;-0.000}")));
        Console.WriteLine($"missed starts: {score.MissedStarts}, missed completions: {score.MissedEnds}");
        Console.WriteLine($"start abs error mean/max: {MeanSeconds(score.StartSeconds):F3}/{MaxSeconds(score.StartSeconds):F3} s " +
            $"({Mean(score.StartErrors):F1}/{Max(score.StartErrors)} frames); completion mean/max: {MeanSeconds(score.EndSeconds):F3}/" +
            $"{MaxSeconds(score.EndSeconds):F3} s ({Mean(score.EndErrors):F1}/{Max(score.EndErrors)} frames)");
        if (firstFrame > 1 || garbageFrames > 0)
        {
            Console.WriteLine($"startup: start={firstFrame} garbage={garbageFrames} first charged frame per slot: {string.Join(' ', session.FirstReady)}");
        }

        return 0;
    }

    /// <summary>
    /// Replays the recording once, decoding each frame once, with an independent tracker session started at every
    /// HUD_SWEEP_STEP frames (default 300), each both clean and after HUD_GARBAGE (default 10) garbage frames.
    /// Only annotated cycles that start after a session's startup are scored. Returns 1 on any false or missed event.
    /// </summary>
    public static int Sweep(string[] args)
    {
        if (args.Length < 3) { throw new ArgumentException("Usage: --sweep-hud <recording.zip> <annotations.json> [regions.json]"); }
        using var archive = ZipFile.OpenRead(args[1]);
        var (bounds, areas, times, source, _) = Open(archive, args);
        var step = int.TryParse(Environment.GetEnvironmentVariable("HUD_SWEEP_STEP"), out var s) ? Math.Max(1, s) : 300;
        var garbageFrames = int.TryParse(Environment.GetEnvironmentVariable("HUD_GARBAGE"), out var g) ? Math.Max(1, g) : 10;
        var cycles = ReadCycles(args[2]);
        var sessions = new List<Session>();
        for (var first = 1; first < times.Length - 600; first += step)
        {
            sessions.Add(new Session(first, 0));
            sessions.Add(new Session(first, garbageFrames));
        }

        Stream(archive, bounds, 1, 0, (frame, pixels, stride) =>
        {
            var timestamp = times[frame - 1];
            foreach (var session in sessions.Where(session => frame >= session.FirstFrame))
            {
                var garbage = frame < session.FirstFrame + session.Garbage;
                var buffer = garbage ? GarbageBuffer(bounds) : pixels;
                for (var slot = 0; slot < 3; slot++) { session.Step(frame, timestamp, buffer, stride, slot, areas[slot], times); }
            }
        });

        Console.WriteLine($"== sweep {Path.GetFileName(args[1])} (regions: {source}; every {step} frames, garbage 0/{garbageFrames})");
        var failures = 0;
        var estimates = new List<(double Observed, double Error)>();
        var startErrors = new List<int>();
        var endErrors = new List<int>();
        var startSeconds = new List<double>();
        var endSeconds = new List<double>();
        var overCeiling = 0;
        foreach (var session in sessions)
        {
            var score = Evaluate(cycles, session, times);
            estimates.AddRange(score.Estimates);
            startErrors.AddRange(score.StartErrors);
            endErrors.AddRange(score.EndErrors);
            startSeconds.AddRange(score.StartSeconds);
            endSeconds.AddRange(score.EndSeconds);
            var overLimit = score.StartSeconds.Concat(score.EndSeconds).Any(error => error > CeilingSeconds);
            if (overLimit) { overCeiling++; }
            var inaccurate = overLimit || score.StartErrors.Concat(score.EndErrors).Any(error => error > 2);
            if (inaccurate && Environment.GetEnvironmentVariable("HUD_SWEEP_VERBOSE") is not null)
            {
                Console.WriteLine($"start={session.FirstFrame} garbage={session.Garbage}: errors over 2 frames or 0.1 s");
                score.Lines.ForEach(line => Console.WriteLine("  " + line));
            }

            if (score.FalseStarts + score.MissedStarts + score.MissedEnds == 0) { continue; }
            failures++;
            Console.WriteLine($"start={session.FirstFrame} garbage={session.Garbage}: false {score.FalseStarts}, missed starts {score.MissedStarts}, " +
                $"missed completions {score.MissedEnds}");
            score.Lines.Where(line => line.Contains("MISSED", StringComparison.Ordinal) || line.StartsWith("false", StringComparison.Ordinal))
                .ToList().ForEach(line => Console.WriteLine("  " + line));
        }

        Console.WriteLine($"sessions: {sessions.Count}, failing (false/missed): {failures}, over 0.1 s: {overCeiling}; " +
            $"start abs error mean/max {MeanSeconds(startSeconds):F3}/{MaxSeconds(startSeconds):F3} s ({Mean(startErrors):F2}/{Max(startErrors)} frames), " +
            $"completion {MeanSeconds(endSeconds):F3}/{MaxSeconds(endSeconds):F3} s ({Mean(endErrors):F2}/{Max(endErrors)} frames) " +
            $"over {startErrors.Count}/{endErrors.Count} cycle replays");
        Console.WriteLine("mid-recharge estimates: observed share of cycle -> |estimated - annotated total| (s) mean/max, count");
        foreach (var (low, high) in new[] { (0.0, 0.1), (0.1, 0.25), (0.25, 0.5), (0.5, 0.75), (0.75, 1.01) })
        {
            var bucket = estimates.Where(e => e.Observed >= low && e.Observed < high).Select(e => Math.Abs(e.Error)).ToList();
            Console.WriteLine($"  {low * 100,3:F0}-{Math.Min(100, high * 100),3:F0}%: " +
                (bucket.Count == 0 ? "none" : $"{bucket.Average():F3}/{bucket.Max():F3} s, n={bucket.Count}"));
        }

        return failures == 0 && overCeiling == 0 ? 0 : 1;
    }

    private static IntPtr _garbage;
    private static int _garbageBytes;

    private static IntPtr GarbageBuffer(Rectangle bounds)
    {
        var bytes = bounds.Width * 4 * bounds.Height;
        if (_garbageBytes != bytes)
        {
            if (_garbage != IntPtr.Zero) { Marshal.FreeHGlobal(_garbage); }
            _garbage = Marshal.AllocHGlobal(bytes);
            _garbageBytes = bytes;
            var solid = new byte[] { 255, 60, 200, 255 };
            for (var offset = 0; offset < bytes; offset += 4) { Marshal.Copy(solid, 0, _garbage + offset, 4); }
        }

        return _garbage;
    }

    /// <summary>Streams frames from <paramref name="from"/> into a capture-bounds pixel buffer, like the live loop.</summary>
    private static void Stream(ZipArchive archive, Rectangle bounds, int from, int garbage, Action<int, IntPtr, int> process)
    {
        var stride = bounds.Width * 4;
        var buffer = Marshal.AllocHGlobal(stride * bounds.Height);
        var line = new byte[stride];
        try
        {
            var frame = 0;
            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("frames/", StringComparison.Ordinal) &&
                e.FullName.EndsWith(".png", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                frame++;
                if (frame < from) { continue; }
                if (frame < from + garbage)
                {
                    process(frame, GarbageBuffer(bounds), stride);
                    continue;
                }

                using (var stream = entry.Open())
                using (var memory = new MemoryStream())
                {
                    stream.CopyTo(memory);
                    memory.Position = 0;
                    using var image = new Bitmap(memory);
                    var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly,
                        PixelFormat.Format32bppRgb);
                    try
                    {
                        for (var y = 0; y < bounds.Height; y++)
                        {
                            Marshal.Copy(data.Scan0 + y * data.Stride, line, 0, stride);
                            Marshal.Copy(line, 0, buffer + y * stride, stride);
                        }
                    }
                    finally { image.UnlockBits(data); }
                }

                process(frame, buffer, stride);
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static (Rectangle Bounds, SlotArea[] Areas, DateTime[] Times, string Source, string RegionsJson) Open(ZipArchive archive, string[] args)
    {
        var bounds = ReadBounds(archive);
        string source;
        string json;
        if (args.Length > 3)
        {
            source = args[3];
            json = File.ReadAllText(args[3]);
        }
        else if (archive.GetEntry("regions.json") is { } embedded)
        {
            source = "recording's regions.json";
            using var reader = new StreamReader(embedded.Open());
            json = reader.ReadToEnd();
        }
        else
        {
            source = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D2AbilityTracker", "settings.json");
            json = File.ReadAllText(source);
        }

        using var document = JsonDocument.Parse(json);
        return (bounds, ReadAreas(document, bounds), ReadTimes(archive), source, json);
    }

    /// <summary>
    /// Reads slot regions (screen coordinates) and converts them to the capture buffer. Saved paddings are used when the
    /// recording has them; otherwise padding is computed as the live loop does, limited by the captured area.
    /// </summary>
    internal static SlotArea[] ReadAreas(JsonDocument document, Rectangle bounds)
    {
        var areas = new SlotArea[3];
        foreach (var slot in document.RootElement.GetProperty("Slots").EnumerateArray())
        {
            var region = slot.GetProperty("Region");
            var local = new Rectangle(region.GetProperty("X").GetInt32() - bounds.X, region.GetProperty("Y").GetInt32() - bounds.Y,
                region.GetProperty("Width").GetInt32(), region.GetProperty("Height").GetInt32());
            var bottom = slot.TryGetProperty("BottomPadding", out var b) ? b.GetInt32() : AbilitySignalDetector.BottomPadding(local.Height);
            var top = slot.TryGetProperty("TopPadding", out var t) ? t.GetInt32() : AbilitySignalDetector.TopPadding(local.Height);
            areas[slot.GetProperty("Kind").GetInt32()] = new SlotArea(local, Math.Clamp(top, 0, Math.Max(0, local.Top)),
                Math.Clamp(bottom, 0, Math.Max(0, bounds.Height - local.Bottom)));
        }

        return areas;
    }

    private static int NearestFrame(DateTime[] times, DateTime target)
    {
        var index = Array.BinarySearch(times, target);
        if (index >= 0) { return index + 1; }
        index = ~index;
        if (index == 0) { return 1; }
        if (index == times.Length) { return times.Length; }
        return target - times[index - 1] <= times[index] - target ? index : index + 1;
    }

    /// <summary>Matches detections to annotated cycles that start after the session's startup (within 300 frames).</summary>
    private static Score Evaluate(List<Cycle> allCycles, Session session, DateTime[] times)
    {
        var cycles = allCycles.Where(c => c.Start >= session.FirstFrame + session.Garbage).ToList();
        var detected = session.Found;
        var used = new HashSet<(int, int)>();
        // Cycles already running at startup: an estimate (if any) is matched by its completion and scored separately.
        var estimates = new List<(double Observed, double Error)>();
        var startup = session.FirstFrame + session.Garbage;
        foreach (var cycle in allCycles.Where(c => c.Start < startup && c.End is { } end && end > startup))
        {
            var index = detected[cycle.Slot].FindIndex(d => d.Estimated && d.End is { } e && Math.Abs(e - cycle.End!.Value) < 120);
            if (index < 0 || used.Contains((cycle.Slot, index))) { continue; }
            used.Add((cycle.Slot, index));
            var match = detected[cycle.Slot][index];
            var annotated = (times[cycle.End!.Value - 1] - times[cycle.Start - 1]).TotalSeconds;
            var estimated = (times[match.End!.Value - 1] - times[match.Start - 1]).TotalSeconds;
            estimates.Add(((cycle.End.Value - startup) / (double)(cycle.End.Value - cycle.Start), estimated - annotated));
            if (Environment.GetEnvironmentVariable("HUD_SWEEP_VERBOSE") is not null)
            {
                Console.WriteLine($"  estimate start={session.FirstFrame}+{session.Garbage} {Names[cycle.Slot]} annotated {cycle.Start}-{cycle.End} " +
                    $"estimated {match.Start}-{match.End}: {estimated - annotated:+0.000;-0.000} s, uncertainty {match.Uncertainty:F3} s");
            }
        }

        foreach (var cycle in allCycles.Where(c => c.Start < startup && c.End is null))
        {
            var index = detected[cycle.Slot].FindIndex(d => d.Estimated && d.End is null);
            if (index >= 0) { used.Add((cycle.Slot, index)); } // still running at the end of the recording
        }

        var lines = new List<string>();
        var startSeconds = new List<double>();
        var endSeconds = new List<double>();
        var startErrors = new List<int>();
        var endErrors = new List<int>();
        var missedStarts = 0;
        var missedEnds = 0;
        foreach (var group in cycles.GroupBy(c => c.Slot))
        {
            var number = 0;
            foreach (var cycle in group.OrderBy(c => c.Start))
            {
                number++;
                var bestIndex = -1;
                for (var i = 0; i < detected[cycle.Slot].Count; i++)
                {
                    if (used.Contains((cycle.Slot, i))) { continue; }
                    var distance = Math.Abs(detected[cycle.Slot][i].Start - cycle.Start);
                    if (distance < 300 && (bestIndex < 0 ||
                        distance < Math.Abs(detected[cycle.Slot][bestIndex].Start - cycle.Start)))
                    {
                        bestIndex = i;
                    }
                }

                var startText = "MISSED";
                var endText = cycle.End is null ? "-" : "MISSED";
                var dStart = "";
                var dEnd = "";
                if (bestIndex >= 0)
                {
                    used.Add((cycle.Slot, bestIndex));
                    var match = detected[cycle.Slot][bestIndex];
                    startText = match.Start.ToString(CultureInfo.InvariantCulture);
                    dStart = (match.Start - cycle.Start).ToString("+0;-0;0", CultureInfo.InvariantCulture);
                    startErrors.Add(Math.Abs(match.Start - cycle.Start));
                    startSeconds.Add(Math.Abs((times[match.Start - 1] - times[cycle.Start - 1]).TotalSeconds));
                    if (cycle.End is { } end && match.End is { } found)
                    {
                        endText = found.ToString(CultureInfo.InvariantCulture);
                        dEnd = (found - end).ToString("+0;-0;0", CultureInfo.InvariantCulture);
                        endErrors.Add(Math.Abs(found - end));
                        endSeconds.Add(Math.Abs((times[found - 1] - times[end - 1]).TotalSeconds));
                    }
                    else if (cycle.End is not null) { missedEnds++; }
                }
                else if (session.FirstFrame > 1 &&
                    (times[cycle.Start - 1] - times[session.FirstFrame + session.Garbage - 1]).TotalSeconds < UnmeasurableSeconds)
                {
                    // The use (and its flash) can precede startup by up to the use-to-bar delay: the start time is
                    // unknowable, so not measuring this cycle is correct.
                    startText = "SKIPPED";
                    endText = cycle.End is null ? "-" : "SKIPPED";
                }
                else
                {
                    missedStarts++;
                    if (cycle.End is not null) { missedEnds++; }
                }

                lines.Add($"{Names[cycle.Slot],-8} {number,5}  {cycle.Start,7}  {startText,8}  {dStart,6}  {cycle.End?.ToString(CultureInfo.InvariantCulture) ?? "-",5}  {endText,6}  {dEnd,4}");
            }
        }

        var falseStarts = new List<string>();
        for (var slot = 0; slot < 3; slot++)
        {
            for (var i = 0; i < detected[slot].Count; i++)
            {
                // An estimate dropped for lack of observed progress reports nothing: not a false start.
                if (detected[slot][i] is { Estimated: true, End: null }) { continue; }
                if (!used.Contains((slot, i))) { falseStarts.Add($"{Names[slot]}@{detected[slot][i].Start}"); }
            }
        }

        if (falseStarts.Count > 0) { lines.Add($"false: {string.Join(' ', falseStarts)}"); }
        return new Score(falseStarts.Count, missedStarts, missedEnds, startErrors, endErrors, startSeconds, endSeconds, lines, estimates);
    }

    private static double Mean(List<int> values) => values.Count == 0 ? 0 : values.Average();
    private static double MeanSeconds(List<double> values) => values.Count == 0 ? 0 : values.Average();
    private static double MaxSeconds(List<double> values) => values.Count == 0 ? 0 : values.Max();
    private static int Max(List<int> values) => values.Count == 0 ? 0 : values.Max();

    private static Rectangle ReadBounds(ZipArchive archive)
    {
        using var reader = new StreamReader(archive.GetEntry("recording.txt")!.Open());
        while (reader.ReadLine() is { } text)
        {
            if (text.StartsWith("Bounds:", StringComparison.Ordinal))
            {
                var parts = text["Bounds:".Length..].Trim().Split(' ')[0].Split(',').Select(int.Parse).ToArray();
                return new Rectangle(parts[0], parts[1], parts[2], parts[3]);
            }
        }

        throw new InvalidDataException("recording.txt has no Bounds line.");
    }

    private static DateTime[] ReadTimes(ZipArchive archive)
    {
        var times = new List<DateTime>();
        using var reader = new StreamReader(archive.GetEntry("frames.csv")!.Open());
        reader.ReadLine();
        while (reader.ReadLine() is { } text)
        {
            var parts = text.Split(',');
            times.Add(DateTime.Parse(parts[2], CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime());
        }

        return times.ToArray();
    }

    private static List<Cycle> ReadCycles(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var cycles = new List<Cycle>();
        foreach (var cycle in document.RootElement.GetProperty("cycles").EnumerateArray())
        {
            var slot = cycle.GetProperty("slot").GetString() switch { "grenade" => 0, "melee" => 1, _ => 2 };
            var end = cycle.GetProperty("endFrame");
            cycles.Add(new Cycle(slot, cycle.GetProperty("startFrame").GetInt32(),
                end.ValueKind == JsonValueKind.Number ? end.GetInt32() : null));
        }

        return cycles;
    }
}

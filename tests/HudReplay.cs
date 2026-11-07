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
/// Usage: --replay-hud recording.zip annotations.json [settings.json]
/// </summary>
internal static class HudReplay
{
    private sealed record Cycle(int Slot, int Start, int? End);
    private sealed record Detected(int Start, int? End, double? Duration);

    public static int Run(string[] args)
    {
        if (args.Length < 3) { throw new ArgumentException("Usage: --replay-hud <recording.zip> <annotations.json> [settings.json]"); }
        var settingsPath = args.Length > 3 ? args[3] : Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "D2AbilityTracker", "settings.json");
        using var fixture = args[0] == "--extract-hud-fixtures" ? HudSequences.CreateWriter(args[1], args[2], settingsPath) : null;
        using var archive = ZipFile.OpenRead(args[1]);
        var bounds = ReadBounds(archive);
        var regions = ReadRegions(settingsPath);
        var times = ReadTimes(archive);
        var cycles = ReadCycles(args[2]);

        // Same arrangement as the live loop: the pixel buffer is the capture bounds, padding is limited by the monitor.
        var trackers = Enum.GetValues<AbilityKind>().Select(kind => new AbilitySlotTracker(kind)).ToArray();
        var startTimes = new List<DateTime>[3] { [], [], [] };
        var detected = new List<Detected>[3] { [], [], [] };
        var open = new int?[3];
        var lastElapsed = new TimeSpan[3];
        var stride = bounds.Width * 4;
        var buffer = Marshal.AllocHGlobal(stride * bounds.Height);
        var line = new byte[stride];
        // HUD_TRACE=slot,fromFrame,toFrame prints per-frame signals for debugging.
        var traceText = Environment.GetEnvironmentVariable("HUD_TRACE")?.Split(',');
        (int Slot, int From, int To)? trace = traceText is { Length: 3 }
            ? (int.Parse(traceText[0]), int.Parse(traceText[1]), int.Parse(traceText[2])) : null;
        try
        {
            var frame = 0;
            foreach (var entry in archive.Entries.Where(e => e.FullName.StartsWith("frames/", StringComparison.Ordinal) &&
                e.FullName.EndsWith(".png", StringComparison.Ordinal)).OrderBy(e => e.FullName, StringComparer.Ordinal))
            {
                frame++;
                var timestamp = times[frame - 1];
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

                for (var slot = 0; slot < 3; slot++)
                {
                    var region = new Rectangle(regions[slot].X - bounds.X, regions[slot].Y - bounds.Y,
                        regions[slot].Width, regions[slot].Height);
                    var bottomPadding = Math.Min(AbilitySignalDetector.BottomPadding(region.Height), bounds.Height - region.Bottom);
                    var topPadding = Math.Min(AbilitySignalDetector.TopPadding(region.Height), region.Top);
                    var tracker = trackers[slot];
                    var before = tracker.State;
                    var signal = AbilitySignalDetector.Read(buffer, stride, region.X, region.Y, region.Width, region.Height,
                        Math.Max(0, bottomPadding), tracker.ChargeProgress, tracker.ProgressSearchTolerance(timestamp),
                        Math.Max(0, topPadding), tracker.Guide(timestamp));
                    if (fixture is not null)
                        HudSequences.SaveCrop(args[1], frame, slot, buffer, stride, region, Math.Max(0, topPadding), Math.Max(0, bottomPadding), signal);
                    fixture?.WriteLine(JsonSerializer.Serialize(new HudSequences.Sample(frame, slot, timestamp, signal), HudSequences.Options));
                    var completed = tracker.Observe(signal, timestamp);
                    if (trace is { } t && t.Slot == slot && frame >= t.From && frame <= t.To)
                    {
                        Console.WriteLine($"f{frame} s{slot} {tracker.State,-10} ch={signal.Charged,-5} lum={signal.FillLuminance:F2} " +
                            $"line={signal.CooldownLineRatio:F2} prog={signal.ChargeProgress:F2} bb={signal.BottomBorderThickness:F3} " +
                            $"flash={signal.UseFlash} blank={signal.Blank} sat={signal.ChargedBackgroundRatio:F2} fs={signal.FillSaturation:F3} done={completed} isCh={tracker.IsCharged} warn={tracker.TrackingWarning}");
                    }

                    if (before != AbilityState.Recharging && tracker.State == AbilityState.Recharging)
                    {
                        startTimes[slot].Add(timestamp);
                        detected[slot].Add(new Detected(frame, null, null));
                        open[slot] = detected[slot].Count - 1;
                    }
                    else if (before == AbilityState.Recharging && tracker.State != AbilityState.Recharging && !completed)
                    {
                        open[slot] = null; // measurement abandoned
                    }

                    if (completed && open[slot] is { } index && tracker.LastDuration is { } duration)
                    {
                        // The duration includes any start shift, so the effective start is earlier than the flash.
                        var shift = tracker.LastStartShift;
                        var endFrame = NearestFrame(times, startTimes[slot][index] + duration - shift);
                        var startFrame = shift > TimeSpan.Zero ? NearestFrame(times, startTimes[slot][index] - shift) : detected[slot][index].Start;
                        detected[slot][index] = detected[slot][index] with { Start = startFrame, End = endFrame, Duration = duration.TotalSeconds };
                        open[slot] = null;
                    }

                    if (completed && tracker.State == AbilityState.Recharging)
                    {
                        // A recast's start flash closed one recharge and opened the next (started at the flash, confirmed a second later).
                        var flash = timestamp - (tracker.CurrentDuration ?? TimeSpan.Zero);
                        startTimes[slot].Add(flash);
                        detected[slot].Add(new Detected(NearestFrame(times, flash), null, null));
                        open[slot] = detected[slot].Count - 1;
                        lastElapsed[slot] = TimeSpan.Zero;
                    }

                    if (!completed && open[slot] is { } running && tracker.State == AbilityState.Recharging &&
                        tracker.CurrentDuration is { } elapsed && elapsed < lastElapsed[slot] - TimeSpan.FromSeconds(0.03))
                    {
                        // The tracker moved its start later (the earlier thickening was a use pulse).
                        startTimes[slot][running] = timestamp - elapsed;
                        detected[slot][running] = detected[slot][running] with { Start = NearestFrame(times, timestamp - elapsed) };
                    }

                    lastElapsed[slot] = tracker.CurrentDuration ?? TimeSpan.Zero;
                }
            }

            Report(cycles, detected, args[1]);
        }
        finally { Marshal.FreeHGlobal(buffer); }

        return 0;
    }

    private static int NearestFrame(DateTime[] times, DateTime target)
    {
        var best = 0;
        for (var i = 0; i < times.Length; i++)
        {
            if (Math.Abs((times[i] - target).Ticks) < Math.Abs((times[best] - target).Ticks)) { best = i; }
        }

        return best + 1;
    }

    private static void Report(List<Cycle> cycles, List<Detected>[] detected, string name)
    {
        string[] names = ["grenade", "melee", "class"];
        Console.WriteLine($"== {Path.GetFileName(name)}");
        Console.WriteLine("slot     cycle  gtStart  detStart  dStart  gtEnd  detEnd  dEnd");
        var startErrors = new List<int>();
        var endErrors = new List<int>();
        var used = new HashSet<(int, int)>();
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
                    if (cycle.End is { } end && match.End is { } found)
                    {
                        endText = found.ToString(CultureInfo.InvariantCulture);
                        dEnd = (found - end).ToString("+0;-0;0", CultureInfo.InvariantCulture);
                        endErrors.Add(Math.Abs(found - end));
                    }
                    else if (cycle.End is not null) { missedEnds++; }
                }
                else
                {
                    missedStarts++;
                    if (cycle.End is not null) { missedEnds++; }
                }

                Console.WriteLine($"{names[cycle.Slot],-8} {number,5}  {cycle.Start,7}  {startText,8}  {dStart,6}  {cycle.End?.ToString() ?? "-",5}  {endText,6}  {dEnd,4}");
            }
        }

        var falseStarts = new List<string>();
        for (var slot = 0; slot < 3; slot++)
        {
            for (var i = 0; i < detected[slot].Count; i++)
            {
                if (!used.Contains((slot, i))) { falseStarts.Add($"{names[slot]}@{detected[slot][i].Start}"); }
            }
        }

        Console.WriteLine($"false starts: {falseStarts.Count} {string.Join(' ', falseStarts)}");
        Console.WriteLine($"missed starts: {missedStarts}, missed completions: {missedEnds}");
        Console.WriteLine($"start abs error mean/max: {Mean(startErrors):F1}/{Max(startErrors)}; completion mean/max: {Mean(endErrors):F1}/{Max(endErrors)}");
    }

    private static double Mean(List<int> values) => values.Count == 0 ? 0 : values.Average();
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

    private static Rectangle[] ReadRegions(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var regions = new Rectangle[3];
        foreach (var slot in document.RootElement.GetProperty("Slots").EnumerateArray())
        {
            var region = slot.GetProperty("Region");
            regions[slot.GetProperty("Kind").GetInt32()] = new Rectangle(region.GetProperty("X").GetInt32(),
                region.GetProperty("Y").GetInt32(), region.GetProperty("Width").GetInt32(), region.GetProperty("Height").GetInt32());
        }

        return regions;
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

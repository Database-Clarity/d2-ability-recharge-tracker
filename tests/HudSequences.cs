using System.Drawing;
using System.Drawing.Imaging;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using D2AbilityTracker;

internal static class HudSequences
{
    internal sealed record Sample(int Frame, int Slot, DateTime Time, AbilitySignal Signal);
    internal static readonly JsonSerializerOptions Options = new() { Converters = { new ColorConverter() } };

    private sealed class ColorConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => Color.FromArgb(reader.GetInt32());
        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) => writer.WriteNumberValue(value.ToArgb());
    }

    internal static StreamWriter CreateWriter(string recording, string annotations, string regionsJson)
    {
        var directory = Path.Combine("tests", "fixtures", "hud-sequences");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(recording);
        using var settings = JsonDocument.Parse(regionsJson);
        var regions = new
        {
            Slots = settings.RootElement.GetProperty("Slots").EnumerateArray().Select(slot => new
            {
                Kind = slot.GetProperty("Kind").GetInt32(),
                Region = slot.GetProperty("Region").Clone()
            }).ToArray()
        };
        File.WriteAllText(Path.Combine(directory, name + ".regions.json"), JsonSerializer.Serialize(regions));
        // Preserve the source annotations alongside traces, without editing the originals.
        File.Copy(annotations, Path.Combine(directory, name + ".annotations.json"), true);
        return new StreamWriter(new GZipStream(File.Create(Path.Combine(directory, name + ".jsonl.gz")), CompressionLevel.SmallestSize));
    }

    internal sealed record CropFixture(int Top, int Height, int Bottom, AbilitySignal Expected);

    internal static void SaveCrop(string recording, int frame, int slot, IntPtr buffer, int stride,
        HudReplay.SlotArea area, AbilitySignal expected)
    {
        var (region, top, bottom) = (area.Region, area.Top, area.Bottom);
        var id = Path.GetFileNameWithoutExtension(recording)[^6..];
        if (!(id switch
        {
            "123051" => (slot, frame) is (1, 121) or (1, 3500) or (2, 4495) or (1, 7618),
            "124028" => (slot, frame) is (0, 349) or (0, 3942),
            // Start bars seen through a bright effect and over bright snow: a fixed contrast threshold misses both.
            "153154" => (slot, frame) is (2, 3748),
            "162021" => (slot, frame) is (0, 7900),
            _ => false,
        })) return;
        var path = Path.Combine("tests", "fixtures", "hud-sequences",
            $"{Path.GetFileNameWithoutExtension(recording)}-{slot}-{frame}");
        using var source = new Bitmap(stride / 4, region.Bottom + bottom, stride, PixelFormat.Format32bppRgb, buffer);
        using var crop = source.Clone(new Rectangle(region.X, region.Y - top, region.Width, region.Height + top + bottom), PixelFormat.Format32bppRgb);
        crop.Save(path + ".png", ImageFormat.Png);
        File.WriteAllText(path + ".json", JsonSerializer.Serialize(new CropFixture(top, region.Height, bottom, expected), Options));
    }

    private static void CheckCrops(string directory)
    {
        var paths = Directory.GetFiles(directory, "*.png");
        if (paths.Length != 8) throw new InvalidOperationException("Expected eight HUD pixel crops.");
        foreach (var path in paths)
        {
            var fixture = JsonSerializer.Deserialize<CropFixture>(File.ReadAllText(Path.ChangeExtension(path, ".json")), Options)!;
            using var image = new Bitmap(path);
            var data = image.LockBits(new Rectangle(0, 0, image.Width, image.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
            try
            {
                var actual = AbilitySignalDetector.Read(data.Scan0, data.Stride, 0, fixture.Top, image.Width, fixture.Height, fixture.Bottom, topPadding: fixture.Top);
                if (Math.Abs(actual.BottomBorderThickness - fixture.Expected.BottomBorderThickness) > 0.00001f ||
                    Math.Abs(actual.FillLuminance!.Value - fixture.Expected.FillLuminance!.Value) > 0.00001f ||
                    Math.Abs(actual.FillSaturation!.Value - fixture.Expected.FillSaturation!.Value) > 0.00001f ||
                    actual.Charged != fixture.Expected.Charged || actual.UseFlash != fixture.Expected.UseFlash ||
                    Math.Abs(actual.BottomBarOpacity!.Value - fixture.Expected.BottomBarOpacity!.Value) > 0.00001f)
                    throw new InvalidOperationException($"HUD pixel evidence changed: {Path.GetFileName(path)}.");
                if ((path.EndsWith("153154-2-3748.png", StringComparison.Ordinal) || path.EndsWith("162021-0-7900.png", StringComparison.Ordinal)) &&
                    actual.BottomBarOpacity < 0.5f)
                    throw new InvalidOperationException($"Start bar not seen against a bright background: {Path.GetFileName(path)}.");
            }
            finally { image.UnlockBits(data); }
        }
    }

    private static List<Sample> Load(string directory, string name)
    {
        using var reader = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(directory, name + ".jsonl.gz")), CompressionMode.Decompress));
        var samples = new List<Sample>();
        while (reader.ReadLine() is { } line)
        {
            var sample = JsonSerializer.Deserialize<Sample>(line, Options)!;
            if (sample.Frame != samples.Count / 3 + 1 || sample.Slot != samples.Count % 3)
                throw new InvalidDataException("HUD trace frames must be contiguous and contain all three slots.");
            samples.Add(sample);
        }

        return samples;
    }

    /// <summary>A solid wrong-colour fill that reads as charged: what a stale or invalid startup frame can look like.</summary>
    private static AbilitySignal GarbageSignal() => new(true, false, 0.9f, 0, 0, null, 300, Color.FromArgb(255, 60, 200),
        PixelEvidence: true, FillLuminance: 0.5f, FillSaturation: 1f, BottomBarOpacity: 0);

    /// <summary>Replays a trace from <paramref name="firstFrame"/>, optionally after garbage frames; returns start/end frames per slot.</summary>
    private static (List<int>[] Starts, List<int>[] Ends) Simulate(List<Sample> samples, int firstFrame = 1, int garbage = 0, bool report = false)
    {
        var trackers = Enum.GetValues<AbilityKind>().Select(kind => new AbilitySlotTracker(kind)).ToArray();
        var times = samples.Where(sample => sample.Slot == 0).Select(sample => sample.Time).ToArray();
        var starts = new List<int>[] { [], [], [] };
        var ends = new List<int>[] { [], [], [] };
        var open = new bool[3];
        int Frame(DateTime time)
        {
            var index = Array.BinarySearch(times, time);
            if (index >= 0) { return index + 1; }
            index = ~index;
            if (index == 0) { return 1; }
            if (index == times.Length) { return times.Length; }
            return time - times[index - 1] <= times[index] - time ? index : index + 1;
        }

        foreach (var sample in samples.Where(sample => sample.Frame >= firstFrame))
        {
            var slot = sample.Slot;
            var tracker = trackers[slot];
            var signal = sample.Frame < firstFrame + garbage ? GarbageSignal() : sample.Signal;
            var before = tracker.State;
            var completed = tracker.Observe(signal, sample.Time);
            if (before != AbilityState.Recharging && tracker.State == AbilityState.Recharging && tracker.Estimating)
            {
                open[slot] = false; // picked up mid-recharge: an estimate, scored separately (--sweep-hud)
            }
            else if (before != AbilityState.Recharging && tracker.State == AbilityState.Recharging)
            {
                starts[slot].Add(sample.Frame);
                open[slot] = true;
                if (report) Console.WriteLine($"initial {slot} {sample.Frame}");
            }
            else if (before == AbilityState.Recharging && tracker.State != AbilityState.Recharging && !completed)
            {
                open[slot] = false;
            }

            if (completed && open[slot] && tracker.LastStartedAt is { } started && tracker.LastDuration is { } duration)
            {
                starts[slot][^1] = Frame(started);
                ends[slot].Add(Frame(started + duration));
                open[slot] = false;
            }

            if (completed && tracker.State == AbilityState.Recharging)
            {
                starts[slot].Add(sample.Frame); // a recast opened the next recharge
                open[slot] = true;
            }

            if (!completed && open[slot] && tracker.CurrentStartedAt is { } current) { starts[slot][^1] = Frame(current); }
        }

        return (starts, ends);
    }

    private static readonly string[] Names = ["grenade", "melee", "class"];

    /// <summary>Annotated (start, end) frames per slot for cycles starting at or after <paramref name="from"/>.</summary>
    private static List<(int Start, int? End)>[] Annotated(JsonDocument annotations, int from)
    {
        var cycles = new List<(int Start, int? End)>[] { [], [], [] };
        foreach (var item in annotations.RootElement.GetProperty("cycles").EnumerateArray())
        {
            var start = item.GetProperty("startFrame").GetInt32();
            if (start < from) continue;
            var end = item.GetProperty("endFrame");
            cycles[Array.IndexOf(Names, item.GetProperty("slot").GetString())].Add(
                (start, end.ValueKind == JsonValueKind.Number ? end.GetInt32() : null));
        }

        foreach (var list in cycles) list.Sort();
        return cycles;
    }

    // The largest start or completion error the user accepts, in seconds of capture time (frames are not evenly
    // spaced: about 45 FPS on average with gaps up to 0.1 s); ±2 frames of the recording's own index is the target.
    private const double CeilingSeconds = 0.1;
    private const int TargetFrames = 2;

    /// <summary>
    /// Every annotated cycle from <paramref name="from"/> on must be found once (no false or missed starts or
    /// completions), each endpoint within the 0.1 s ceiling. Returns endpoints beyond the ±2-frame target.
    /// </summary>
    private static List<string> Score(string name, List<Sample> samples, JsonDocument annotations, int from, int garbage)
    {
        var (starts, ends) = Simulate(samples, from, garbage);
        var times = samples.Where(sample => sample.Slot == 0).Select(sample => sample.Time).ToArray();
        var expected = Annotated(annotations, from + garbage);
        var label = $"{name} startup at frame {from} after {garbage} garbage frames";
        var beyondTarget = new List<string>();
        for (var slot = 0; slot < 3; slot++)
        {
            var expectedEnds = expected[slot].Where(cycle => cycle.End is not null).ToList();
            // A cycle starting within 3 s of a mid-recording startup may have been used before it; skipping it is right.
            if (from > 1 && expected[slot].Count > 0 && starts[slot].Count == expected[slot].Count - 1 &&
                (times[expected[slot][0].Start - 1] - times[from + garbage - 1]).TotalSeconds < HudReplay.UnmeasurableSeconds &&
                (starts[slot].Count == 0 || Math.Abs(starts[slot][0] - expected[slot][0].Start) > 60))
            {
                expected[slot].RemoveAt(0);
                expectedEnds = expected[slot].Where(cycle => cycle.End is not null).ToList();
            }

            if (starts[slot].Count != expected[slot].Count || ends[slot].Count != expectedEnds.Count)
                throw new InvalidOperationException($"{label}, {Names[slot]}: {starts[slot].Count} starts and {ends[slot].Count} " +
                    $"completions, annotated {expected[slot].Count} and {expectedEnds.Count} (starts {string.Join(',', starts[slot])}).");
            void Check(string what, int cycle, int actual, int annotated)
            {
                var seconds = Math.Abs((times[actual - 1] - times[annotated - 1]).TotalSeconds);
                if (seconds > CeilingSeconds)
                    throw new InvalidOperationException($"{label}, {Names[slot]} cycle {cycle} {what}: {actual}, annotated {annotated} ({seconds:F3} s, over 0.1 s).");
                if (Math.Abs(actual - annotated) > TargetFrames) beyondTarget.Add($"{Names[slot]}{cycle}-{what}");
            }

            for (var i = 0; i < expected[slot].Count; i++) Check("start", i + 1, starts[slot][i], expected[slot][i].Start);
            for (var i = 0; i < expectedEnds.Count; i++)
                Check("end", expected[slot].IndexOf(expectedEnds[i]) + 1, ends[slot][i], expectedEnds[i].End!.Value);
        }

        return beyondTarget;
    }

    internal static void Run(bool report = false)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-sequences");
        if (!report) CheckCrops(directory);
        foreach (var name in new[] { "D2Ability-HUD-20261107-123051", "D2Ability-HUD-20261107-124028",
            "D2Ability-HUD-20261107-153154", "D2Ability-HUD-20261107-162021" })
        {
            var samples = Load(directory, name);
            using var annotations = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name + ".annotations.json")));
            if (report)
            {
                var (starts, ends) = Simulate(samples, report: true);
                Console.WriteLine(name);
                for (var slot = 0; slot < 3; slot++)
                    Console.WriteLine($"slot {slot}: starts {string.Join(',', starts[slot])}; ends {string.Join(',', ends[slot])}");
                var lastFrame = samples[^1].Frame;
                for (var from = 1; from < lastFrame - 600; from += 300)
                    foreach (var garbage in new[] { 0, 10 })
                        try { Score(name, samples, annotations, from, garbage); }
                        catch (InvalidOperationException failure) { Console.WriteLine(failure.Message); }
                continue;
            }

            // Full replay: every annotated start and completion, ±2 frames except the sword cycle's completion
            // (123051 melee 2: the sword replaced the melee icon), and all within 0.1 s.
            var beyond = Score(name, samples, annotations, 1, 0);
            var allowed = name.EndsWith("123051", StringComparison.Ordinal) ? new[] { "melee2-end" } : [];
            if (beyond.Except(allowed).Any())
                throw new InvalidOperationException($"{name}: endpoints beyond ±2 frames: {string.Join(' ', beyond)}.");

            Console.WriteLine($"HUD sequence checks passed: {name}.");
        }
    }
}

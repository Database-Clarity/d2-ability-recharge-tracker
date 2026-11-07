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

    internal static StreamWriter CreateWriter(string recording, string annotations, string settingsPath)
    {
        var directory = Path.Combine("tests", "fixtures", "hud-sequences");
        Directory.CreateDirectory(directory);
        var name = Path.GetFileNameWithoutExtension(recording);
        using var settings = JsonDocument.Parse(File.ReadAllText(settingsPath));
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
        Rectangle region, int top, int bottom, AbilitySignal expected)
    {
        var first = Path.GetFileNameWithoutExtension(recording).EndsWith("123051", StringComparison.Ordinal);
        if (!(first ? (slot, frame) is (1, 121) or (1, 3500) or (2, 4495) or (1, 7618)
            : (slot, frame) is (0, 349) or (0, 3942))) return;
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
        if (paths.Length != 6) throw new InvalidOperationException("Expected six HUD pixel crops.");
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
                    actual.Charged != fixture.Expected.Charged || actual.UseFlash != fixture.Expected.UseFlash)
                    throw new InvalidOperationException($"HUD pixel evidence changed: {Path.GetFileName(path)}.");
            }
            finally { image.UnlockBits(data); }
        }
    }

    internal static void Run(bool report = false)
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "fixtures", "hud-sequences");
        if (!report) CheckCrops(directory);
        foreach (var name in new[] { "D2Ability-HUD-20261107-123051", "D2Ability-HUD-20261107-124028" })
        {
            using var reader = new StreamReader(new GZipStream(File.OpenRead(Path.Combine(directory, name + ".jsonl.gz")), CompressionMode.Decompress));
            var trackers = Enum.GetValues<AbilityKind>().Select(kind => new AbilitySlotTracker(kind)).ToArray();
            var times = new List<DateTime>();
            var starts = new List<DateTime>[] { [], [], [] };
            var ends = new List<DateTime>[] { [], [], [] };
            var previousElapsed = new TimeSpan[3];
            var samples = 0;
            while (reader.ReadLine() is { } line)
            {
                var sample = JsonSerializer.Deserialize<Sample>(line, Options)!;
                if (sample.Frame != samples / 3 + 1 || sample.Slot != samples % 3)
                    throw new InvalidDataException("HUD trace frames must be contiguous and contain all three slots.");
                samples++;
                if (sample.Slot == 0) times.Add(sample.Time);
                var slot = sample.Slot;
                var tracker = trackers[slot];
                var before = tracker.State;
                var completed = tracker.Observe(sample.Signal, sample.Time);
                if (before != AbilityState.Recharging && tracker.State == AbilityState.Recharging)
                {
                    starts[slot].Add(sample.Time);
                    if (report) Console.WriteLine($"initial {slot} {sample.Frame}");
                }
                if (completed)
                {
                    var start = starts[slot][^1];
                    ends[slot].Add(start + tracker.LastDuration!.Value - tracker.LastStartShift);
                    starts[slot][^1] -= tracker.LastStartShift;
                    if (tracker.State == AbilityState.Recharging)
                        starts[slot].Add(sample.Time - tracker.CurrentDuration!.Value);
                }
                else if (tracker.State == AbilityState.Recharging && tracker.CurrentDuration is { } elapsed &&
                    elapsed < previousElapsed[slot] - TimeSpan.FromSeconds(0.03))
                {
                    starts[slot][^1] = sample.Time - elapsed;
                    if (report) Console.WriteLine($"pulse reset {slot} {sample.Frame}");
                }
                previousElapsed[slot] = tracker.CurrentDuration ?? TimeSpan.Zero;
            }

            int Frame(DateTime time) => Enumerable.Range(0, times.Count).MinBy(i => Math.Abs((times[i] - time).Ticks)) + 1;
            using var annotations = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, name + ".annotations.json")));
            void Check(int slot, bool end, int cycle)
            {
                string[] names = ["grenade", "melee", "class"];
                var annotated = annotations.RootElement.GetProperty("cycles").EnumerateArray()
                    .Where(item => item.GetProperty("slot").GetString() == names[slot]).ElementAt(cycle - 1);
                var expected = annotated.GetProperty(end ? "endFrame" : "startFrame").GetInt32();
                var actual = Frame((end ? ends : starts)[slot][cycle - 1]);
                if (Math.Abs(actual - expected) > 2)
                    throw new InvalidOperationException($"{name} slot {slot} cycle {cycle} {(end ? "end" : "start")}: {actual}, annotated {expected} (tolerance 2 frames).");
            }
            if (report)
            {
                Console.WriteLine(name);
                for (var slot = 0; slot < 3; slot++)
                    Console.WriteLine($"slot {slot}: starts {string.Join(',', starts[slot].Select(Frame))}; ends {string.Join(',', ends[slot].Select(Frame))}");
                continue;
            }
            if (name.EndsWith("123051", StringComparison.Ordinal))
            {
                if (!starts.Select(list => list.Count).SequenceEqual(new[] { 2, 4, 3 }) ||
                    !ends.Select(list => list.Count).SequenceEqual(new[] { 1, 3, 2 }))
                    throw new InvalidOperationException("First recording: false or missed start/completion (including completion animation recast).");
                Check(0, false, 1); // normal start
                Check(1, false, 1); // normal bar
                Check(1, true, 1); // gray bar disappearance
                Check(1, false, 2); // sword cycle start, no tuning to its completion
                Check(1, true, 3); // quick recast closes the old cycle
                Check(1, false, 4);
                Check(2, true, 1); // completion animation must not become a recast
            }
            else
            {
                if (!starts.Select(list => list.Count).SequenceEqual(new[] { 2, 2, 3 }) ||
                    !ends.Select(list => list.Count).SequenceEqual(new[] { 1, 1, 2 }))
                    throw new InvalidOperationException("Second recording: false or missed start/completion.");
                Check(0, false, 1);
                Check(1, false, 1);
                Check(1, false, 2); // use pulse, then true bar
                Check(2, false, 3); // another pulse/bar sequence
                Check(0, true, 1);
                Check(1, true, 1);
                Check(2, true, 1);
                Check(2, true, 2);
            }
            Console.WriteLine($"HUD sequence checks passed: {name}.");
        }
    }
}

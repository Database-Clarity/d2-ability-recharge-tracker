using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using D2AbilityTracker;

internal static class FootageReplay
{
    public static void Run(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("Usage: --replay <581x182 60 FPS cropped video> <output.csv>");
        }

        const int width = 581;
        const int height = 182;
        var regions = new[] { new Rectangle(109, 83, 62, 63), new Rectangle(176, 78, 62, 63), new Rectangle(243, 73, 61, 63) };
        var trackers = Enum.GetValues<AbilityKind>().Select(kind => new AbilitySlotTracker(kind)).ToArray();
        var uses = new int[3];
        var starts = Enumerable.Range(0, 3).Select(_ => new List<double>()).ToArray();
        var durations = Enumerable.Range(0, 3).Select(_ => new List<double>()).ToArray();
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in new[] { "-v", "error", "-i", args[1], "-map", "0:v:0", "-f", "rawvideo", "-pix_fmt", "bgra", "-" })
        {
            info.ArgumentList.Add(argument);
        }

        using var decoder = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg.");
        var errors = decoder.StandardError.ReadToEndAsync();
        using var output = new StreamWriter(args[2]);
        output.WriteLine("seconds,ability,charged,line,fill,contrast,border,progress,state,elapsed,completed,warning");
        var data = new byte[width * height * 4];
        var buffer = Marshal.AllocHGlobal(data.Length);
        try
        {
            var frame = 0;
            while (true)
            {
                var read = 0;
                while (read < data.Length)
                {
                    var count = decoder.StandardOutput.BaseStream.Read(data, read, data.Length - read);
                    if (count == 0) { break; }
                    read += count;
                }

                if (read == 0) { break; }
                if (read != data.Length) { throw new InvalidDataException("Incomplete frame; expected 581x182 video."); }
                Marshal.Copy(data, 0, buffer, data.Length);
                var time = DateTime.UnixEpoch.AddSeconds(frame / 60d);
                for (var slot = 0; slot < regions.Length; slot++)
                {
                    var region = regions[slot];
                    var signal = AbilitySignalDetector.Read(buffer, width * 4, region.X, region.Y,
                        region.Width, region.Height, AbilitySignalDetector.BottomPadding(region.Height),
                        trackers[slot].ChargeProgress, trackers[slot].ProgressSearchTolerance(time));
                    var tracker = trackers[slot];
                    var previous = tracker.State;
                    var completed = tracker.Observe(signal, time);
                    if (previous != AbilityState.Recharging && tracker.State == AbilityState.Recharging)
                    {
                        uses[slot]++;
                        starts[slot].Add(frame / 60d);
                    }
                    if (completed) { durations[slot].Add(tracker.LastDuration!.Value.TotalSeconds); }
                    output.WriteLine(FormattableString.Invariant(
                        $"{frame / 60d:F4},{tracker.Kind},{signal.Charged},{signal.CooldownLine},{signal.ChargedBackgroundRatio:F4},{signal.CooldownLineRatio:F4},{signal.BottomBorderThickness:F4},{signal.ChargeProgress:F4},{tracker.State},{tracker.CurrentDuration?.TotalSeconds:F4},{completed},{tracker.TrackingWarning}"));
                    if (completed || previous != tracker.State && tracker.State == AbilityState.Recharging)
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"{frame / 60d:F3}s {tracker.Kind}: {previous} -> {tracker.State}, measured {tracker.LastDuration?.TotalSeconds:F3}s"));
                    }
                }

                frame++;
            }

            decoder.WaitForExit();
            if (decoder.ExitCode != 0) { throw new InvalidOperationException(errors.GetAwaiter().GetResult()); }
            if (frame != 9982 || !uses.SequenceEqual(new[] { 1, 2, 2 }) ||
                !durations.Select(values => values.Count).SequenceEqual(new[] { 1, 2, 1 }) ||
                trackers[2].State != AbilityState.Recharging || trackers.Any(tracker => tracker.MeasurementDiscarded))
            {
                throw new InvalidOperationException("Reference footage must produce one grenade, two melee and one class completion, with the second class cycle still charging.");
            }

            var expected = new[] { new[] { 160.066 }, new[] { 49.650, 49.667 }, new[] { 90.600 } };
            var expectedStarts = new[] { new[] { 2.966667 }, new[] { 5.183333, 109.150 }, new[] { 8.700, 113.416667 } };
            for (var slot = 0; slot < expected.Length; slot++)
            {
                for (var cycle = 0; cycle < expectedStarts[slot].Length; cycle++)
                {
                    if (Math.Abs(starts[slot][cycle] - expectedStarts[slot][cycle]) > 1d / 60)
                    {
                        throw new InvalidOperationException($"Reference recharge-start pulse shifted for {trackers[slot].Kind} cycle {cycle + 1}.");
                    }
                }

                for (var cycle = 0; cycle < expected[slot].Length; cycle++)
                {
                    if (Math.Abs(durations[slot][cycle] - expected[slot][cycle]) > 1d / 60)
                    {
                        throw new InvalidOperationException($"Reference endpoint shifted for {trackers[slot].Kind} cycle {cycle + 1}.");
                    }
                }
            }

            Console.WriteLine($"Replayed {frame} frames.");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (!decoder.HasExited) { decoder.Kill(); }
        }
    }
}

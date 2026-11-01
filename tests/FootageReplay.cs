using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using D2AbilityTracker;

internal static class FootageReplay
{
    private static readonly Rectangle[] Regions =
        [new Rectangle(109, 83, 62, 63), new Rectangle(176, 78, 62, 63), new Rectangle(243, 73, 61, 63)];
    private static readonly double[][] ExpectedDurations = [[160.066], [49.650, 49.667], [90.600]];
    private static readonly double[][] ExpectedStarts = [[2.966667], [5.183333, 109.150], [8.700, 113.416667]];
    private static readonly double[] EstimateCheckpoints = [0.2, 0.35, 0.5, 0.75, 0.9];

    // Usage: --replay <581x182 60 FPS cropped video> <output.csv>
    // Replays the footage at 60 Hz (frame-exact) and at lower, jittered capture rates with dropped frames.
    public static void Run(string[] args)
    {
        if (args.Length != 3)
        {
            throw new ArgumentException("Usage: --replay <581x182 60 FPS cropped video> <output.csv>");
        }

        var simulations = new List<Simulation>
        {
            new(60, 0, 0, 0, args[2]),
            // Mid-cycle obstruction (melee), obscured endpoint (grenade) and obscured recharge start (class).
            new(60, -1, 0, 0, Path.ChangeExtension(args[2], ".occluded.csv"), [new(1, 25, 30), new(0, 161.5, 164), new(2, 8.3, 12)])
        };
        foreach (var rate in new[] { 30d, 20d, 15d })
        {
            for (var seed = 1; seed <= 4; seed++)
            {
                simulations.Add(new Simulation(rate, seed, 0.35, 0.03, null));
            }
        }

        const int width = 581;
        const int height = 182;
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
        var data = new byte[width * height * 4];
        var buffer = Marshal.AllocHGlobal(data.Length);
        var frame = 0;
        try
        {
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
                foreach (var simulation in simulations)
                {
                    simulation.Process(buffer, width * 4, frame);
                }

                frame++;
            }

            decoder.WaitForExit();
            if (decoder.ExitCode != 0) { throw new InvalidOperationException(errors.GetAwaiter().GetResult()); }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
            if (!decoder.HasExited) { decoder.Kill(); }
            foreach (var simulation in simulations) { simulation.Dispose(); }
        }

        if (frame != 9982)
        {
            throw new InvalidOperationException($"Expected 9982 frames, decoded {frame}.");
        }

        var failures = new List<string>();
        foreach (var simulation in simulations)
        {
            simulation.Verify(failures);
        }

        Console.WriteLine($"Replayed {frame} frames across {simulations.Count} capture simulations.");
        if (failures.Count > 0)
        {
            throw new InvalidOperationException(string.Join(Environment.NewLine, failures));
        }
    }

    private sealed class Simulation : IDisposable
    {
        private readonly double _rate;
        private readonly int _seed;
        private readonly double _dropRate;
        private readonly Random _random;
        private readonly StreamWriter? _output;
        private readonly AbilitySlotTracker[] _trackers =
            Enum.GetValues<AbilityKind>().Select(kind => new AbilitySlotTracker(kind)).ToArray();
        private readonly List<double>[] _starts = [[], [], []];
        private readonly List<double>[] _durations = [[], [], []];
        private readonly List<(double Fraction, double Estimate, double Spread)>[][] _estimates =
            [[[], []], [[], []], [[], []]];
        private readonly int[] _nextCheckpoint = new int[3];
        private readonly double _jitter;
        private double _nextCapture;
        private readonly Occlusion[] _occlusions;
        private readonly double?[] _recoveredAfter = new double?[3];
        private readonly List<MeasurementSource?>[] _sources = [[], [], []];
        private byte[]? _frameCopy;
        private IntPtr _occludedBuffer;

        public Simulation(double rate, int seed, double jitter, double dropRate, string? csv, Occlusion[]? occlusions = null)
        {
            _rate = rate;
            _seed = seed;
            _jitter = jitter;
            _dropRate = dropRate;
            _random = new Random(seed);
            _occlusions = occlusions ?? [];
            _nextCapture = seed <= 0 ? 0 : _random.NextDouble() / rate;
            if (csv is not null)
            {
                _output = new StreamWriter(csv);
                _output.WriteLine("seconds,ability,charged,flash,blank,lum,line,border,progress,display,state,elapsed,estimate,spread,completed,warning");
            }
        }

        private string Name => _seed == 0 ? "60 Hz exact" : _seed < 0 ? "60 Hz occluded" : $"{_rate:F0} Hz seed {_seed}";

        public void Process(IntPtr buffer, int stride, int frame)
        {
            var time = frame / 60d;
            if (_seed > 0)
            {
                if (time + 1e-9 < _nextCapture) { return; }
                var period = 1 / _rate;
                _nextCapture += period * (1 + _jitter * (2 * _random.NextDouble() - 1));
                if (_nextCapture <= time) { _nextCapture = time + period * 0.5; }
                if (_random.NextDouble() < _dropRate) { return; }
            }

            var timestamp = DateTime.UnixEpoch.AddSeconds(time);
            var active = _occlusions.Where(occlusion => time >= occlusion.From && time < occlusion.To).ToArray();
            if (active.Length > 0)
            {
                _frameCopy ??= new byte[stride * 182];
                if (_occludedBuffer == IntPtr.Zero) { _occludedBuffer = Marshal.AllocHGlobal(_frameCopy.Length); }
                Marshal.Copy(buffer, _frameCopy, 0, _frameCopy.Length);
                foreach (var occlusion in active)
                {
                    var region = Regions[occlusion.Slot];
                    for (var y = Math.Max(0, region.Top - 12); y < Math.Min(182, region.Bottom + 12); y++)
                    {
                        for (var x = region.Left; x < region.Right; x++)
                        {
                            var index = y * stride + x * 4;
                            _frameCopy[index] = _frameCopy[index + 1] = _frameCopy[index + 2] = 38;
                        }
                    }
                }

                Marshal.Copy(_frameCopy, 0, _occludedBuffer, _frameCopy.Length);
                buffer = _occludedBuffer;
            }
            for (var slot = 0; slot < Regions.Length; slot++)
            {
                var region = Regions[slot];
                var tracker = _trackers[slot];
                var signal = AbilitySignalDetector.Read(buffer, stride, region.X, region.Y,
                    region.Width, region.Height, AbilitySignalDetector.BottomPadding(region.Height),
                    tracker.ChargeProgress, tracker.ProgressSearchTolerance(timestamp),
                    AbilitySignalDetector.TopPadding(region.Height), tracker.Guide(timestamp));
                var previous = tracker.State;
                var completed = tracker.Observe(signal, timestamp);
                if (previous != AbilityState.Recharging && tracker.State == AbilityState.Recharging)
                {
                    _starts[slot].Add(time);
                    _nextCheckpoint[slot] = 0;
                }

                var cycle = _starts[slot].Count - 1;
                if (tracker.State == AbilityState.Recharging && cycle >= 0 && cycle < ExpectedDurations[slot].Length &&
                    _nextCheckpoint[slot] < EstimateCheckpoints.Length && tracker.CurrentDuration is { } elapsed &&
                    elapsed.TotalSeconds >= EstimateCheckpoints[_nextCheckpoint[slot]] * ExpectedDurations[slot][cycle])
                {
                    _estimates[slot][cycle].Add((EstimateCheckpoints[_nextCheckpoint[slot]],
                        tracker.EstimatedDuration?.TotalSeconds ?? double.NaN,
                        tracker.EstimateUncertainty?.TotalSeconds ?? double.NaN));
                    _nextCheckpoint[slot]++;
                }

                if (completed)
                {
                    _durations[slot].Add(tracker.LastDuration!.Value.TotalSeconds);
                    _sources[slot].Add(tracker.LastSource);
                    if (_seed == 0 || _seed == 1)
                    {
                        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"[{Name}] {time:F3}s {tracker.Kind}: measured {tracker.LastDuration?.TotalSeconds:F3}s ± {tracker.LastUncertainty?.TotalSeconds:F3} ({tracker.LastSource})"));
                    }
                }

                if (_occlusions.FirstOrDefault(occlusion => occlusion.Slot == slot) is { } cleared &&
                    time >= cleared.To && _recoveredAfter[slot] is null &&
                    tracker.State == AbilityState.Recharging && tracker.TrackingWarning is null)
                {
                    _recoveredAfter[slot] = time - cleared.To;
                }

                _output?.WriteLine(FormattableString.Invariant(
                    $"{time:F4},{tracker.Kind},{signal.Charged},{signal.UseFlash},{signal.Blank},{signal.FillLuminance:F4},{signal.CooldownLine},{signal.BottomBorderThickness:F4},{signal.ChargeProgress:F4},{tracker.DisplayProgress:F4},{tracker.State},{tracker.CurrentDuration?.TotalSeconds:F4},{tracker.EstimatedDuration?.TotalSeconds:F3},{tracker.EstimateUncertainty?.TotalSeconds:F3},{completed},{tracker.TrackingWarning}"));
            }
        }

        public void Verify(List<string> failures)
        {
            if (_occlusions.Length > 0)
            {
                VerifyOccluded(failures);
                return;
            }

            var frameTolerance = _seed == 0 ? 1d / 60 + 1e-6 : 1.5 / _rate;
            for (var slot = 0; slot < Regions.Length; slot++)
            {
                var kind = _trackers[slot].Kind;
                if (_starts[slot].Count != ExpectedStarts[slot].Length ||
                    _durations[slot].Count != ExpectedDurations[slot].Length)
                {
                    failures.Add($"[{Name}] {kind}: {_starts[slot].Count} starts and {_durations[slot].Count} completions.");
                    continue;
                }

                for (var cycle = 0; cycle < ExpectedStarts[slot].Length; cycle++)
                {
                    if (Math.Abs(_starts[slot][cycle] - ExpectedStarts[slot][cycle]) > frameTolerance)
                    {
                        failures.Add($"[{Name}] {kind} cycle {cycle + 1}: start {_starts[slot][cycle]:F3}s.");
                    }
                }

                for (var cycle = 0; cycle < ExpectedDurations[slot].Length; cycle++)
                {
                    var error = _durations[slot][cycle] - ExpectedDurations[slot][cycle];
                    var estimates = string.Join(" ", _estimates[slot][cycle].Select(entry => string.Create(
                        CultureInfo.InvariantCulture, $"{entry.Fraction:P0}:{entry.Estimate - ExpectedDurations[slot][cycle]:+0.000;-0.000}±{entry.Spread:F3}")));
                    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"[{Name}] {kind} cycle {cycle + 1}: error {error * 1000:+0;-0} ms; estimate errors {estimates}"));
                    if (Math.Abs(error) > frameTolerance)
                    {
                        failures.Add($"[{Name}] {kind} cycle {cycle + 1}: measured {_durations[slot][cycle]:F3}s.");
                    }

                    foreach (var entry in _estimates[slot][cycle].Where(entry => entry.Fraction >= 0.35))
                    {
                        var estimateError = Math.Abs(entry.Estimate - ExpectedDurations[slot][cycle]);
                        if (!(estimateError <= 0.01 * ExpectedDurations[slot][cycle] + 0.1) ||
                            !(estimateError <= 3 * entry.Spread + 0.05))
                        {
                            failures.Add(string.Create(CultureInfo.InvariantCulture,
                                $"[{Name}] {kind} cycle {cycle + 1}: estimate at {entry.Fraction:P0} was {entry.Estimate:F3}s ± {entry.Spread:F3}."));
                        }
                    }
                }
            }

            if (_trackers[2].State != AbilityState.Recharging || _trackers.Any(tracker => tracker.MeasurementDiscarded))
            {
                failures.Add($"[{Name}] the second class cycle must still be charging with nothing discarded.");
            }
        }

        private void VerifyOccluded(List<string> failures)
        {
            static string Join(IEnumerable<double> values) =>
                string.Join('/', values.Select(value => value.ToString("F3", CultureInfo.InvariantCulture)));
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"[{Name}] grenade {Join(_durations[0])} ({string.Join('/', _sources[0])}), melee {Join(_durations[1])} recovered after {_recoveredAfter[1]:F3}s, class {Join(_durations[2])} ({string.Join('/', _sources[2])})"));
            if (_durations[0].Count != 1 || _sources[0][0] != MeasurementSource.Model ||
                Math.Abs(_durations[0][0] - ExpectedDurations[0][0]) > 0.5)
            {
                failures.Add($"[{Name}] grenade with an obscured endpoint should complete from the model within 0.5 s.");
            }

            if (_durations[1].Count != 2 ||
                _durations[1].Zip(ExpectedDurations[1]).Any(pair => Math.Abs(pair.First - pair.Second) > 1d / 60 + 1e-6) ||
                _recoveredAfter[1] is not <= 0.5)
            {
                failures.Add($"[{Name}] melee must recover within 0.5 s of a mid-cycle obstruction and stay frame-exact.");
            }

            if (_durations[2].Count != 1 || Math.Abs(_durations[2][0] - ExpectedDurations[2][0]) > 0.5)
            {
                failures.Add($"[{Name}] class with an obscured start should be corrected from the model within 0.5 s.");
            }
        }

        public void Dispose()
        {
            _output?.Dispose();
            if (_occludedBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_occludedBuffer); }
        }
    }
}

internal sealed record Occlusion(int Slot, double From, double To);

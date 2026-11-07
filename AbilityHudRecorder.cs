using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace D2AbilityTracker;

/// <summary>
/// Records the ability HUD as lossless PNG frames inside a ZIP. Frames are fed by the tracker's own capture
/// loop (so recording never competes with detection for captures) and encoded on a background thread.
/// </summary>
internal sealed class AbilityHudRecorder : IDisposable
{
    public const int FramesPerSecond = 60;
    public const long MinimumFreeDiskBytes = 1_000_000_000;
    private const int CaptureMargin = 16;
    private const int QueueCapacity = 240;
    private readonly FramePacer _pacer = new(Stopwatch.Frequency / FramesPerSecond);
    private readonly FramePacer _testPacer = new(TimeSpan.TicksPerSecond / FramesPerSecond);
    // GDI-only captures never report a fresh frame; stop waiting for one after this long.
    private static readonly TimeSpan FreshFrameWait = TimeSpan.FromSeconds(2);
    private readonly IReadOnlyList<AbilityHudRecordingSlot> _slots;
    private int _skippedStartupFrames;
    private readonly BlockingCollection<PendingFrame> _queue = new(QueueCapacity);
    private readonly Action<AbilityHudRecordingProgress>? _reportProgress;
    private readonly string _path;
    private readonly string _temporaryPath;
    private readonly Thread _encoder;
    private readonly TaskCompletionSource<AbilityHudRecordingResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Stopwatch _stopwatch = new();
    private DateTime? _firstFrame;
    private int _droppedFrames;
    private volatile string? _stopReason;

    public AbilityHudRecorder(Rectangle bounds, string deviceName, string path,
        Action<AbilityHudRecordingProgress>? reportProgress = null, IReadOnlyList<AbilityHudRecordingSlot>? slots = null)
    {
        ScreenCapture.ValidateBounds(bounds);
        Bounds = bounds;
        _slots = slots ?? [];
        DeviceName = deviceName;
        _path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        _temporaryPath = Path.Combine(Path.GetDirectoryName(_path)!, $".{Path.GetFileName(_path)}.{Guid.NewGuid():N}.tmp");
        _reportProgress = reportProgress;
        _encoder = new Thread(EncodeFrames) { IsBackground = true, Name = "HUD recording encoder", Priority = ThreadPriority.BelowNormal };
        _stopwatch.Start();
        _encoder.Start();
    }

    private readonly object _diagnosticsSync = new();
    private readonly Queue<(double Capture, double Detection, double Submit)> _timings = new();
    private readonly HashSet<string> _backends = [];
    private readonly HashSet<string> _fallbackReasons = [];
    private bool _highResolution;

    public void RecordDiagnostics(string backend, string? fallbackReason, bool highResolution,
        double captureMs, double detectionMs, double submitMs)
    {
        lock (_diagnosticsSync)
        {
            _backends.Add(backend);
            if (fallbackReason is not null) _fallbackReasons.Add(fallbackReason);
            _highResolution = highResolution;
            if (_timings.Count == 4096) _timings.Dequeue();
            _timings.Enqueue((captureMs, detectionMs, submitMs));
        }
    }

    private string Diagnostics
    {
        get
        {
            lock (_diagnosticsSync)
            {
                static string Percentiles(IEnumerable<double> values)
                {
                    var sorted = values.Order().ToArray();
                    return sorted.Length == 0 ? "no samples" : string.Join("/", new[] { 0.5, 0.95, 0.99 }
                        .Select(p => sorted[(int)((sorted.Length - 1) * p)].ToString("F3", CultureInfo.InvariantCulture)));
                }
                return $"Backends: {string.Join(", ", _backends)}; fallback reasons: {string.Join("; ", _fallbackReasons)}; " +
                    $"high-resolution timer: {_highResolution}; stage ms p50/p95/p99 (last 4096 ticks): " +
                    $"capture {Percentiles(_timings.Select(t => t.Capture))}, detection {Percentiles(_timings.Select(t => t.Detection))}, " +
                    $"submit {Percentiles(_timings.Select(t => t.Submit))}";
            }
        }
    }

    public Rectangle Bounds { get; }
    public string DeviceName { get; }
    public string OutputPath => _path;
    public Task<AbilityHudRecordingResult> Completion => _completion.Task;
    public bool IsAccepting => !_queue.IsAddingCompleted;

    public static string DefaultFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "D2AbilityTracker", "HUD recordings");

    public static string DefaultPath(DateTime localTime) =>
        System.IO.Path.Combine(DefaultFolder, $"D2Ability-HUD-{localTime:yyyyMMdd-HHmmss}.zip");

    public static Rectangle GetCaptureBounds(IEnumerable<AbilitySlotSettings> slots) =>
        GetCaptureBounds(slots
            .Where(slot => slot.Region is { Width: > 0, Height: > 0 } && slot.MonitorBounds is { Width: > 0, Height: > 0 })
            .Select(slot => (slot.Region!.Value, slot.MonitorBounds!.Value)));

    /// <summary>Returns the padded union of the given ability areas, which must all be on one display.</summary>
    public static Rectangle GetCaptureBounds(IEnumerable<(Rectangle Region, Rectangle MonitorBounds)> areas)
    {
        var selected = areas.ToArray();
        if (selected.Length == 0)
        {
            throw new InvalidOperationException("Select at least one tracked ability area before recording the HUD.");
        }

        var monitor = selected[0].MonitorBounds;
        if (selected.Any(area => area.MonitorBounds != monitor))
        {
            throw new InvalidOperationException("The tracked ability areas must be on the same display to record the HUD.");
        }

        var union = selected.Select(area => area.Region).Aggregate(Rectangle.Union);
        var padded = Rectangle.FromLTRB(union.Left - CaptureMargin, union.Top - CaptureMargin,
            union.Right + CaptureMargin, union.Bottom + CaptureMargin);
        var captureBounds = Rectangle.Intersect(padded, monitor);
        ScreenCapture.ValidateBounds(captureBounds);
        return captureBounds;
    }

    /// <summary>True when a frame captured at <paramref name="timestamp"/> keeps the recording at about 60 FPS.</summary>
    public bool IsDue(DateTime timestamp) => IsAccepting && _testPacer.IsDue(timestamp.Ticks);
    public bool IsDue() => IsAccepting && _pacer.IsDue(Stopwatch.GetTimestamp());

    /// <summary>
    /// Copies a captured frame and queues it for encoding; never blocks the capture loop. Until the first
    /// <paramref name="fresh"/> frame (a newly presented desktop frame), frames are skipped: a capture that just
    /// started can return a stale image (for example, a GDI copy of a game's flip-model swap chain), which would
    /// otherwise start the recording with frozen, invalid frames.
    /// </summary>
    public unsafe void Submit(IntPtr pixels, int stride, DateTime timestamp, string trackerState, bool fresh = true)
    {
        if (!IsAccepting)
        {
            return;
        }

        if (_firstFrame is null && !fresh && _stopwatch.Elapsed < FreshFrameWait)
        {
            _skippedStartupFrames++;
            return;
        }

        _firstFrame ??= timestamp;
        var rowBytes = Bounds.Width * 4;
        var buffer = ArrayPool<byte>.Shared.Rent(rowBytes * Bounds.Height);
        fixed (byte* destination = buffer)
        {
            for (var y = 0; y < Bounds.Height; y++)
            {
                Buffer.MemoryCopy((byte*)pixels + (long)y * stride, destination + (long)y * rowBytes, rowBytes, rowBytes);
            }
        }

        var frame = new PendingFrame(buffer, timestamp, timestamp - _firstFrame.Value, trackerState);
        try
        {
            if (!_queue.TryAdd(frame))
            {
                ArrayPool<byte>.Shared.Return(buffer);
                Interlocked.Increment(ref _droppedFrames);
            }
        }
        catch (InvalidOperationException)
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Stop(string? reason = null)
    {
        _stopReason ??= reason;
        try
        {
            _queue.CompleteAdding();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EncodeFrames()
    {
        var frameCount = 0;
        var index = new StringBuilder("frame,elapsed_ms,utc,grenade_state,grenade_progress,melee_state,melee_progress,class_state,class_progress\r\n");
        var lastProgress = TimeSpan.FromSeconds(-1);
        try
        {
            using (var file = new FileStream(_temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                var drive = new DriveInfo(System.IO.Path.GetPathRoot(_path)!);
                foreach (var frame in _queue.GetConsumingEnumerable())
                {
                    try
                    {
                        WriteFrame(archive, ++frameCount, frame.Pixels);
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame.Pixels);
                    }

                    index.Append(frameCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(frame.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                        .Append(frame.Timestamp.ToString("O", CultureInfo.InvariantCulture)).Append(',')
                        .Append(frame.TrackerState).Append("\r\n");
                    if (frame.Elapsed - lastProgress >= TimeSpan.FromSeconds(1))
                    {
                        lastProgress = frame.Elapsed;
                        _reportProgress?.Invoke(new AbilityHudRecordingProgress(frameCount, frame.Elapsed, file.Position, Volatile.Read(ref _droppedFrames)));
                        if (drive.IsReady && drive.AvailableFreeSpace < MinimumFreeDiskBytes)
                        {
                            Stop("Stopped because less than 1 GB of disk space is left");
                        }
                    }
                }

                var manifest = archive.CreateEntry("recording.txt", CompressionLevel.Fastest);
                using (var writer = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                {
                    writer.WriteLine("Lossless PNG frames captured from the ability HUD by the tracker's capture loop.");
                    writer.WriteLine($"Bounds: {Bounds.X},{Bounds.Y},{Bounds.Width},{Bounds.Height} screen pixels on {DeviceName}");
                    writer.WriteLine($"Target frame rate: {FramesPerSecond} FPS");
                    writer.WriteLine($"Frames: {frameCount}");
                    writer.WriteLine($"Frames dropped (encoder busy): {Volatile.Read(ref _droppedFrames)}");
                    writer.WriteLine($"Recorder pacing skips: {_pacer.Skipped + _testPacer.Skipped}");
                    writer.WriteLine($"Capture diagnostics: {Diagnostics}");
                    writer.WriteLine($"Stale startup frames skipped: {_skippedStartupFrames}");
                    writer.WriteLine($"App version: {UpdateChecker.CurrentVersion}");
                    writer.WriteLine("frames.csv lists each frame's capture time and the tracker's live state and progress.");
                    writer.WriteLine("regions.json lists each tracked slot's screen region and the padding the detector used.");
                }

                var regions = archive.CreateEntry("regions.json", CompressionLevel.Fastest);
                using (var stream = regions.Open())
                {
                    JsonSerializer.Serialize(stream, new
                    {
                        Slots = _slots.Select(slot => new
                        {
                            Kind = (int)slot.Kind,
                            Region = new { slot.Region.X, slot.Region.Y, slot.Region.Width, slot.Region.Height },
                            slot.TopPadding,
                            slot.BottomPadding,
                        }),
                    });
                }

                var indexEntry = archive.CreateEntry("frames.csv", CompressionLevel.Fastest);
                using (var writer = new StreamWriter(indexEntry.Open(), new UTF8Encoding(false)))
                {
                    writer.Write(index.ToString());
                }
            }

            File.Move(_temporaryPath, _path, overwrite: true);
            _completion.TrySetResult(new AbilityHudRecordingResult(frameCount, new FileInfo(_path).Length, _stopwatch.Elapsed,
                Volatile.Read(ref _droppedFrames), _stopReason, _path));
        }
        catch (Exception exception)
        {
            _queue.CompleteAdding();
            while (_queue.TryTake(out var pending))
            {
                ArrayPool<byte>.Shared.Return(pending.Pixels);
            }

            _completion.TrySetException(exception);
        }
        finally
        {
            try
            {
                if (File.Exists(_temporaryPath))
                {
                    File.Delete(_temporaryPath);
                }
            }
            catch (IOException)
            {
            }
        }
    }

    private void WriteFrame(ZipArchive archive, int frameNumber, byte[] pixels)
    {
        var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(Bounds.Width, Bounds.Height, Bounds.Width * 4, PixelFormat.Format32bppRgb, handle.AddrOfPinnedObject());
            var entry = archive.CreateEntry($"frames/{frameNumber:D6}.png", CompressionLevel.NoCompression);
            using var stream = entry.Open();
            using var png = new MemoryStream();
            bitmap.Save(png, ImageFormat.Png);
            png.Position = 0;
            png.CopyTo(stream);
        }
        finally
        {
            handle.Free();
        }
    }

    public void Dispose()
    {
        Stop();
        _encoder.Join();
        _queue.Dispose();
    }

    private readonly record struct PendingFrame(byte[] Pixels, DateTime Timestamp, TimeSpan Elapsed, string TrackerState);
}

/// <summary>A tracked slot's screen region and the padding the detector reads around it.</summary>
internal readonly record struct AbilityHudRecordingSlot(AbilityKind Kind, Rectangle Region, int TopPadding, int BottomPadding)
{
    /// <summary>The padding the live detector uses for <paramref name="region"/>, limited by its monitor.</summary>
    public static AbilityHudRecordingSlot For(AbilityKind kind, Rectangle region, Rectangle monitor) => new(kind, region,
        Math.Max(0, Math.Min(AbilitySignalDetector.TopPadding(region.Height), region.Top - monitor.Top)),
        Math.Max(0, Math.Min(AbilitySignalDetector.BottomPadding(region.Height), monitor.Bottom - region.Bottom)));
}

internal readonly record struct AbilityHudRecordingProgress(int Frames, TimeSpan Elapsed, long BytesWritten, int DroppedFrames);

internal readonly record struct AbilityHudRecordingResult(
    int Frames,
    long Bytes,
    TimeSpan Elapsed,
    int DroppedFrames,
    string? StopReason,
    string Path);

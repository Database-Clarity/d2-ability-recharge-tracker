using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO.Compression;
using System.Text;

namespace D2AbilityTracker;

internal static class AbilityHudRecorder
{
    public const long MaximumArchiveBytes = 50_000_000;
    private const int FramesPerSecond = 15;
    private const int MaximumFrames = FramesPerSecond * 60 * 10;
    private const long ArchiveOverheadReserve = 3_000_000;
    private const int CaptureMargin = 16;

    public static Rectangle GetCaptureBounds(IEnumerable<AbilitySlotSettings> slots)
    {
        var configuredSlots = slots
            .Where(slot => slot.Kind is AbilityKind.Grenade or AbilityKind.Melee or AbilityKind.ClassAbility)
            .OrderBy(slot => slot.Kind)
            .ToArray();
        if (configuredSlots.Length != 3 || configuredSlots.Any(slot =>
                slot.Region is not { Width: > 0, Height: > 0 } ||
                slot.MonitorBounds is not { Width: > 0, Height: > 0 } ||
                string.IsNullOrWhiteSpace(slot.MonitorDeviceName)))
        {
            throw new InvalidOperationException("Select all three ability areas before recording the HUD.");
        }

        var firstSlot = configuredSlots[0];
        if (configuredSlots.Any(slot =>
                !string.Equals(slot.MonitorDeviceName, firstSlot.MonitorDeviceName, StringComparison.OrdinalIgnoreCase) ||
                slot.MonitorBounds != firstSlot.MonitorBounds))
        {
            throw new InvalidOperationException("All three ability areas must be on the same display to record the HUD.");
        }

        var union = configuredSlots
            .Select(slot => slot.Region!.Value)
            .Aggregate(Rectangle.Union);
        var padded = Rectangle.FromLTRB(
            union.Left - CaptureMargin,
            union.Top - CaptureMargin,
            union.Right + CaptureMargin,
            union.Bottom + CaptureMargin);
        var captureBounds = Rectangle.Intersect(padded, firstSlot.MonitorBounds!.Value);
        ScreenCapture.ValidateBounds(captureBounds);
        return captureBounds;
    }

    public static AbilityHudRecordingResult Record(
        Rectangle bounds,
        string path,
        CancellationToken cancellationToken,
        Action<AbilityHudRecordingProgress>? reportProgress = null,
        Func<Rectangle, Bitmap>? captureFrame = null)
    {
        ScreenCapture.ValidateBounds(bounds);
        captureFrame ??= ScreenCapture.Capture;
        var frames = new StringBuilder("frame,elapsed_ms,utc\r\n");
        var stopwatch = Stopwatch.StartNew();
        var intervalTicks = Stopwatch.Frequency / FramesPerSecond;
        var nextFrameAt = Stopwatch.GetTimestamp();
        var frameCount = 0;
        var sizeLimitReached = false;
        var lastProgressAt = TimeSpan.FromSeconds(-1);
        var fullPath = Path.GetFullPath(path);
        var temporaryPath = Path.Combine(
            Path.GetDirectoryName(fullPath)!,
            $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var file = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: true))
            {
                while (!cancellationToken.IsCancellationRequested && frameCount < MaximumFrames)
                {
                    var elapsedTicks = Stopwatch.GetTimestamp() - nextFrameAt;
                    if (elapsedTicks < 0)
                    {
                        var waitMilliseconds = Math.Max(1, (int)Math.Ceiling(-elapsedTicks * 1000d / Stopwatch.Frequency));
                        cancellationToken.WaitHandle.WaitOne(waitMilliseconds);
                        continue;
                    }

                    var frameTimestamp = stopwatch.Elapsed;
                    using var bitmap = captureFrame(bounds);
                    using var png = new MemoryStream();
                    bitmap.Save(png, System.Drawing.Imaging.ImageFormat.Png);
                    if (file.Position + png.Length + ArchiveOverheadReserve > MaximumArchiveBytes)
                    {
                        sizeLimitReached = true;
                        break;
                    }

                    var frameName = $"frames/{frameCount + 1:D6}.png";
                    var entry = archive.CreateEntry(frameName, CompressionLevel.NoCompression);
                    using (var entryStream = entry.Open())
                    {
                        png.Position = 0;
                        png.CopyTo(entryStream);
                    }

                    frameCount++;
                    frames.Append(frameCount.ToString(CultureInfo.InvariantCulture)).Append(',')
                        .Append(frameTimestamp.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture)).Append(',')
                        .Append(DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)).Append("\r\n");
                    nextFrameAt += intervalTicks;
                    if (nextFrameAt < Stopwatch.GetTimestamp())
                    {
                        nextFrameAt = Stopwatch.GetTimestamp() + intervalTicks;
                    }

                    if (frameTimestamp - lastProgressAt >= TimeSpan.FromSeconds(1))
                    {
                        lastProgressAt = frameTimestamp;
                        reportProgress?.Invoke(new AbilityHudRecordingProgress(
                            frameCount,
                            frameTimestamp,
                            file.Position));
                    }
                }

                var durationLimitReached = frameCount >= MaximumFrames &&
                    !cancellationToken.IsCancellationRequested && !sizeLimitReached;
                var manifest = archive.CreateEntry("recording.txt", CompressionLevel.Fastest);
                using (var manifestWriter = new StreamWriter(manifest.Open(), new UTF8Encoding(false)))
                {
                    manifestWriter.WriteLine("Lossless PNG frames captured from the ability HUD.");
                    manifestWriter.WriteLine($"Bounds: {bounds.X},{bounds.Y},{bounds.Width},{bounds.Height} screen pixels");
                    manifestWriter.WriteLine($"Target frame rate: {FramesPerSecond} FPS");
                    manifestWriter.WriteLine($"Frames: {frameCount}");
                    manifestWriter.WriteLine($"Stopped at size limit: {sizeLimitReached}");
                    manifestWriter.WriteLine($"Stopped at 10-minute limit: {durationLimitReached}");
                    manifestWriter.WriteLine("frames.csv lists each frame's elapsed time and UTC capture time.");
                }

                var index = archive.CreateEntry("frames.csv", CompressionLevel.Fastest);
                using (var indexWriter = new StreamWriter(index.Open(), new UTF8Encoding(false)))
                {
                    indexWriter.Write(frames.ToString());
                }
            }

            var fileSize = new FileInfo(temporaryPath).Length;
            if (fileSize > MaximumArchiveBytes)
            {
                throw new InvalidOperationException("The recording exceeded the 50 MB size limit.");
            }

            File.Move(temporaryPath, fullPath, overwrite: true);
            return new AbilityHudRecordingResult(
                frameCount,
                fileSize,
                stopwatch.Elapsed,
                sizeLimitReached,
                frameCount >= MaximumFrames && !cancellationToken.IsCancellationRequested && !sizeLimitReached);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal readonly record struct AbilityHudRecordingProgress(int Frames, TimeSpan Elapsed, long BytesWritten);

internal readonly record struct AbilityHudRecordingResult(
    int Frames,
    long Bytes,
    TimeSpan Elapsed,
    bool SizeLimitReached,
    bool DurationLimitReached);

using System.Diagnostics;
using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace D2AbilityTracker;

/// <summary>
/// Captures screen regions through DXGI Desktop Duplication. Every composed frame is available with its
/// present timestamp, which is far faster and more precise than GDI BitBlt. Returns false whenever a
/// region cannot be served so the caller can fall back to GDI.
/// </summary>
internal sealed class DesktopDuplicationCapture : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(1);
    private readonly Dictionary<string, OutputDuplication> _outputs = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTime> _retryAfter = new(StringComparer.OrdinalIgnoreCase);
    private long _tick;

    public string? LastError { get; private set; }

    /// <summary>Starts a capture tick; each output acquires at most one new frame per tick.</summary>
    public void BeginTick() => _tick++;

    public bool TryCapture(string deviceName, Rectangle bounds, ScreenCaptureSurface surface, out DateTime timestamp)
    {
        timestamp = DateTime.UtcNow;
        if (_retryAfter.TryGetValue(deviceName, out var retryAfter) && timestamp < retryAfter)
        {
            return false;
        }

        try
        {
            if (!_outputs.TryGetValue(deviceName, out var output))
            {
                output = OutputDuplication.Create(deviceName);
                _outputs.Add(deviceName, output);
            }

            if (output.TryCapture(_tick, bounds, surface, out timestamp))
            {
                LastError = null;
                return true;
            }

            return false;
        }
        catch (Exception exception) when (exception is SharpGenException or InvalidOperationException or COMException)
        {
            LastError = exception.Message;
            if (_outputs.Remove(deviceName, out var failed))
            {
                failed.Dispose();
            }

            _retryAfter[deviceName] = DateTime.UtcNow + RetryDelay;
            return false;
        }
    }

    public void Dispose()
    {
        foreach (var output in _outputs.Values)
        {
            output.Dispose();
        }

        _outputs.Clear();
    }

    private sealed class OutputDuplication : IDisposable
    {
        private readonly ID3D11Device _device;
        private readonly ID3D11DeviceContext _context;
        private readonly IDXGIOutputDuplication _duplication;
        private readonly Rectangle _desktopBounds;
        private ID3D11Texture2D? _frame;
        private bool _frameHeld;
        private readonly Dictionary<Rectangle, StagedRegion> _staging = [];
        private long _acquiredTick = -1;
        private DateTime _tickTimestamp;

        private OutputDuplication(ID3D11Device device, IDXGIOutputDuplication duplication, Rectangle desktopBounds)
        {
            _device = device;
            _context = device.ImmediateContext;
            _duplication = duplication;
            _desktopBounds = desktopBounds;
        }

        public static OutputDuplication Create(string deviceName)
        {
            using var factory = DXGI.CreateDXGIFactory1<IDXGIFactory1>();
            for (uint adapterIndex = 0; factory.EnumAdapters1(adapterIndex, out var adapter).Success; adapterIndex++)
            {
                using (adapter)
                {
                    for (uint outputIndex = 0; adapter.EnumOutputs(outputIndex, out var output).Success; outputIndex++)
                    {
                        using (output)
                        {
                            var description = output.Description;
                            if (!string.Equals(description.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase))
                            {
                                continue;
                            }

                            if (description.Rotation is not (ModeRotation.Identity or ModeRotation.Unspecified))
                            {
                                throw new InvalidOperationException("Rotated displays use GDI capture.");
                            }

                            D3D11.D3D11CreateDevice(adapter, DriverType.Unknown, DeviceCreationFlags.BgraSupport,
                                [FeatureLevel.Level_11_0, FeatureLevel.Level_10_1, FeatureLevel.Level_10_0],
                                out var device).CheckError();
                            try
                            {
                                using var output1 = output.QueryInterface<IDXGIOutput1>();
                                var duplication = output1.DuplicateOutput(device!);
                                if (duplication.Description.ModeDescription.Format != Format.B8G8R8A8_UNorm)
                                {
                                    duplication.Dispose();
                                    throw new InvalidOperationException("This display format uses GDI capture.");
                                }

                                var coordinates = description.DesktopCoordinates;
                                return new OutputDuplication(device!, duplication, Rectangle.FromLTRB(
                                    coordinates.Left, coordinates.Top, coordinates.Right, coordinates.Bottom));
                            }
                            catch
                            {
                                device?.Dispose();
                                throw;
                            }
                        }
                    }
                }
            }

            throw new InvalidOperationException($"Display {deviceName} was not found for desktop duplication.");
        }

        public bool TryCapture(long tick, Rectangle bounds, ScreenCaptureSurface surface, out DateTime timestamp)
        {
            timestamp = DateTime.UtcNow;
            if (!_desktopBounds.Contains(bounds))
            {
                return false;
            }

            if (_acquiredTick != tick)
            {
                _acquiredTick = tick;
                AcquireLatest();
            }

            var staging = Staging(bounds);
            if (_frame is not null)
            {
                // Copy only this region straight out of the held desktop frame; no full-frame GPU copy.
                var left = bounds.Left - _desktopBounds.Left;
                var top = bounds.Top - _desktopBounds.Top;
                _context.CopySubresourceRegion(staging.Texture, 0, 0, 0, 0, _frame, 0,
                    new Vortice.Mathematics.Box(left, top, 0, left + bounds.Width, top + bounds.Height, 1));
                staging.HasData = true;
            }
            else if (!staging.HasData)
            {
                // Nothing changed since this region was last copied, but it was never copied: use GDI once.
                return false;
            }

            var mapped = _context.Map(staging.Texture, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                surface.CopyFrom(mapped.DataPointer, (int)mapped.RowPitch);
            }
            finally
            {
                _context.Unmap(staging.Texture, 0);
            }

            timestamp = _tickTimestamp;
            return true;
        }

        private void AcquireLatest()
        {
            ReleaseHeldFrame();
            var timestamp = AcquireFrame();
            // Keep timestamps strictly monotonic even if present times arrive slightly out of order.
            _tickTimestamp = timestamp > _tickTimestamp ? timestamp : _tickTimestamp.AddTicks(1);
        }

        private DateTime AcquireFrame()
        {
            var result = _duplication.AcquireNextFrame(0, out var info, out var resource);
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            {
                // Nothing on the display changed since the last frame, so the staged copies are still current.
                return DateTime.UtcNow;
            }

            result.CheckError();
            _frameHeld = true;
            try
            {
                if (info.LastPresentTime == 0)
                {
                    // Pointer-only update, or the initial frame before anything presented (which can be blank).
                    ReleaseHeldFrame();
                    return DateTime.UtcNow;
                }

                // Hold the frame for this tick so every region copies directly from it.
                _frame = resource!.QueryInterface<ID3D11Texture2D>();
                return PresentTimestamp(info.LastPresentTime);
            }
            finally
            {
                resource?.Dispose();
            }
        }

        private void ReleaseHeldFrame()
        {
            _frame?.Dispose();
            _frame = null;
            if (_frameHeld)
            {
                _frameHeld = false;
                _duplication.ReleaseFrame();
            }
        }
        private static DateTime PresentTimestamp(long presentQpc)
        {
            var now = DateTime.UtcNow;
            if (presentQpc <= 0)
            {
                return now;
            }

            var age = (Stopwatch.GetTimestamp() - presentQpc) / (double)Stopwatch.Frequency;
            return now - TimeSpan.FromSeconds(Math.Clamp(age, 0, 0.1));
        }

        private StagedRegion Staging(Rectangle bounds)
        {
            if (_staging.TryGetValue(bounds, out var existing))
            {
                return existing;
            }

            if (_staging.Count >= 8)
            {
                foreach (var region in _staging.Values)
                {
                    region.Texture.Dispose();
                }

                _staging.Clear();
            }

            var staging = new StagedRegion(_device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)bounds.Width,
                Height = (uint)bounds.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            }));
            _staging.Add(bounds, staging);
            return staging;
        }

        public void Dispose()
        {
            try
            {
                ReleaseHeldFrame();
            }
            catch (Exception exception) when (exception is SharpGenException or COMException)
            {
            }

            foreach (var region in _staging.Values)
            {
                region.Texture.Dispose();
            }

            _staging.Clear();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }

        private sealed class StagedRegion(ID3D11Texture2D texture)
        {
            public ID3D11Texture2D Texture { get; } = texture;
            public bool HasData { get; set; }
        }
    }
}
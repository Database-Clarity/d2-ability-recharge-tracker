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
        private ID3D11Texture2D? _latest;
        private readonly Dictionary<Size, ID3D11Texture2D> _staging = [];
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

            if (_latest is null)
            {
                return false;
            }

            var staging = Staging(bounds.Size);
            var left = bounds.Left - _desktopBounds.Left;
            var top = bounds.Top - _desktopBounds.Top;
            _context.CopySubresourceRegion(staging, 0, 0, 0, 0, _latest, 0,
                new Vortice.Mathematics.Box(left, top, 0, left + bounds.Width, top + bounds.Height, 1));
            var mapped = _context.Map(staging, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
            try
            {
                surface.CopyFrom(mapped.DataPointer, (int)mapped.RowPitch);
            }
            finally
            {
                _context.Unmap(staging, 0);
            }

            timestamp = _tickTimestamp;
            return true;
        }

        private void AcquireLatest()
        {
            var timestamp = AcquireFrame();
            // Keep timestamps strictly monotonic even if present times arrive slightly out of order.
            _tickTimestamp = timestamp > _tickTimestamp ? timestamp : _tickTimestamp.AddTicks(1);
        }

        private DateTime AcquireFrame()
        {
            var result = _duplication.AcquireNextFrame(0, out var info, out var resource);
            if (result.Code == Vortice.DXGI.ResultCode.WaitTimeout.Code)
            {
                // Nothing on the display changed since the last frame, so the retained copy is still current.
                return DateTime.UtcNow;
            }

            result.CheckError();
            try
            {
                if (info.LastPresentTime == 0)
                {
                    // Pointer-only update, or the initial frame before anything presented (which can be blank).
                    return DateTime.UtcNow;
                }

                using var texture = resource!.QueryInterface<ID3D11Texture2D>();
                if (_latest is null)
                {
                    var description = texture.Description;
                    _latest = _device.CreateTexture2D(new Texture2DDescription
                    {
                        Width = description.Width,
                        Height = description.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = description.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Default,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.None
                    });
                }

                _context.CopyResource(_latest, texture);
                return PresentTimestamp(info.LastPresentTime);
            }
            finally
            {
                resource?.Dispose();
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

        private ID3D11Texture2D Staging(Size size)
        {
            if (_staging.TryGetValue(size, out var existing))
            {
                return existing;
            }

            if (_staging.Count >= 8)
            {
                foreach (var texture in _staging.Values)
                {
                    texture.Dispose();
                }

                _staging.Clear();
            }

            var staging = _device.CreateTexture2D(new Texture2DDescription
            {
                Width = (uint)size.Width,
                Height = (uint)size.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CPUAccessFlags = CpuAccessFlags.Read
            });
            _staging.Add(size, staging);
            return staging;
        }

        public void Dispose()
        {
            foreach (var texture in _staging.Values)
            {
                texture.Dispose();
            }

            _staging.Clear();
            _latest?.Dispose();
            _duplication.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}

using System.Drawing;
using System.Runtime.InteropServices;

namespace D2AbilityTracker;

internal static class ScreenCapture
{
    public static Bitmap Capture(Rectangle bounds)
    {
        ValidateBounds(bounds);
        var bitmap = new Bitmap(bounds.Width, bounds.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    public static DesktopCaptureContext OpenDesktop() => new();

    internal static void ValidateBounds(Rectangle bounds)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bounds), "The capture area must have a positive size.");
        }
    }
}

internal sealed class DesktopCaptureContext : IDisposable
{
    private IntPtr _deviceContext;

    public DesktopCaptureContext()
    {
        _deviceContext = NativeMethods.GetDC(IntPtr.Zero);
        if (_deviceContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows could not open the desktop capture context.");
        }
    }

    public IntPtr DeviceContext => _deviceContext != IntPtr.Zero
        ? _deviceContext
        : throw new ObjectDisposedException(nameof(DesktopCaptureContext));

    public void Dispose()
    {
        if (_deviceContext != IntPtr.Zero)
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, _deviceContext);
            _deviceContext = IntPtr.Zero;
        }
    }
}

internal sealed class ScreenCaptureSurface : IDisposable
{
    private const uint DibRgbColors = 0;
    private const uint SourceCopy = 0x00CC0020;
    private IntPtr _memoryContext;
    private IntPtr _dib;
    private IntPtr _previousObject;

    public ScreenCaptureSurface(int width, int height, IntPtr compatibleDeviceContext)
    {
        ScreenCapture.ValidateBounds(new Rectangle(0, 0, width, height));
        Width = width;
        Height = height;
        Stride = checked(width * 4);
        _memoryContext = NativeMethods.CreateCompatibleDC(compatibleDeviceContext);
        if (_memoryContext == IntPtr.Zero)
        {
            throw new InvalidOperationException("Windows could not create a reusable capture buffer.");
        }

        var info = new BitmapInfo
        {
            Header = new BitmapInfoHeader
            {
                Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                Width = width,
                Height = -height,
                Planes = 1,
                BitCount = 32,
                Compression = 0,
                SizeImage = (uint)checked(Stride * height)
            }
        };
        _dib = NativeMethods.CreateDIBSection(
            compatibleDeviceContext,
            ref info,
            DibRgbColors,
            out var pixels,
            IntPtr.Zero,
            0);
        if (_dib == IntPtr.Zero || pixels == IntPtr.Zero)
        {
            Dispose();
            throw new InvalidOperationException("Windows could not allocate a reusable capture buffer.");
        }

        Pixels = pixels;
        _previousObject = NativeMethods.SelectObject(_memoryContext, _dib);
        if (_previousObject == IntPtr.Zero || _previousObject == new IntPtr(-1))
        {
            Dispose();
            throw new InvalidOperationException("Windows could not initialize a reusable capture buffer.");
        }
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public IntPtr Pixels { get; private set; }

    public void Capture(IntPtr sourceDeviceContext, Rectangle bounds)
    {
        ObjectDisposedException.ThrowIf(_memoryContext == IntPtr.Zero, this);
        if (bounds.Width != Width || bounds.Height != Height)
        {
            throw new ArgumentException("The capture area dimensions do not match the reusable buffer.", nameof(bounds));
        }

        if (!NativeMethods.BitBlt(
            _memoryContext,
            0,
            0,
            Width,
            Height,
            sourceDeviceContext,
            bounds.Left,
            bounds.Top,
            SourceCopy))
        {
            throw new InvalidOperationException($"Windows could not capture the selected display region (error {Marshal.GetLastWin32Error()}).");
        }

        if (!NativeMethods.GdiFlush())
        {
            throw new InvalidOperationException($"Windows could not finish the screen capture (error {Marshal.GetLastWin32Error()}).");
        }
    }

    public void Dispose()
    {
        Pixels = IntPtr.Zero;
        if (_memoryContext != IntPtr.Zero && _previousObject != IntPtr.Zero && _previousObject != new IntPtr(-1))
        {
            NativeMethods.SelectObject(_memoryContext, _previousObject);
            _previousObject = IntPtr.Zero;
        }

        if (_dib != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_dib);
            _dib = IntPtr.Zero;
        }

        if (_memoryContext != IntPtr.Zero)
        {
            NativeMethods.DeleteDC(_memoryContext);
            _memoryContext = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BitmapInfo
    {
        public BitmapInfoHeader Header;
        public uint Color;
    }
}

internal static class NativeMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern int ReleaseDC(IntPtr window, IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateCompatibleDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteDC(IntPtr deviceContext);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref ScreenCaptureSurface.BitmapInfo info,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern IntPtr SelectObject(IntPtr deviceContext, IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool DeleteObject(IntPtr graphicsObject);

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool GdiFlush();

    [DllImport("gdi32.dll", SetLastError = true)]
    internal static extern bool BitBlt(
        IntPtr destination,
        int x,
        int y,
        int width,
        int height,
        IntPtr source,
        int sourceX,
        int sourceY,
        uint operation);
}

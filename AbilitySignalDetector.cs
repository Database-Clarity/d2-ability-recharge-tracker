namespace D2AbilityTracker;

internal static class AbilitySignalDetector
{
    private const float ChargedSaturationThreshold = 0.32f;
    private const float MinimumChargedBackgroundRatio = 0.24f;
    private const float MinimumLineContrast = 0.18f;
    private const int LineContrastOffset = 3;

    public static unsafe AbilitySignal Read(
        IntPtr pixels,
        int stride,
        int cropX,
        int cropY,
        int width,
        int height)
    {
        if (pixels == IntPtr.Zero || stride <= 0 || cropX < 0 || cropY < 0 || width <= 0 || height <= 0 ||
            (long)cropX + width > stride / 4)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "The captured pixel buffer is invalid.");
        }

        pixels = IntPtr.Add(pixels, checked(cropY * stride + cropX * 4));
        var leftInner = Math.Clamp((int)(width * 0.12f), 0, width - 1);
        var leftOuter = Math.Clamp((int)(width * 0.3f), leftInner + 1, width);
        var rightInner = Math.Clamp((int)(width * 0.7f), 0, width - 1);
        var rightOuter = Math.Clamp((int)(width * 0.88f), rightInner + 1, width);
        var saturatedPixels = 0;
        var fillPixels = 0;

        var pixelData = (byte*)pixels;
        for (var side = 0; side < 2; side++)
        {
            var left = side == 0 ? leftInner : rightInner;
            var right = side == 0 ? leftOuter : rightOuter;
            for (var patch = 0; patch < 2; patch++)
            {
                var top = patch == 0 ? (int)(height * 0.14f) : (int)(height * 0.52f);
                var bottom = patch == 0 ? (int)(height * 0.32f) : (int)(height * 0.72f);
                for (var y = top; y < bottom; y++)
                {
                    var row = pixelData + y * stride;
                    for (var x = left; x < right; x++)
                    {
                        var pixel = row + x * 4;
                        var red = pixel[2];
                        var green = pixel[1];
                        var blue = pixel[0];
                        var maximum = Math.Max(red, Math.Max(green, blue));
                        var minimum = Math.Min(red, Math.Min(green, blue));
                        var saturation = maximum == 0 ? 0f : (maximum - minimum) / (float)maximum;
                        if (saturation >= ChargedSaturationThreshold)
                        {
                            saturatedPixels++;
                        }

                        fillPixels++;
                    }
                }
            }
        }

        // The four side/corner patches sample slot fill, avoiding the ability artwork.
        var chargedRatio = fillPixels == 0 ? 0 : saturatedPixels / (float)fillPixels;
        var lineRatio = FindCooldownLineContrast(
            pixelData,
            width,
            height,
            stride,
            Math.Clamp((int)(width * 0.07f), 0, width - 1),
            Math.Clamp((int)(width * 0.20f), 1, width),
            Math.Clamp((int)(width * 0.80f), 0, width - 1),
            Math.Clamp((int)(width * 0.93f), 1, width));
        return new AbilitySignal(
            chargedRatio >= MinimumChargedBackgroundRatio,
            lineRatio >= MinimumLineContrast,
            chargedRatio,
            lineRatio);
    }

    private static unsafe float FindCooldownLineContrast(
        byte* pixelData,
        int width,
        int height,
        int stride,
        int leftStart,
        int leftEnd,
        int rightStart,
        int rightEnd)
    {
        var innerWidth = leftEnd - leftStart + rightEnd - rightStart;
        if (innerWidth <= 0)
        {
            return 0;
        }

        var firstRow = Math.Clamp((int)(height * 0.04f), 0, height - 1);
        var lastRow = Math.Clamp((int)(height * 0.96f), firstRow + 1, height);
        Span<float> luminanceByRow = stackalloc float[height];
        for (var y = firstRow; y < lastRow; y++)
        {
            var luminanceTotal = 0f;
            var row = pixelData + y * stride;
            for (var x = leftStart; x < leftEnd; x++)
            {
                luminanceTotal += GetLuminance(row + x * 4);
            }

            for (var x = rightStart; x < rightEnd; x++)
            {
                luminanceTotal += GetLuminance(row + x * 4);
            }

            luminanceByRow[y] = luminanceTotal / innerWidth;
        }

        var strongestLine = 0f;
        for (var y = firstRow + LineContrastOffset; y < lastRow - LineContrastOffset; y++)
        {
            var above = 0f;
            var below = 0f;
            for (var offset = 1; offset <= LineContrastOffset; offset++)
            {
                above += luminanceByRow[y - offset];
                below += luminanceByRow[y + offset];
            }

            var contrast = Math.Abs(above - below) / LineContrastOffset;
            strongestLine = Math.Max(strongestLine, contrast);
        }

        return strongestLine;
    }

    private static unsafe float GetLuminance(byte* pixel)
    {
        return (0.2126f * pixel[2] + 0.7152f * pixel[1] + 0.0722f * pixel[0]) / 255f;
    }
}

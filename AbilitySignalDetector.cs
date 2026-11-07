namespace D2AbilityTracker;

internal static class AbilitySignalDetector
{
    private const float ChargedSaturationThreshold = 0.32f;
    private const float MinimumChargedBackgroundRatio = 0.65f;
    private const float MinimumLineContrast = 0.10f;
    private const int LineContrastOffset = 3;

    public static unsafe AbilitySignal Read(
        IntPtr pixels,
        int stride,
        int cropX,
        int cropY,
        int width,
        int height,
        int bottomPadding = 0,
        float? previousProgress = null,
        float progressTolerance = 0.12f,
        int topPadding = 0,
        TwinGuide? guide = null)
    {
        if (pixels == IntPtr.Zero || stride <= 0 || cropX < 0 || cropY < 0 || width <= 0 || height <= 0 ||
            (long)cropX + width > stride / 4 || bottomPadding < 0 || topPadding < 0 || topPadding > cropY)
        {
            throw new ArgumentOutOfRangeException(nameof(pixels), "The captured pixel buffer is invalid.");
        }

        pixels = IntPtr.Add(pixels, checked(cropY * stride + cropX * 4));
        var twin = HudTwin.Read((byte*)pixels, stride, width, height, topPadding, bottomPadding, guide);
        var leftInner = Math.Clamp((int)(width * 0.12f), 0, width - 1);
        var leftOuter = Math.Clamp((int)(width * 0.3f), leftInner + 1, width);
        var rightInner = Math.Clamp((int)(width * 0.7f), 0, width - 1);
        var rightOuter = Math.Clamp((int)(width * 0.88f), rightInner + 1, width);
        var saturatedPixels = 0;
        var fillPixels = 0;
        var redTotal = 0;
        var greenTotal = 0;
        var blueTotal = 0;
        var flashPixels = 0;
        var blankPixels = 0;
        var fillLuminance = 0f;
        var fillRed = 0;
        var fillGreen = 0;
        var fillBlue = 0;
        Span<int> hues = stackalloc int[24];
        hues.Clear();

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
                        fillLuminance += GetLuminance(pixel);
                        fillRed += red;
                        fillGreen += green;
                        fillBlue += blue;
                        if (maximum - minimum < 35 && GetLuminance(pixel) > 0.72f) { flashPixels++; }
                        if (GetLuminance(pixel) < 0.18f) { blankPixels++; }
                        if (saturation >= ChargedSaturationThreshold && maximum - minimum >= 40)
                        {
                            saturatedPixels++;
                            redTotal += red;
                            greenTotal += green;
                            blueTotal += blue;
                            var hue = maximum == red ? (green - blue) / (float)(maximum - minimum) :
                                maximum == green ? 2 + (blue - red) / (float)(maximum - minimum) :
                                4 + (red - green) / (float)(maximum - minimum);
                            hue = (hue * 60 + 360) % 360;
                            hues[(int)(hue / 15)]++;
                        }

                        fillPixels++;
                    }
                }
            }
        }

        // The four side/corner patches sample slot fill, avoiding the ability artwork.
        var chargedRatio = fillPixels == 0 ? 0 : saturatedPixels / (float)fillPixels;
        var dominantHue = 0;
        for (var bin = 1; bin < hues.Length; bin++)
        {
            if (hues[bin] > hues[dominantHue]) { dominantHue = bin; }
        }
        var lineRatio = FindCooldownLineContrast(
            pixelData,
            width,
            height,
            stride,
            Math.Clamp((int)(width * 0.07f), 0, width - 1),
            Math.Clamp((int)(width * 0.20f), 1, width),
            Math.Clamp((int)(width * 0.80f), 0, width - 1),
            Math.Clamp((int)(width * 0.93f), 1, width),
            out var progress,
            previousProgress,
            progressTolerance);
        return new AbilitySignal(
            chargedRatio >= MinimumChargedBackgroundRatio,
            lineRatio >= MinimumLineContrast,
            chargedRatio,
            lineRatio,
            ReadBottomBorderThickness(pixelData, stride, width, height, bottomPadding),
            progress,
            saturatedPixels == 0 ? null : dominantHue * 15 + 7.5f,
            saturatedPixels == 0 ? null : System.Drawing.Color.FromArgb(redTotal / saturatedPixels,
                greenTotal / saturatedPixels, blueTotal / saturatedPixels),
            fillPixels > 0 && flashPixels / (float)fillPixels > 0.65f,
            true,
            fillPixels > 0 && blankPixels / (float)fillPixels > 0.65f,
            fillPixels == 0 ? null : fillLuminance / fillPixels,
            twin,
            fillPixels == 0 ? null : MeanSaturation(fillRed, fillGreen, fillBlue));
    }

    // Saturation of the mean fill colour: the gray recharge bar is neutral, the charged fill is strongly tinted.
    private static float MeanSaturation(int red, int green, int blue)
    {
        var maximum = Math.Max(red, Math.Max(green, blue));
        var minimum = Math.Min(red, Math.Min(green, blue));
        return maximum == 0 ? 0 : (maximum - minimum) / (float)maximum;
    }

    public static int BottomPadding(int height) => Math.Max(3, (int)Math.Ceiling(height * 0.1));

    public static int TopPadding(int height) => Math.Max(3, (int)Math.Ceiling(height * 0.1));

    private static unsafe float ReadBottomBorderThickness(byte* pixels, int stride, int width, int height, int padding)
    {
        var left = (int)(width * 0.12f);
        var right = Math.Max(left + 1, (int)(width * 0.88f));
        var thickness = 0f;
        for (var x = left; x < right; x++)
        {
            var runLength = 0;
            var longest = 0;
            for (var y = (int)(height * 0.85f); y < height + padding; y++)
            {
                var value = GetLuminance(pixels + y * stride + x * 4);
                var above = GetLuminance(pixels + Math.Max(0, y - 6) * stride + x * 4);
                if (value >= 0.4f && value - above >= 0.15f)
                {
                    runLength++;
                    longest = Math.Max(longest, runLength);
                }
                else { runLength = 0; }
            }

            thickness += longest;
        }

        return thickness / (right - left) / height;
    }

    private static unsafe float FindCooldownLineContrast(
        byte* pixelData,
        int width,
        int height,
        int stride,
        int leftStart,
        int leftEnd,
        int rightStart,
        int rightEnd,
        out float? progress,
        float? previousProgress,
        float progressTolerance)
    {
        progress = null;
        var innerWidth = leftEnd - leftStart + rightEnd - rightStart;
        if (innerWidth <= 0)
        {
            return 0;
        }

        var strongestLine = 0f;
        // HUD fill is brighter below the rising boundary; scene edges of the opposite polarity are not recharge.
        ReadOnlySpan<float> slopes = [-0.08f, 0f];
        foreach (var slope in slopes)
        {
            for (var y = Math.Max(LineContrastOffset + 3, (int)(height * 0.06f)); y < (int)(height * 0.85f); y++)
            {
                var candidate = 1f - y / (float)height;
                if (previousProgress is { } previous &&
                    Math.Abs(candidate - previous) > (previous == 0 ? 0.25f : progressTolerance)) { continue; }
                var left = BoundaryContrast(pixelData, stride, width, height, y, leftStart, leftEnd, slope);
                var right = BoundaryContrast(pixelData, stride, width, height, y, rightStart, rightEnd, slope);
                var contrast = Math.Min(left, right);
                if (contrast > strongestLine)
                {
                    strongestLine = contrast;
                    progress = 1f - y / (float)height;
                }
            }
        }

        if (strongestLine < MinimumLineContrast)
        {
            progress = null;
        }

        return strongestLine;
    }

    private static unsafe float BoundaryContrast(byte* pixels, int stride, int width, int height,
        int y, int start, int end, float slope)
    {
        var total = 0f;
        var supported = 0;
        for (var x = start; x < end; x++)
        {
            var row = Math.Clamp(y + (int)Math.Round((x - width / 2f) * slope),
                LineContrastOffset, height - LineContrastOffset - 1);
            var difference = 0f;
            for (var offset = 1; offset <= LineContrastOffset; offset++)
            {
                difference += GetLuminance(pixels + (row + offset) * stride + x * 4) -
                    GetLuminance(pixels + (row - offset) * stride + x * 4);
            }

            difference /= LineContrastOffset;
            total += difference;
            if (difference >= MinimumLineContrast) { supported++; }
        }

        return supported >= (end - start) * 0.7f ? total / Math.Max(1, end - start) : 0;
    }

    private static unsafe float GetLuminance(byte* pixel)
    {
        return (0.2126f * pixel[2] + 0.7152f * pixel[1] + 0.0722f * pixel[0]) / 255f;
    }
}

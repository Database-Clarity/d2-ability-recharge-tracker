using System.Drawing;

namespace D2AbilityTracker;

internal static class AbilitySignalDetector
{
    private const float ChargedSaturationThreshold = 0.32f;
    private const float MinimumChargedBackgroundRatio = 0.24f;
    private const float MinimumLineContrast = 0.18f;
    private const int LineContrastOffset = 3;

    public static AbilitySignal Read(Bitmap slot)
    {
        var leftInner = Math.Clamp((int)(slot.Width * 0.12f), 0, slot.Width - 1);
        var leftOuter = Math.Clamp((int)(slot.Width * 0.3f), leftInner + 1, slot.Width);
        var rightInner = Math.Clamp((int)(slot.Width * 0.7f), 0, slot.Width - 1);
        var rightOuter = Math.Clamp((int)(slot.Width * 0.88f), rightInner + 1, slot.Width);
        var saturatedPixels = 0;
        var fillPixels = 0;

        foreach (var (left, right) in new[] { (leftInner, leftOuter), (rightInner, rightOuter) })
        {
            foreach (var (topRatio, bottomRatio) in new[] { (0.14f, 0.32f), (0.52f, 0.72f) })
            {
                var top = Math.Clamp((int)(slot.Height * topRatio), 0, slot.Height - 1);
                var bottom = Math.Clamp((int)(slot.Height * bottomRatio), top + 1, slot.Height);
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var color = slot.GetPixel(x, y);
                        var maximum = Math.Max(color.R, Math.Max(color.G, color.B));
                        var minimum = Math.Min(color.R, Math.Min(color.G, color.B));
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
            slot,
            Math.Clamp((int)(slot.Width * 0.07f), 0, slot.Width - 1),
            Math.Clamp((int)(slot.Width * 0.20f), 1, slot.Width),
            Math.Clamp((int)(slot.Width * 0.80f), 0, slot.Width - 1),
            Math.Clamp((int)(slot.Width * 0.93f), 1, slot.Width));
        return new AbilitySignal(
            chargedRatio >= MinimumChargedBackgroundRatio,
            lineRatio >= MinimumLineContrast,
            chargedRatio,
            lineRatio);
    }

    private static float FindCooldownLineContrast(
        Bitmap slot,
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

        var firstRow = Math.Clamp((int)(slot.Height * 0.04f), 0, slot.Height - 1);
        var lastRow = Math.Clamp((int)(slot.Height * 0.96f), firstRow + 1, slot.Height);
        var luminanceByRow = new float[slot.Height];
        for (var y = firstRow; y < lastRow; y++)
        {
            var luminanceTotal = 0f;
            for (var x = leftStart; x < leftEnd; x++)
            {
                luminanceTotal += GetLuminance(slot.GetPixel(x, y));
            }

            for (var x = rightStart; x < rightEnd; x++)
            {
                luminanceTotal += GetLuminance(slot.GetPixel(x, y));
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

    private static float GetLuminance(Color color)
    {
        return (0.2126f * color.R + 0.7152f * color.G + 0.0722f * color.B) / 255f;
    }
}

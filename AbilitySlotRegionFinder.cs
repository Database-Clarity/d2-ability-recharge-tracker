using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace D2AbilityTracker;

internal static class AbilitySlotRegionFinder
{
    public static Rectangle[] Find(Bitmap preview, Rectangle sourceBounds)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Width <= 0 || preview.Height <= 0 || sourceBounds.Width != preview.Width || sourceBounds.Height != preview.Height)
        {
            return [];
        }

        var pixels = ReadPixels(preview, out var stride);
        var luminance = ReadLuminance(pixels, stride, preview.Width, preview.Height);
        return FindLayout(luminance, preview.Width, preview.Height, sourceBounds);
    }

    private static Rectangle[] FindLayout(byte[] luminance, int width, int height, Rectangle sourceBounds)
    {
        // Pair visible vertical edges into slots before matching the complete three-slot row.
        var edges = FindVerticalEdges(luminance, width, height);
        var edgesByX = edges.ToLookup(edge => edge.X);
        var candidates = new List<Candidate>();
        foreach (var left in edges)
        {
            var length = left.Bottom - left.Top + 1;
            var tolerance = Math.Max(4, length * 0.3);
            var minimumRight = Math.Max(left.X + 13, (int)Math.Ceiling(left.X + length - tolerance - 1));
            var maximumRight = Math.Min(width - 1, (int)Math.Floor(left.X + length + tolerance - 1));
            for (var rightX = minimumRight; rightX <= maximumRight; rightX++)
            {
                foreach (var right in edgesByX[rightX])
                {
                    var side = right.X - left.X + 1;
                    if (Math.Abs(right.Bottom - right.Top + 1 - side) > tolerance)
                    {
                        continue;
                    }

                    if (Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top) < side * 0.7)
                    {
                        continue;
                    }

                    var measuredSlope = (right.Top + right.Bottom - left.Top - left.Bottom) / (2f * (side - 1));
                    foreach (var slope in new[] { -0.06f, Math.Clamp(measuredSlope, -0.15f, -0.015f) })
                    {
                        var skew = (int)Math.Round(slope * (side - 1));
                        foreach (var top in new[] { left.Top, left.Bottom - side + 1, right.Top - skew, right.Bottom - side + 1 - skew })
                        {
                            var score = ScoreSquare(luminance, width, height, left.X, top, side, slope);
                            if (score >= 0.38)
                            {
                                candidates.Add(new Candidate(left.X, top, side, score, slope));
                            }
                        }
                    }
                }
            }
        }

        var distinct = new List<Candidate>();
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (!distinct.Any(other => Math.Abs(other.Left - candidate.Left) <= 3 &&
                Math.Abs(other.Top - candidate.Top) <= 3 && Math.Abs(other.Width - candidate.Width) <= 4))
            {
                distinct.Add(candidate);
            }
        }

        Candidate? bestFirst = null;
        Candidate? bestSecond = null;
        Candidate? bestThird = null;
        var bestScore = double.NegativeInfinity;
        foreach (var first in distinct)
        {
            foreach (var second in distinct)
            {
                if (!Compatible(first, second, first.Width))
                {
                    continue;
                }

                foreach (var third in distinct)
                {
                    if (!Compatible(second, third, first.Width) ||
                        Math.Abs(first.Width - third.Width) > first.Width * 0.08f ||
                        Math.Abs((second.Left - first.Left) - (third.Left - second.Left)) > first.Width * 0.08f)
                    {
                        continue;
                    }

                    var slope = (third.Top - first.Top) / (float)(third.Left - first.Left);
                    if (slope is < -0.15f or > -0.015f)
                    {
                        continue;
                    }

                    var score = ScoreSquare(luminance, width, height, first.Left, first.Top, first.Width, slope) +
                        ScoreSquare(luminance, width, height, second.Left, second.Top, second.Width, slope) +
                        ScoreSquare(luminance, width, height, third.Left, third.Top, third.Width, slope) -
                        (Math.Abs(first.Width - second.Width) + Math.Abs(second.Width - third.Width)) / (double)first.Width;
                    if (score > bestScore)
                    {
                        bestScore = score;
                        bestFirst = first with { Slope = slope };
                        bestSecond = second with { Slope = slope };
                        bestThird = third with { Slope = slope };
                    }
                }
            }
        }

        if (bestFirst is null || bestSecond is null || bestThird is null)
        {
            return [];
        }

        return RefineLayout(luminance, width, height, bestFirst.Value, bestSecond.Value, bestThird.Value)
            .Select(candidate => ToSourceBounds(candidate, candidate.Slope, sourceBounds))
            .ToArray();
    }

    private static Candidate[] RefineLayout(byte[] luminance, int width, int height, Candidate first, Candidate second, Candidate third)
    {
        // Fit one size and warp to the row so artwork cannot independently resize each slot.
        var result = new[] { first, second, third };
        var bestScore = double.NegativeInfinity;
        var minimumSide = Math.Min(first.Width, Math.Min(second.Width, third.Width)) - 2;
        var maximumSide = Math.Max(first.Width, Math.Max(second.Width, third.Width)) + 2;
        var spacing = (third.Left - first.Left) / 2f;
        for (var side = minimumSide; side <= maximumSide; side++)
        {
            for (var slopeIndex = 0; slopeIndex <= 27; slopeIndex++)
            {
                var slope = -0.15f + slopeIndex * 0.005f;
                for (var top = first.Top - 3; top <= first.Top + 3; top++)
                {
                    var row = new Candidate[3];
                    for (var index = 0; index < row.Length; index++)
                    {
                        var nominalLeft = (int)Math.Round(first.Left + index * spacing);
                        var rowTop = (int)Math.Round(top + index * spacing * slope);
                        var best = new Candidate(nominalLeft, rowTop, side, double.NegativeInfinity, slope);
                        for (var left = nominalLeft - 2; left <= nominalLeft + 2; left++)
                        {
                            var score = ScoreSquare(luminance, width, height, left, rowTop, side, slope);
                            if (score > best.Score)
                            {
                                best = new Candidate(left, rowTop, side, score, slope);
                            }
                        }

                        row[index] = best;
                    }

                    var rowScore = row.Sum(candidate => candidate.Score);
                    if (rowScore > bestScore)
                    {
                        bestScore = rowScore;
                        result = row;
                    }
                }
            }
        }

        return result;
    }

    private static List<VerticalEdge> FindVerticalEdges(byte[] luminance, int width, int height)
    {
        var edges = new List<VerticalEdge>();
        var minimumSide = 14;
        var maximumSide = Math.Min(height, width / 3);
        for (var x = 3; x < width - 3; x++)
        {
            var start = -1;
            var last = -1;
            for (var y = 1; y <= height; y++)
            {
                var edge = y < height && luminance[y * width + x] > 45 &&
                    (Math.Abs(luminance[y * width + x] - luminance[y * width + x - 3]) >= 12 ||
                    Math.Abs(luminance[y * width + x] - luminance[y * width + x + 3]) >= 12);
                if (edge)
                {
                    if (start < 0)
                    {
                        start = y;
                    }

                    last = y;
                }

                if (start >= 0 && (y - last > 2 || y == height))
                {
                    var length = last - start + 1;
                    if (length >= minimumSide && length <= maximumSide)
                    {
                        edges.Add(new VerticalEdge(x, start, last));
                    }

                    start = -1;
                }
            }
        }

        return edges;
    }

    private static byte[] ReadPixels(Bitmap preview, out int stride)
    {
        if (preview.PixelFormat is PixelFormat.Format32bppArgb or PixelFormat.Format32bppRgb or PixelFormat.Format32bppPArgb)
        {
            var sourceRect = new Rectangle(0, 0, preview.Width, preview.Height);
            var sourceData = preview.LockBits(sourceRect, ImageLockMode.ReadOnly, preview.PixelFormat);
            try
            {
                stride = Math.Abs(sourceData.Stride);
                var buffer = new byte[stride * preview.Height];
                if (sourceData.Stride > 0)
                {
                    Marshal.Copy(sourceData.Scan0, buffer, 0, buffer.Length);
                }
                else
                {
                    for (var y = 0; y < preview.Height; y++)
                    {
                        Marshal.Copy(
                            IntPtr.Add(sourceData.Scan0, y * sourceData.Stride),
                            buffer, y * stride, stride);
                    }
                }

                return buffer;
            }
            finally
            {
                preview.UnlockBits(sourceData);
            }
        }

        using var normalized = new Bitmap(preview.Width, preview.Height, PixelFormat.Format32bppArgb);
        using (var graphics = Graphics.FromImage(normalized))
        {
            graphics.DrawImageUnscaled(preview, 0, 0);
        }

        var rect = new Rectangle(0, 0, normalized.Width, normalized.Height);
        var data = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            stride = data.Stride;
            var buffer = new byte[stride * normalized.Height];
            Marshal.Copy(data.Scan0, buffer, 0, buffer.Length);
            return buffer;
        }
        finally
        {
            normalized.UnlockBits(data);
        }
    }

    private static byte[] ReadLuminance(byte[] pixels, int stride, int width, int height)
    {
        var luminance = new byte[width * height];
        for (var y = 0; y < height; y++)
        {
            var sourceRow = y * stride;
            var targetRow = y * width;
            for (var x = 0; x < width; x++)
            {
                var offset = sourceRow + x * 4;
                luminance[targetRow + x] = (byte)((pixels[offset + 2] * 77 +
                    pixels[offset + 1] * 150 + pixels[offset] * 29) >> 8);
            }
        }

        return luminance;
    }

    private static double ScoreSquare(byte[] luminance, int width, int height, int left, int top, int side, float slope)
    {
        if (side < 14 || left < 0 || left + side > width || top + slope * (side - 1) < 0 || top + side > height)
        {
            return 0;
        }

        double topTotal = 0;
        double bottomTotal = 0;
        double leftTotal = 0;
        double rightTotal = 0;
        var sampleCount = (side + 3) / 4;
        var right = left + side - 1;
        for (var step = 0; step < side; step += 4)
        {
            var x = left + step;
            var skew = (int)Math.Round(slope * step);
            var topY = top + skew;
            var bottomY = top + side - 1 + skew;
            AddEdgeSample(luminance, width, height, x, topY, true, ref topTotal);
            AddEdgeSample(luminance, width, height, x, bottomY, true, ref bottomTotal);
            var leftY = top + step;
            var rightY = top + step + (int)Math.Round(slope * (side - 1));
            AddEdgeSample(luminance, width, height, left, leftY, false, ref leftTotal);
            AddEdgeSample(luminance, width, height, right, rightY, false, ref rightTotal);
        }

        var topScore = topTotal / sampleCount;
        var bottomScore = bottomTotal / sampleCount;
        var leftScore = leftTotal / sampleCount;
        var rightScore = rightTotal / sampleCount;
        var weakestEdge = Math.Min(Math.Min(topScore, bottomScore), Math.Min(leftScore, rightScore));
        return (topScore + bottomScore + leftScore + rightScore) * 0.25 + weakestEdge * 0.35;
    }

    private static void AddEdgeSample(byte[] luminance, int width, int height, int x, int y, bool horizontal,
        ref double total)
    {
        var edgeLight = PixelLuminance(luminance, width, height, x, y);
        var before = PixelLuminance(luminance, width, height, x + (horizontal ? 0 : -3), y + (horizontal ? -3 : 0));
        var after = PixelLuminance(luminance, width, height, x + (horizontal ? 0 : 3), y + (horizontal ? 3 : 0));
        var contrast = edgeLight - (before + after) * 0.5;
        var brightScore = Math.Clamp((edgeLight - 55) / 150d, 0, 1);
        var contrastScore = Math.Clamp((contrast - 2) / 65d, 0, 1);
        total += brightScore * 0.48 + contrastScore * 0.52;
    }

    private static byte PixelLuminance(byte[] luminance, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        return luminance[y * width + x];
    }

    private static bool Compatible(Candidate first, Candidate second, float expectedSide)
    {
        var gap = second.Left - first.Left;
        var expectedTop = first.Top + first.Slope * gap;
        return gap >= expectedSide * 1.02f && gap <= expectedSide * 1.22f &&
            Math.Abs(first.Width - second.Width) <= Math.Max(2, expectedSide * 0.08f) &&
            Math.Abs(second.Top - expectedTop) <= Math.Max(2, expectedSide * 0.05f) &&
            Math.Abs(first.Slope - second.Slope) <= 0.045f;
    }

    private static Rectangle ToSourceBounds(Candidate candidate, float slope, Rectangle sourceBounds)
    {
        var padding = Math.Max(1, (int)(candidate.Width * 0.03));
        var skew = (int)Math.Round(slope * (candidate.Width - 1));
        var minTop = Math.Max(0, candidate.Top + Math.Min(0, skew) - padding);
        var maxBottom = Math.Min(sourceBounds.Height, candidate.Top + Math.Max(0, skew) + candidate.Width + padding);
        var left = Math.Max(0, candidate.Left - padding);
        var right = Math.Min(sourceBounds.Width, candidate.Left + candidate.Width + padding);
        return new Rectangle(
            sourceBounds.Left + left,
            sourceBounds.Top + minTop,
            right - left,
            maxBottom - minTop);
    }

    private readonly record struct Candidate(int Left, int Top, int Width, double Score, float Slope);
    private readonly record struct VerticalEdge(int X, int Top, int Bottom);
}

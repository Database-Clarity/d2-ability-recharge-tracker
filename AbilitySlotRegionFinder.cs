using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace D2AbilityTracker;

internal static class AbilitySlotRegionFinder
{
    private const float MinimumSlope = -0.15f;
    private const float SlopeStep = 0.005f;

    public static Rectangle[] Find(Bitmap preview, Rectangle sourceBounds)
    {
        ArgumentNullException.ThrowIfNull(preview);
        if (preview.Width <= 0 || preview.Height <= 0 || sourceBounds.Width != preview.Width || sourceBounds.Height != preview.Height)
        {
            return [];
        }

        var pixels = ReadPixels(preview, out var stride);
        var luminance = ReadLuminance(pixels, stride, preview.Width, preview.Height);
        var stripGroups = FindHudStripPixels(pixels, stride, preview.Width, preview.Height);
        foreach (var stripPoints in stripGroups)
        {
            if (stripPoints.Count < 80)
            {
                continue;
            }

            var regions = FindFromStrip(luminance, preview.Width, preview.Height, sourceBounds, stripPoints);
            if (regions.Length == 3)
            {
                return regions;
            }
        }

        return [];
    }

    private static Rectangle[] FindFromStrip(
        byte[] luminance, int width, int height, Rectangle sourceBounds, List<Point> stripPoints)
    {
        var anchor = FindAnchor(stripPoints, width, height);
        if (anchor is null)
        {
            return [];
        }

        var (firstX, lastX, slope, firstY) = anchor.Value;
        var span = lastX - firstX + 1;
        if (span < Math.Max(60, width * 0.12f) || span > width * 0.9f ||
            firstX > width * 0.5f)
        {
            return [];
        }

        var wideFrame = width > 600 || width / (float)height > 2.5f;
        var slotWidthRatio = wideFrame ? 0.135f : 0.23f;
        var expectedSide = span * slotWidthRatio;
        if (expectedSide < 12 || expectedSide >= span * 0.45f)
        {
            return [];
        }

        var candidateSets = new List<Candidate>[2][];
        var candidateSetIndex = 0;
        var minimumSide = Math.Max(12, wideFrame
            ? (int)Math.Floor(expectedSide - 6)
            : (int)Math.Floor(expectedSide * 0.78f));
        var maximumSide = Math.Max(minimumSide, wideFrame
            ? (int)Math.Ceiling(expectedSide - 2)
            : (int)Math.Ceiling(expectedSide * 1.24f));

        foreach (var startOffset in new[] { 0f, expectedSide })
        {
            var candidatesBySlot = new List<Candidate>[3];
            for (var index = 0; index < candidatesBySlot.Length; index++)
            {
                var centerX = firstX + startOffset + index * expectedSide * 1.075f;
                var searchRadius = Math.Max(4, (int)Math.Ceiling(expectedSide * (wideFrame ? 0.12f : 0.55f)));
                var leftStart = Math.Max(0, (int)Math.Round(centerX) - searchRadius);
                var leftEnd = Math.Min(width - minimumSide - 1, (int)Math.Round(centerX) + searchRadius);
                candidatesBySlot[index] = FindCandidates(
                    luminance, width, height, firstX, firstY, slope,
                    leftStart, leftEnd, minimumSide, maximumSide, expectedSide - 4, wideFrame);
            }

            candidateSets[candidateSetIndex++] = candidatesBySlot;
        }

        Candidate? bestFirst = null;
        Candidate? bestSecond = null;
        Candidate? bestThird = null;
        var bestScore = double.NegativeInfinity;
        foreach (var candidatesBySlot in candidateSets)
        {
            if (candidatesBySlot.Any(candidates => candidates.Count == 0))
            {
                continue;
            }

            foreach (var first in candidatesBySlot[0])
            {
                var slotSide = first.Width;
                foreach (var second in candidatesBySlot[1])
                {
                    if (!Compatible(first, second, slotSide))
                    {
                        continue;
                    }

                    foreach (var third in candidatesBySlot[2])
                    {
                        if (!Compatible(second, third, slotSide) ||
                            Math.Abs(first.Width - third.Width) > slotSide * 0.24f)
                        {
                            continue;
                        }

                        var score = first.Score + second.Score + third.Score -
                            Math.Abs((second.Left - first.Left) - (third.Left - second.Left)) / slotSide * 0.06 -
                            Math.Abs(first.Left - firstX) / slotSide * 0.4;
                        if (score > bestScore)
                        {
                            bestScore = score;
                            bestFirst = first;
                            bestSecond = second;
                            bestThird = third;
                        }
                    }
                }
            }
        }

        if (bestFirst is null || bestSecond is null || bestThird is null || bestScore < 1.05)
        {
            return [];
        }

        return new[] { bestFirst.Value, bestSecond.Value, bestThird.Value }
            .Select(candidate => ToSourceBounds(candidate, candidate.Slope, sourceBounds))
            .ToArray();
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

    private static List<Point>[] FindHudStripPixels(byte[] pixels, int stride, int width, int height)
    {
        var pointsByColor = Enumerable.Range(0, 6).Select(_ => new List<Point>()).ToArray();
        var minimumY = (int)(height * 0.08f);
        var maximumY = (int)(height * 0.98f);
        var minimumRun = Math.Max(40, (int)(width * 0.08f));
        var longestStarts = new int[pointsByColor.Length];
        var longestEnds = new int[pointsByColor.Length];
        var sampleStep = width >= 1600 && height >= 900 ? 4 : 1;
        var scanWidth = width + (width % sampleStep == 0 ? 0 : sampleStep - width % sampleStep);
        for (var y = minimumY; y < maximumY; y += sampleStep)
        {
            var row = y * stride;
            Array.Fill(longestStarts, -1);
            Array.Clear(longestEnds);
            var activeGroup = -1;
            var activeStart = -1;
            for (var x = 0; x <= scanWidth; x += sampleStep)
            {
                var colorGroup = -1;
                if (x < width)
                {
                    var offset = row + x * 4;
                    var blue = pixels[offset];
                    var green = pixels[offset + 1];
                    var red = pixels[offset + 2];
                    var brightest = Math.Max(red, Math.Max(green, blue));
                    var darkest = Math.Min(red, Math.Min(green, blue));
                    if (brightest > 85 && brightest - darkest > 32)
                    {
                        colorGroup = GetColorGroup(red, green, blue);
                        if (colorGroup == 0 &&
                            red > 110 && blue > 65 && red > green + 42 && blue > green + 24)
                        {
                            colorGroup = 5;
                        }
                        else if (colorGroup == 5 &&
                            !(red > 110 && blue > 65 && red > green + 42 && blue > green + 24))
                        {
                            colorGroup = -1;
                        }
                    }
                }

                if (colorGroup != activeGroup)
                {
                    if (activeGroup >= 0)
                    {
                        if (x - activeStart > longestEnds[activeGroup] - longestStarts[activeGroup])
                        {
                            longestStarts[activeGroup] = activeStart;
                            longestEnds[activeGroup] = x;
                        }
                    }

                    activeGroup = colorGroup;
                    activeStart = colorGroup >= 0 ? x : -1;
                }
            }

            for (var group = 0; group < pointsByColor.Length; group++)
            {
                if (longestEnds[group] - longestStarts[group] >= minimumRun)
                {
                    for (var pointX = longestStarts[group]; pointX < longestEnds[group]; pointX += sampleStep)
                    {
                        pointsByColor[group].Add(new Point(pointX, y));
                    }
                }
            }
        }

        var order = new[] { 5, 1, 2, 3, 4, 0 };
        return order.Select(group => pointsByColor[group]).ToArray();
    }

    private static (int FirstX, int LastX, float Slope, float FirstY)? FindAnchor(
        List<Point> points, int width, int height)
    {
        const float minimumSlope = MinimumSlope;
        const float maximumSlope = -0.015f;
        var slopeCount = (int)MathF.Round((maximumSlope - MinimumSlope) / SlopeStep) + 1;
        var referenceX = width * 0.05f;
        var votes = new int[slopeCount * height];
        for (var slopeIndex = 0; slopeIndex < slopeCount; slopeIndex++)
        {
            var slope = minimumSlope + slopeIndex * SlopeStep;
            var baseIndex = slopeIndex * height;
            foreach (var point in points)
            {
                var referenceY = (int)Math.Round(point.Y - slope * (point.X - referenceX));
                if ((uint)referenceY < (uint)height)
                {
                    votes[baseIndex + referenceY]++;
                }
            }
        }

        var bestVote = 0;
        var bestSlopeIndex = 0;
        var bestReferenceY = 0;
        for (var slopeIndex = 0; slopeIndex < slopeCount; slopeIndex++)
        {
            var baseIndex = slopeIndex * height;
            for (var referenceY = (int)(height * 0.08f); referenceY < height * 0.98f; referenceY++)
            {
                var vote = votes[baseIndex + referenceY];
                if (vote > bestVote)
                {
                    bestVote = vote;
                    bestSlopeIndex = slopeIndex;
                    bestReferenceY = referenceY;
                }
            }
        }

        var candidateSlope = minimumSlope + bestSlopeIndex * SlopeStep;
        var inliers = points.Where(point =>
            Math.Abs(point.Y - (bestReferenceY + candidateSlope * (point.X - referenceX))) <= 3).ToArray();
        if (inliers.Length < 40)
        {
            return null;
        }

        var firstX = inliers.Min(point => point.X);
        var lastX = inliers.Max(point => point.X);
        var span = lastX - firstX + 1;
        if (span <= 0 || inliers.Length / (float)span < 0.1f)
        {
            return null;
        }

        var meanX = inliers.Average(point => point.X);
        var meanY = inliers.Average(point => point.Y);
        var variance = inliers.Sum(point => Math.Pow(point.X - meanX, 2));
        if (variance == 0)
        {
            return null;
        }

        var fittedSlope = inliers.Sum(point => (point.X - meanX) * (point.Y - meanY)) / variance;
        if (fittedSlope is < MinimumSlope or > maximumSlope)
        {
            return null;
        }

        var firstY = (float)(meanY + fittedSlope * (firstX - meanX));
        return (firstX, lastX, (float)fittedSlope, firstY);
    }

    private static List<Candidate> FindCandidates(
        byte[] luminance, int width, int height, int anchorX, float anchorY, float slope,
        int leftStart, int leftEnd, int minimumSide, int maximumSide, float nominalSide, bool wideFrame)
    {
        var candidates = new List<Candidate>();
        var maximumTopOffset = Math.Max(25, (int)(maximumSide * 0.8f));
        for (var left = leftStart; left <= leftEnd; left++)
        {
            var lineY = anchorY + slope * (left - anchorX);
            var minimumTopOffset = wideFrame ? Math.Max(4, minimumSide * 0.05f) : 4;
            var firstTop = Math.Max(0, (int)Math.Ceiling(lineY + minimumTopOffset));
            var lastTop = Math.Min(height - minimumSide - 2,
                (int)Math.Floor(lineY + (wideFrame ? Math.Max(minimumTopOffset + 8, maximumSide * 0.22f) : maximumTopOffset)));
            for (var top = firstTop; top <= lastTop; top++)
            {
                for (var side = minimumSide; side <= maximumSide && left + side < width; side++)
                {
                    if (top + side + (int)Math.Ceiling(Math.Abs(slope) * side) >= height)
                    {
                        continue;
                    }

                    var score = ScoreSquare(luminance, width, height, left, top, side, slope) -
                        Math.Abs(side - nominalSide) * (wideFrame ? 0.08 : 0);
                    if (score >= 0.32)
                    {
                        candidates.Add(new Candidate(left, top, side, score, slope));
                    }
                }
            }
        }

        var selected = new List<Candidate>(12);
        foreach (var candidate in candidates.OrderByDescending(candidate => candidate.Score))
        {
            if (selected.Any(existing =>
                Math.Abs(existing.Left - candidate.Left) <= 3 &&
                Math.Abs(existing.Top - candidate.Top) <= 3 &&
                Math.Abs(existing.Width - candidate.Width) <= 4))
            {
                continue;
            }

            selected.Add(candidate);
            if (selected.Count == 12)
            {
                break;
            }
        }

        return selected;
    }

    private static double ScoreSquare(byte[] luminance, int width, int height, int left, int top, int side, float slope)
    {
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
        var edgeLight = MaxNearbyLuminance(luminance, width, height, x, y, horizontal);
        var before = PixelLuminance(luminance, width, height, x + (horizontal ? 0 : -3), y + (horizontal ? -3 : 0));
        var after = PixelLuminance(luminance, width, height, x + (horizontal ? 0 : 3), y + (horizontal ? 3 : 0));
        var contrast = edgeLight - (before + after) * 0.5;
        var brightScore = Math.Clamp((edgeLight - 55) / 150d, 0, 1);
        var contrastScore = Math.Clamp((contrast - 2) / 65d, 0, 1);
        total += brightScore * 0.48 + contrastScore * 0.52;
    }

    private static double MaxNearbyLuminance(byte[] luminance, int width, int height, int x, int y, bool horizontal)
    {
        var best = 0d;
        for (var offset = -1; offset <= 1; offset++)
        {
            var px = x + (horizontal ? 0 : offset);
            var py = y + (horizontal ? offset : 0);
            best = Math.Max(best, PixelLuminance(luminance, width, height, px, py));
        }

        return best;
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
        return second.Left > first.Left && gap >= expectedSide * 0.82f && gap <= expectedSide * 1.32f &&
            Math.Abs(first.Width - second.Width) <= expectedSide * 0.2f &&
            Math.Abs(first.Top - second.Top) <= expectedSide * 0.38f;
    }

    private static Rectangle ToSourceBounds(Candidate candidate, float slope, Rectangle sourceBounds)
    {
        var skew = (int)Math.Round(slope * (candidate.Width - 1));
        var minTop = Math.Max(0, candidate.Top + Math.Min(0, skew) - 1);
        var maxBottom = Math.Min(sourceBounds.Height, candidate.Top + Math.Max(0, skew) + candidate.Width + 1);
        var left = Math.Max(0, candidate.Left - 1);
        var right = Math.Min(sourceBounds.Width, candidate.Left + candidate.Width + 1);
        return new Rectangle(
            sourceBounds.Left + left,
            sourceBounds.Top + minTop,
            right - left,
            maxBottom - minTop);
    }

    private static int GetColorGroup(byte red, byte green, byte blue)
    {
        if (red >= green && red >= blue)
        {
            return blue > green ? 5 : 0;
        }

        if (green >= blue)
        {
            return red > blue ? 1 : 2;
        }

        return green > red ? 3 : 4;
    }

    private readonly record struct Candidate(int Left, int Top, int Width, double Score, float Slope);
}

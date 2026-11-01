namespace D2AbilityTracker;

internal readonly record struct TwinSideReading(float? Top, float? Bottom, float? Line, float LineSupport);

internal readonly record struct TwinReading(TwinSideReading Left, TwinSideReading Right);

internal readonly record struct TwinSideGuide(float Top, float Bottom);

/// <summary>Learned slot borders plus the cooldown model's expected boundary, in raw border-relative progress.</summary>
internal readonly record struct TwinGuide(
    TwinSideGuide Left,
    TwinSideGuide Right,
    float? PredictedRawProgress = null,
    float RawTolerance = 1);

/// <summary>
/// Geometric model of an ability slot. The recharge boundary is a bright ridge with brighter fill below it that
/// travels linearly from the slot's bottom border to its top border. Both borders and the boundary are slanted with
/// the HUD, so each side strip is measured separately and expressed relative to that strip's own borders.
/// </summary>
internal static class HudTwin
{
    private const float MinimumBorderRidge = 0.06f;
    private const float MinimumLineRidge = 0.04f;
    private const float MinimumFillStep = 0.015f;
    private const float MinimumLineSupport = 0.5f;

    public static unsafe TwinReading Read(byte* origin, int stride, int width, int height,
        int topPadding, int bottomPadding, TwinGuide? guide)
    {
        var rows = height + topPadding + bottomPadding;
        if (rows < 12 || width < 12)
        {
            return default;
        }

        var left = ReadSide(origin, stride, width, height, topPadding, rows,
            (int)(width * 0.07f), Math.Max((int)(width * 0.07f) + 2, (int)(width * 0.22f)), guide?.Left, guide);
        var right = ReadSide(origin, stride, width, height, topPadding, rows,
            (int)(width * 0.78f), Math.Max((int)(width * 0.78f) + 2, (int)(width * 0.93f)), guide?.Right, guide);
        return new TwinReading(left, right);
    }

    public static float? RawProgress(float? line, TwinSideGuide borders)
    {
        var span = borders.Bottom - borders.Top;
        return line is { } row && span > 4 ? (borders.Bottom - row) / span : null;
    }

    private static unsafe TwinSideReading ReadSide(byte* origin, int stride, int width, int height, int topPadding,
        int rows, int start, int end, TwinSideGuide? borders, TwinGuide? guide)
    {
        end = Math.Min(end, width);
        var columns = end - start;
        if (columns < 2)
        {
            return default;
        }

        Span<float> luminance = rows <= 512 ? stackalloc float[rows] : new float[rows];
        Span<float> tops = stackalloc float[columns];
        Span<float> bottoms = stackalloc float[columns];
        Span<float> lines = stackalloc float[columns];
        var topCount = 0;
        var bottomCount = 0;
        var lineCount = 0;
        var top0 = topPadding;
        var topSearchStart = 2;
        var topSearchEnd = top0 + (int)(height * 0.3f);
        var bottomSearchStart = top0 + (int)(height * 0.7f);
        var bottomSearchEnd = rows - 3;
        for (var x = start; x < end; x++)
        {
            for (var y = 0; y < rows; y++)
            {
                var pixel = origin + (y - topPadding) * (long)stride + x * 4;
                luminance[y] = (0.2126f * pixel[2] + 0.7152f * pixel[1] + 0.0722f * pixel[0]) / 255f;
            }

            if (FindRidge(luminance, topSearchStart, topSearchEnd, MinimumBorderRidge) is { } top)
            {
                tops[topCount++] = top - top0;
            }

            if (FindRidge(luminance, bottomSearchStart, bottomSearchEnd, MinimumBorderRidge) is { } bottom)
            {
                bottoms[bottomCount++] = bottom - top0;
            }

            var lineStart = 4;
            var lineEnd = rows - 6;
            if (borders is { } known)
            {
                lineStart = Math.Max(lineStart, (int)MathF.Ceiling(known.Top + top0 + 2));
                lineEnd = Math.Min(lineEnd, (int)MathF.Floor(known.Bottom + top0 - 1.5f));
                if (guide?.PredictedRawProgress is { } predicted)
                {
                    var span = known.Bottom - known.Top;
                    var expected = known.Bottom + top0 - predicted * span;
                    var tolerance = guide.Value.RawTolerance * span + 1.5f;
                    lineStart = Math.Max(lineStart, (int)MathF.Floor(expected - tolerance));
                    lineEnd = Math.Min(lineEnd, (int)MathF.Ceiling(expected + tolerance));
                }
            }

            if (FindBoundary(luminance, lineStart, lineEnd) is { } line)
            {
                lines[lineCount++] = line - top0;
            }
        }

        var lineRow = Consensus(lines[..lineCount], columns, out var support);
        return new TwinSideReading(
            topCount >= columns / 2 ? Median(tops[..topCount]) : null,
            bottomCount >= columns / 2 ? Median(bottoms[..bottomCount]) : null,
            support >= MinimumLineSupport ? lineRow : null,
            support);
    }

    private static float? FindRidge(ReadOnlySpan<float> values, int start, int end, float minimum)
    {
        start = Math.Max(start, 2);
        end = Math.Min(end, values.Length - 2);
        var best = minimum;
        var bestRow = -1;
        for (var y = start; y < end; y++)
        {
            var ridge = values[y] - (values[y - 2] + values[y + 2]) / 2;
            if (ridge > best)
            {
                best = ridge;
                bestRow = y;
            }
        }

        return bestRow < 0 ? null : bestRow + SubPixel(values, bestRow, static (v, y) => v[y] - (v[y - 2] + v[y + 2]) / 2);
    }

    private static float? FindBoundary(ReadOnlySpan<float> values, int start, int end)
    {
        start = Math.Max(start, 5);
        end = Math.Min(end, values.Length - 6);
        var best = 0f;
        var bestRow = -1;
        for (var y = start; y < end; y++)
        {
            var ridge = values[y] - (values[y - 2] + values[y + 2]) / 2;
            if (ridge < MinimumLineRidge)
            {
                continue;
            }

            var fill = (values[y + 3] + values[y + 4] + values[y + 5] - values[y - 4] - values[y - 3] - values[y - 2]) / 3;
            if (fill < MinimumFillStep)
            {
                continue;
            }

            var score = ridge + fill * 0.5f;
            if (score > best)
            {
                best = score;
                bestRow = y;
            }
        }

        return bestRow < 0 ? null : bestRow + SubPixel(values, bestRow, static (v, y) => v[y] - (v[y - 2] + v[y + 2]) / 2);
    }

    private delegate float RowScore(ReadOnlySpan<float> values, int row);

    private static float SubPixel(ReadOnlySpan<float> values, int row, RowScore score)
    {
        if (row < 3 || row >= values.Length - 3)
        {
            return 0;
        }

        var above = score(values, row - 1);
        var center = score(values, row);
        var below = score(values, row + 1);
        var denominator = above - 2 * center + below;
        return MathF.Abs(denominator) < 1e-6f ? 0 : Math.Clamp(0.5f * (above - below) / denominator, -0.5f, 0.5f);
    }

    private static float? Consensus(Span<float> rows, int columns, out float support)
    {
        support = 0;
        if (rows.Length == 0)
        {
            return null;
        }

        var median = Median(rows);
        var total = 0f;
        var inliers = 0;
        foreach (var row in rows)
        {
            if (MathF.Abs(row - median) <= 2)
            {
                total += row;
                inliers++;
            }
        }

        support = inliers / (float)columns;
        return total / inliers;
    }

    private static float Median(Span<float> values)
    {
        values.Sort();
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}

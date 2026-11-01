using System.Drawing;

namespace D2AbilityTracker;

internal static class AbilitySlotRegionFinder
{
    private const float MinimumSlope = -0.15f;
    private const float SlopeStep = 0.005f;
    private const float SlotWidthRatio = 0.135f;
    private const float SlotSpacingRatio = 1.075f;

    public static Rectangle[] Find(Bitmap preview, Rectangle sourceBounds)
    {
        var points = new List<Point>();
        var minimumY = (int)(preview.Height * 0.15f);
        var maximumY = (int)(preview.Height * 0.97f);
        for (var y = minimumY; y < maximumY; y++)
        {
            for (var x = 0; x < preview.Width; x++)
            {
                var color = preview.GetPixel(x, y);
                if (color.R > 120 && color.B > 80 &&
                    color.R > color.G + 55 && color.B > color.G + 35)
                {
                    points.Add(new Point(x, y));
                }
            }
        }

        if (points.Count < 80)
        {
            return [];
        }

        const float minimumSlope = MinimumSlope;
        var slopeCount = (int)(-2 * minimumSlope / SlopeStep) + 1;
        var referenceX = preview.Width * 0.05f;
        var votes = new int[slopeCount * preview.Height];
        for (var slopeIndex = 0; slopeIndex < slopeCount; slopeIndex++)
        {
            var slope = minimumSlope + slopeIndex * SlopeStep;
            foreach (var point in points)
            {
                var referenceY = (int)Math.Round(point.Y - slope * (point.X - referenceX));
                if (referenceY >= 0 && referenceY < preview.Height)
                {
                    votes[slopeIndex * preview.Height + referenceY]++;
                }
            }
        }

        var bestVote = 0;
        var bestSlopeIndex = 0;
        var bestReferenceY = 0;
        for (var slopeIndex = 0; slopeIndex < slopeCount; slopeIndex++)
        {
            for (var referenceY = minimumY; referenceY < maximumY; referenceY++)
            {
                var vote = votes[slopeIndex * preview.Height + referenceY];
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
        if (inliers.Length < 60)
        {
            return [];
        }

        var firstX = inliers.Min(point => point.X);
        var lastX = inliers.Max(point => point.X);
        var span = lastX - firstX + 1;
        var minimumSpan = Math.Max(60, preview.Width * 0.12f);
        if (span < minimumSpan || span > preview.Width * 0.9f ||
            firstX > preview.Width * 0.5f || inliers.Length / (float)span < 1.5f)
        {
            return [];
        }

        var meanX = inliers.Average(point => point.X);
        var meanY = inliers.Average(point => point.Y);
        var variance = inliers.Sum(point => Math.Pow(point.X - meanX, 2));
        if (variance == 0)
        {
            return [];
        }

        var slopeFit = inliers.Sum(point => (point.X - meanX) * (point.Y - meanY)) / variance;
        if (slopeFit is < MinimumSlope or > -MinimumSlope)
        {
            return [];
        }

        var lineYAtFirstX = meanY + slopeFit * (firstX - meanX);
        var slotSide = (int)Math.Round(span * SlotWidthRatio);
        if (slotSide < 12)
        {
            return [];
        }

        var firstSlotTopLeft = lineYAtFirstX + 8;
        var slotStep = slotSide * SlotSpacingRatio;
        var regions = new Rectangle[3];
        for (var index = 0; index < regions.Length; index++)
        {
            var left = (int)Math.Round(firstX + index * slotStep);
            var topLeft = firstSlotTopLeft + index * slotStep * slopeFit;
            var top = (int)Math.Floor(topLeft + slopeFit * slotSide);
            var width = slotSide + (int)Math.Ceiling(Math.Abs(slopeFit) * slotSide);
            var height = slotSide + (int)Math.Ceiling(Math.Abs(slopeFit) * slotSide);
            var localRegion = new Rectangle(left, top, width, height);
            if (!new Rectangle(Point.Empty, preview.Size).Contains(localRegion))
            {
                return [];
            }

            regions[index] = new Rectangle(
                sourceBounds.Left + localRegion.Left,
                sourceBounds.Top + localRegion.Top,
                localRegion.Width,
                localRegion.Height);
        }

        return regions;
    }
}

namespace D2AbilityTracker;

/// <summary>
/// Whole-cycle robust linear model of recharge progress, progress(t) = intercept + slope * t, where t is seconds since
/// the observed recharge start. Every sample of the cycle is retained so the model survives HUD obstructions and
/// recovers immediately; false edges are rejected with a Theil–Sen seed followed by trimmed least squares.
/// </summary>
internal sealed class RechargeModel
{
    private const double MinimumResidualScale = 0.004;
    private const double CalibrationUncertainty = 0.0025;
    private readonly List<(double Seconds, double Progress)> _samples = [];
    private int _fittedCount;
    private double _slope;
    private double _intercept;
    private double _sigma;
    private double _slopeVariance;
    private double _interceptVariance;
    private double _covariance;
    private double _minimumProgress;
    private double _maximumProgress;
    private double _firstSeconds;
    private double _lastSeconds;
    private int _inliers;

    public bool IsReady { get; private set; }
    public int SampleCount => _samples.Count;
    public double ObservedMaximumProgress => IsReady ? _maximumProgress : 0;
    public double ObservedSpanSeconds => IsReady ? _lastSeconds - _firstSeconds : 0;

    public void Reset()
    {
        _samples.Clear();
        _fittedCount = 0;
        IsReady = false;
    }

    public void Add(double seconds, double progress)
    {
        if (!double.IsFinite(seconds) || !double.IsFinite(progress))
        {
            return;
        }

        _samples.Add((seconds, progress));
        // Refits are linear-time; throttle them as the history grows so long cycles stay cheap at high capture rates.
        if (_samples.Count < 64 || _samples.Count - _fittedCount >= Math.Max(4, _samples.Count / 64))
        {
            Fit();
        }
    }

    public double? Predict(double seconds) => IsReady ? _intercept + _slope * seconds : null;

    /// <summary>Gate half-width, in progress units, for accepting a new sample against the prediction.</summary>
    public double? Tolerance(double seconds)
    {
        if (!IsReady)
        {
            return null;
        }

        var predictionSigma = Math.Sqrt(Math.Max(0, _interceptVariance + seconds * seconds * _slopeVariance +
            2 * seconds * _covariance));
        return Math.Max(0.03, 5 * _sigma + 3 * predictionSigma);
    }

    /// <summary>Seconds since start at which the fitted progress reaches <paramref name="progress"/>.</summary>
    public double? SecondsAt(double progress) =>
        IsReady && _slope > 0 ? (progress - _intercept) / _slope : null;

    /// <summary>One-sigma uncertainty of <see cref="SecondsAt"/>, including extrapolation and calibration terms.</summary>
    public double? SecondsUncertaintyAt(double progress)
    {
        if (SecondsAt(progress) is not { } seconds)
        {
            return null;
        }

        var predictionVariance = _interceptVariance + seconds * seconds * _slopeVariance + 2 * seconds * _covariance;
        var statistical = Math.Sqrt(Math.Max(0, predictionVariance)) / _slope;
        var duration = 1 / _slope;
        var gap = progress > _maximumProgress ? progress - _maximumProgress :
            progress < _minimumProgress ? _minimumProgress - progress : 0;
        var extrapolation = 0.03 * gap * duration;
        var calibration = CalibrationUncertainty * duration;
        return Math.Sqrt(statistical * statistical + extrapolation * extrapolation + calibration * calibration);
    }

    private void Fit()
    {
        _fittedCount = _samples.Count;
        IsReady = false;
        if (_samples.Count < 12)
        {
            return;
        }

        // Theil–Sen seed over half-separated pairs tolerates a large fraction of false-edge samples.
        var count = _samples.Count;
        var half = count / 2;
        var slopes = new double[half];
        var slopeCount = 0;
        for (var index = 0; index < half; index++)
        {
            var first = _samples[index];
            var second = _samples[index + half];
            var dt = second.Seconds - first.Seconds;
            if (dt > 1e-6)
            {
                slopes[slopeCount++] = (second.Progress - first.Progress) / dt;
            }
        }

        if (slopeCount < 6)
        {
            return;
        }

        var slope = Median(slopes.AsSpan(0, slopeCount));
        var offsets = new double[count];
        for (var index = 0; index < count; index++)
        {
            offsets[index] = _samples[index].Progress - slope * _samples[index].Seconds;
        }

        var intercept = Median(offsets);
        var residuals = new double[count];
        var scale = 0d;
        var inliers = 0;
        double sumT = 0, sumP = 0, sumTT = 0, sumTP = 0;
        for (var iteration = 0; iteration < 4; iteration++)
        {
            for (var index = 0; index < count; index++)
            {
                residuals[index] = Math.Abs(_samples[index].Progress - intercept - slope * _samples[index].Seconds);
            }

            var sorted = (double[])residuals.Clone();
            scale = Math.Max(MinimumResidualScale, 1.4826 * Median(sorted));
            var limit = Math.Max(3 * scale, 0.01);
            sumT = sumP = sumTT = sumTP = 0;
            inliers = 0;
            for (var index = 0; index < count; index++)
            {
                if (residuals[index] > limit)
                {
                    continue;
                }

                var (t, p) = _samples[index];
                sumT += t;
                sumP += p;
                sumTT += t * t;
                sumTP += t * p;
                inliers++;
            }

            var determinant = inliers * sumTT - sumT * sumT;
            if (inliers < 8 || determinant <= 1e-9)
            {
                return;
            }

            slope = (inliers * sumTP - sumT * sumP) / determinant;
            intercept = (sumP - slope * sumT) / inliers;
        }

        if (slope <= 0 || !double.IsFinite(slope))
        {
            return;
        }

        double minP = double.MaxValue, maxP = double.MinValue, minT = double.MaxValue, maxT = double.MinValue;
        var squared = 0d;
        var limitFinal = Math.Max(3 * scale, 0.01);
        for (var index = 0; index < count; index++)
        {
            var (t, p) = _samples[index];
            var residual = p - intercept - slope * t;
            if (Math.Abs(residual) > limitFinal)
            {
                continue;
            }

            squared += residual * residual;
            minP = Math.Min(minP, p);
            maxP = Math.Max(maxP, p);
            minT = Math.Min(minT, t);
            maxT = Math.Max(maxT, t);
        }

        var span = maxT - minT;
        if (span < 1 || maxP - minP < 0.04)
        {
            return;
        }

        _sigma = Math.Max(MinimumResidualScale, Math.Sqrt(squared / Math.Max(1, inliers - 2)));
        // The boundary advances in whole-pixel steps, so residuals are correlated for about one step. Inflate the
        // ordinary least-squares variance to the effective number of independent samples.
        var stepSeconds = Math.Max(1d / 60, 1 / (slope * 60));
        var effective = Math.Clamp(span / stepSeconds, 2, inliers);
        var inflation = inliers / effective;
        var determinantFinal = inliers * sumTT - sumT * sumT;
        _slopeVariance = _sigma * _sigma * inliers / determinantFinal * inflation;
        _interceptVariance = _sigma * _sigma * sumTT / determinantFinal * inflation;
        _covariance = -_sigma * _sigma * sumT / determinantFinal * inflation;
        _slope = slope;
        _intercept = intercept;
        _minimumProgress = minP;
        _maximumProgress = maxP;
        _firstSeconds = minT;
        _lastSeconds = maxT;
        _inliers = inliers;
        IsReady = _inliers >= 8;
    }

    private static double Median(Span<double> values)
    {
        values.Sort();
        var middle = values.Length / 2;
        return values.Length % 2 == 1 ? values[middle] : (values[middle - 1] + values[middle]) / 2;
    }
}

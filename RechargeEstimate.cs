namespace D2AbilityTracker;

internal sealed class RechargeEstimate
{
    private readonly Queue<(double Seconds, double Progress)> _samples = [];
    public TimeSpan? TotalDuration { get; private set; }
    public double? Confidence { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        TotalDuration = null;
        Confidence = null;
    }

    public void Observe(TimeSpan elapsed, float progress)
    {
        if (_samples.TryPeek(out _) && elapsed.TotalSeconds - _samples.Last().Seconds < 0.1)
        {
            return;
        }

        if (_samples.Count > 0 && elapsed.TotalSeconds - _samples.Last().Seconds > 0.5)
        {
            Reset();
        }

        _samples.Enqueue((elapsed.TotalSeconds, progress));
        while (_samples.Count > 32)
        {
            _samples.Dequeue();
        }

        TotalDuration = null;
        Confidence = null;
        if (_samples.Count < 8)
        {
            return;
        }

        var first = _samples.Peek();
        var last = _samples.Last();
        var span = last.Seconds - first.Seconds;
        var coverage = last.Progress - first.Progress;
        if (span < 1 || coverage < 0.12)
        {
            return;
        }

        var meanTime = _samples.Average(sample => sample.Seconds);
        var meanProgress = _samples.Average(sample => sample.Progress);
        var timeVariance = 0d;
        var progressVariance = 0d;
        var covariance = 0d;
        foreach (var sample in _samples)
        {
            timeVariance += Math.Pow(sample.Seconds - meanTime, 2);
            progressVariance += Math.Pow(sample.Progress - meanProgress, 2);
            covariance += (sample.Seconds - meanTime) * (sample.Progress - meanProgress);
        }

        if (timeVariance <= 0 || progressVariance <= 0 || covariance <= 0)
        {
            return;
        }

        var slope = covariance / timeVariance;
        var error = _samples.Sum(sample =>
            Math.Pow(sample.Progress - (meanProgress + slope * (sample.Seconds - meanTime)), 2));
        var fit = Math.Clamp(1 - error / progressVariance, 0, 1);
        if (fit < 0.9 || Math.Sqrt(error / _samples.Count) > 0.03)
        {
            return;
        }

        var seconds = last.Seconds + (1 - last.Progress) / slope;
        if (!double.IsFinite(seconds) || seconds < elapsed.TotalSeconds || seconds > TimeSpan.MaxValue.TotalSeconds)
        {
            return;
        }

        TotalDuration = TimeSpan.FromSeconds(seconds);
        Confidence = Math.Min(0.95, fit * Math.Min(1, coverage / 0.3) * Math.Min(1, span / 2));
    }
}

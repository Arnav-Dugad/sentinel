namespace Sentinel.Analytics;

public readonly record struct RegressionResult(double Slope, double Intercept, double RSquared, int N)
{
    public double Predict(double x) => Intercept + Slope * x;
}

public readonly record struct ChangePoint(int Index, double MeanBefore, double MeanAfter, double Score);

/// <summary>Robust, allocation-light statistics used by baselines and anomaly detection.</summary>
public static class Statistics
{
    public static double Mean(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return double.NaN;
        double s = 0;
        for (var i = 0; i < v.Count; i++) s += v[i];
        return s / v.Count;
    }

    public static double StdDev(IReadOnlyList<double> v)
    {
        if (v.Count < 2) return 0;
        var m = Mean(v);
        double s = 0;
        for (var i = 0; i < v.Count; i++) s += (v[i] - m) * (v[i] - m);
        return Math.Sqrt(s / (v.Count - 1));
    }

    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var sorted = values.Where(x => !double.IsNaN(x)).Order().ToArray();
        if (sorted.Length == 0) return double.NaN;
        var rank = Math.Clamp(p, 0, 1) * (sorted.Length - 1);
        var lo = (int)Math.Floor(rank);
        var hi = (int)Math.Ceiling(rank);
        return sorted[lo] + (sorted[hi] - sorted[lo]) * (rank - lo);
    }

    public static double Median(IReadOnlyList<double> v) => Percentile(v, 0.5);

    /// <summary>Median absolute deviation (unscaled).</summary>
    public static double Mad(IReadOnlyList<double> v)
    {
        if (v.Count == 0) return double.NaN;
        var med = Median(v);
        var dev = new double[v.Count];
        for (var i = 0; i < v.Count; i++) dev[i] = Math.Abs(v[i] - med);
        return Median(dev);
    }

    /// <summary>Robust z-score using median and MAD (scaled by 1.4826).</summary>
    public static double RobustZ(double x, double median, double mad, double floor = 1e-6) =>
        (x - median) / Math.Max(mad * 1.4826, floor);

    public static double ZScore(double x, double mean, double sd) => sd <= 0 ? 0 : (x - mean) / sd;

    public static RegressionResult LinearRegression(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 2) return new RegressionResult(0, n == 1 ? y[0] : 0, 0, n);
        double sx = 0, sy = 0, sxx = 0, sxy = 0, syy = 0;
        for (var i = 0; i < n; i++)
        {
            sx += x[i];
            sy += y[i];
            sxx += x[i] * x[i];
            sxy += x[i] * y[i];
            syy += y[i] * y[i];
        }
        var denom = n * sxx - sx * sx;
        if (Math.Abs(denom) < 1e-12) return new RegressionResult(0, sy / n, 0, n);
        var slope = (n * sxy - sx * sy) / denom;
        var intercept = (sy - slope * sx) / n;
        var ssTot = syy - sy * sy / n;
        double ssRes = 0;
        for (var i = 0; i < n; i++)
        {
            var e = y[i] - (intercept + slope * x[i]);
            ssRes += e * e;
        }
        var r2 = ssTot <= 1e-12 ? 0 : Math.Clamp(1 - ssRes / ssTot, 0, 1);
        return new RegressionResult(slope, intercept, r2, n);
    }

    /// <summary>
    /// Single change-point detection: the split that maximises the between-segment mean difference
    /// relative to pooled noise (a CUSUM-style statistic). Returns null when no meaningful split exists.
    /// </summary>
    public static ChangePoint? DetectChangePoint(IReadOnlyList<double> v, int minSegment = 5, double minScore = 3.0)
    {
        if (v.Count < minSegment * 2) return null;
        var prefix = new double[v.Count + 1];
        for (var i = 0; i < v.Count; i++) prefix[i + 1] = prefix[i] + v[i];
        var sigma = Math.Max(Mad(v) * 1.4826, StdDev(v) * 0.25);
        if (sigma <= 1e-9) return null;
        ChangePoint? best = null;
        for (var k = minSegment; k <= v.Count - minSegment; k++)
        {
            var m1 = prefix[k] / k;
            var m2 = (prefix[v.Count] - prefix[k]) / (v.Count - k);
            // t-like statistic for the difference of means
            var score = Math.Abs(m2 - m1) / (sigma * Math.Sqrt(1.0 / k + 1.0 / (v.Count - k)));
            if (best is null || score > best.Value.Score) best = new ChangePoint(k, m1, m2, score);
        }
        return best is { } b && b.Score >= minScore ? b : null;
    }

    /// <summary>Upper tail probability P(X ≥ k) for a Poisson distribution with rate λ.</summary>
    public static double PoissonUpperTail(int k, double lambda)
    {
        if (k <= 0) return 1;
        if (lambda <= 0) return 0;
        double term = Math.Exp(-lambda), cdf = term;
        for (var i = 1; i < k; i++)
        {
            term *= lambda / i;
            cdf += term;
        }
        return Math.Clamp(1 - cdf, 0, 1);
    }
}

/// <summary>Exponentially weighted moving average with variance, for smoothing live signals.</summary>
public sealed class Ewma(double alpha)
{
    private bool _initialised;

    public double Mean { get; private set; }
    public double Variance { get; private set; }
    public double StdDev => Math.Sqrt(Variance);

    public double Update(double x)
    {
        if (double.IsNaN(x)) return Mean;
        if (!_initialised)
        {
            Mean = x;
            Variance = 0;
            _initialised = true;
            return Mean;
        }
        var diff = x - Mean;
        var incr = alpha * diff;
        Mean += incr;
        Variance = (1 - alpha) * (Variance + diff * incr);
        return Mean;
    }

    public void Reset() => _initialised = false;
}

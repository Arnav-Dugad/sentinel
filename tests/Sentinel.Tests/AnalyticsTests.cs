using Sentinel.Analytics;
using Xunit;

namespace Sentinel.Tests;

public class StatisticsTests
{
    [Fact]
    public void MedianAndMadAreRobustToOutliers()
    {
        double[] v = [10, 11, 10, 12, 11, 10, 500];
        Assert.Equal(11, Statistics.Median(v));
        Assert.Equal(1, Statistics.Mad(v));
        Assert.True(Statistics.RobustZ(500, 11, 1) > 100);
    }

    [Fact]
    public void PercentileInterpolates()
    {
        double[] v = [0, 10, 20, 30, 40];
        Assert.Equal(20, Statistics.Percentile(v, 0.5));
        Assert.Equal(36, Statistics.Percentile(v, 0.9), 6);
        Assert.True(double.IsNaN(Statistics.Percentile([], 0.5)));
    }

    [Fact]
    public void LinearRegressionRecoversSlope()
    {
        var x = Enumerable.Range(0, 50).Select(i => (double)i).ToList();
        var y = x.Select(v => 3 * v + 7).ToList();
        var fit = Statistics.LinearRegression(x, y);
        Assert.Equal(3, fit.Slope, 6);
        Assert.Equal(7, fit.Intercept, 6);
        Assert.Equal(1, fit.RSquared, 6);
    }

    [Fact]
    public void ChangePointFindsStepChange()
    {
        var values = Enumerable.Repeat(30.0, 20).Concat(Enumerable.Repeat(45.0, 15)).Select((v, i) => v + (i % 3) * 0.5).ToList();
        var cp = Statistics.DetectChangePoint(values);
        Assert.NotNull(cp);
        Assert.InRange(cp!.Value.Index, 18, 22);
        Assert.True(cp.Value.MeanAfter - cp.Value.MeanBefore > 10);
    }

    [Fact]
    public void ChangePointIgnoresNoise()
    {
        var rng = new Random(1);
        var values = Enumerable.Range(0, 40).Select(_ => 50 + rng.NextDouble()).ToList();
        Assert.Null(Statistics.DetectChangePoint(values, minScore: 6));
    }

    [Fact]
    public void PoissonTailIsSane()
    {
        Assert.Equal(1, Statistics.PoissonUpperTail(0, 2));
        Assert.True(Statistics.PoissonUpperTail(10, 0.5) < 1e-6);
        Assert.InRange(Statistics.PoissonUpperTail(1, 1), 0.63, 0.64);
    }

    [Fact]
    public void EwmaConverges()
    {
        var e = new Ewma(0.3);
        for (var i = 0; i < 100; i++) e.Update(42);
        Assert.Equal(42, e.Mean, 6);
        Assert.Equal(0, e.StdDev, 6);
    }

    [Fact]
    public void BaselineComputeUsesRobustStatistics()
    {
        var values = Enumerable.Range(0, 200).Select(i => 50.0 + i % 5).ToList();
        var b = BaselineEngine.Compute("thermal.x", "lowload", values, 10, DateTimeOffset.Now);
        Assert.Equal(52, b.Median);
        Assert.True(b.IsMature);
        Assert.InRange(b.P05, 50, 51);
        Assert.InRange(b.P95, 53, 54);
    }

    [Theory]
    [InlineData(5.0, null, 0.0, null, true)]
    [InlineData(40.0, null, 0.0, null, false)]
    [InlineData(5.0, 30.0, 0.0, null, false)]
    public void LowLoadContext(double cpu, double? gpu, double idle, double? ac, bool expected) =>
        Assert.Equal(expected, BaselineContexts.Matches(BaselineContext.LowLoad, cpu, gpu, idle, ac));

    [Fact]
    public void AwayAndBatteryContexts()
    {
        Assert.True(BaselineContexts.Matches(BaselineContext.Away, 2, null, 600, 1));
        Assert.False(BaselineContexts.Matches(BaselineContext.Away, 2, null, 30, 1));
        Assert.True(BaselineContexts.Matches(BaselineContext.OnBattery, 2, null, 0, 0));
        Assert.False(BaselineContexts.Matches(BaselineContext.OnBattery, 2, null, 0, null));
    }
}

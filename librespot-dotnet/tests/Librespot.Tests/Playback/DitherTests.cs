// Tests for playback/src/dither.rs (upstream has no unit tests for it).
using Librespot.Playback;
using Xunit;

namespace Librespot.Tests.Playback;

public class DitherTests
{
    private const int SampleCount = 200_000;

    [Theory]
    [InlineData("tpdf", typeof(TriangularDitherer))]
    [InlineData("gpdf", typeof(GaussianDitherer))]
    [InlineData("tpdf_hp", typeof(HighPassDitherer))]
    public void FindDitherer_finds_by_upstream_name(string name, Type type)
    {
        var builder = Dither.FindDitherer(name);
        Assert.NotNull(builder);
        var ditherer = builder();
        Assert.IsType(type, ditherer);
        Assert.Equal(name, ditherer.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("none")]
    [InlineData("Triangular")]
    [InlineData("")]
    public void FindDitherer_returns_null_for_unknown_names(string? name)
    {
        Assert.Null(Dither.FindDitherer(name));
    }

    [Fact]
    public void Triangular_noise_is_bounded_zero_mean_with_triangular_variance()
    {
        var (min, max, mean, variance) = Stats(new TriangularDitherer());
        Assert.InRange(min, -1.0, 1.0);
        Assert.InRange(max, -1.0, 1.0);
        Assert.InRange(mean, -0.01, 0.01);

        // Var of Triangular(-1, 1, 0) is (a² + b² + c² - ab - ac - bc) / 18 = 1/6.
        Assert.InRange(variance, 1.0 / 6 - 0.005, 1.0 / 6 + 0.005);
    }

    [Fact]
    public void Gaussian_noise_has_zero_mean_and_sigma_0_6()
    {
        var (_, _, mean, variance) = Stats(new GaussianDitherer());
        Assert.InRange(mean, -0.01, 0.01);
        Assert.InRange(Math.Sqrt(variance), 0.59, 0.61);
    }

    [Fact]
    public void HighPass_noise_is_bounded_and_differences_per_channel()
    {
        var (min, max, mean, variance) = Stats(new HighPassDitherer());
        Assert.InRange(min, -1.0, 1.0);
        Assert.InRange(max, -1.0, 1.0);
        Assert.InRange(mean, -0.01, 0.01);

        // Difference of two independent U(-0.5, 0.5): variance 2 * 1/12 = 1/6.
        Assert.InRange(variance, 1.0 / 6 - 0.005, 1.0 / 6 + 0.005);
    }

    [Fact]
    public void HighPass_first_sample_of_each_channel_is_plain_uniform()
    {
        var ditherer = new HighPassDitherer();

        // Previous noise starts at 0, so the first value of both channels is within [-0.5, 0.5].
        Assert.InRange(ditherer.Noise(), -0.5, 0.5);
        Assert.InRange(ditherer.Noise(), -0.5, 0.5);
    }

    [Fact]
    public void MakeDitherer_creates_new_instances()
    {
        var a = Dither.MakeDitherer<TriangularDitherer>();
        var b = Dither.MakeDitherer<TriangularDitherer>();
        Assert.NotSame(a, b);
    }

    private static (double Min, double Max, double Mean, double Variance) Stats(IDitherer ditherer)
    {
        double min = double.MaxValue, max = double.MinValue, sum = 0, sumSq = 0;
        for (var i = 0; i < SampleCount; i++)
        {
            var n = ditherer.Noise();
            min = Math.Min(min, n);
            max = Math.Max(max, n);
            sum += n;
            sumSq += n * n;
        }

        var mean = sum / SampleCount;
        return (min, max, mean, sumSq / SampleCount - mean * mean);
    }
}

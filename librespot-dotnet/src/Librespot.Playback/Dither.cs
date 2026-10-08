// Port of playback/src/dither.rs.
//
// Dithering lowers digital-to-analog conversion ("requantization") error,
// linearizing output, lowering distortion and replacing it with a constant,
// fixed noise level, which is more pleasant to the ear than the distortion.
//
// Guidance:
//
//  * On S24, S24_3 and S24, the default is to use triangular dithering.
//    Depending on personal preference you may use Gaussian dithering instead;
//    it's not as good objectively, but it may be preferred subjectively if
//    you are looking for a more "analog" sound akin to tape hiss.
//
//  * Advanced users who know that they have a DAC without noise shaping have
//    a third option: high-passed dithering, which is like triangular dithering
//    except that it moves dithering noise up in frequency where it is less
//    audible. Note: 99% of DACs are of delta-sigma design with noise shaping,
//    so unless you have a multibit / R2R DAC, or otherwise know what you are
//    doing, this is not for you.
//
//  * Don't dither or shape noise on S32 or F32. On F32 it's not supported
//    anyway (there are no integer conversions and so no rounding errors) and
//    on S32 the noise level is so far down that it is simply inaudible even
//    after volume normalisation and control.
//
// The rand_distr distributions (Triangular, Normal, Uniform) are hand-written below on top of System.Random
// (upstream: SmallRng seeded from the OS). The sequences differ from upstream; the distributions do not.
namespace Librespot.Playback;

/// <summary>Upstream <c>trait Ditherer</c>. Implementations also need a public parameterless constructor (upstream <c>fn new()</c>).</summary>
public interface IDitherer
{
    /// <summary>The ditherer's name as used on the command line (upstream <c>name()</c>, <c>Display</c>).</summary>
    string Name { get; }

    /// <summary>The next noise sample, in LSB units.</summary>
    double Noise();
}

/// <summary>Upstream <c>DithererBuilder = fn() -> Box&lt;dyn Ditherer&gt;</c>.</summary>
public delegate IDitherer DithererBuilder();

/// <summary>Ditherer factory helpers (upstream free functions of <c>dither.rs</c>).</summary>
public static class Dither
{
    /// <summary>Upstream <c>mk_ditherer::&lt;T&gt;</c>.</summary>
    public static IDitherer MakeDitherer<T>() where T : IDitherer, new() => new T();

    /// <summary>Upstream <c>find_ditherer(name)</c>; null for an unknown name (including "none") or a null name.</summary>
    public static DithererBuilder? FindDitherer(string? name) => name switch
    {
        TriangularDitherer.NameConst => MakeDitherer<TriangularDitherer>,
        GaussianDitherer.NameConst => MakeDitherer<GaussianDitherer>,
        HighPassDitherer.NameConst => MakeDitherer<HighPassDitherer>,
        _ => null,
    };

    /// <summary>Upstream <c>create_rng()</c> (<c>SmallRng::from_os_rng()</c>): a fresh, randomly seeded generator.</summary>
    internal static Random CreateRng() => new();

    /// <summary>
    /// <c>rand_distr::Triangular::new(min, max, mode).sample(rng)</c>: inverse-CDF sampling of a triangular
    /// distribution, exactly as rand_distr computes it.
    /// </summary>
    internal static double SampleTriangular(Random rng, double min, double max, double mode)
    {
        var f = rng.NextDouble();
        var diffModeMin = mode - min;
        var range = max - min;
        var fRange = f * range;
        return fRange < diffModeMin
            ? min + Math.Sqrt(fRange * diffModeMin)
            : max - Math.Sqrt((range - fRange) * (max - mode));
    }

    /// <summary><c>rand_distr::Normal::new(mean, std_dev).sample(rng)</c>, implemented with the Box-Muller transform.</summary>
    internal static double SampleNormal(Random rng, double mean, double stdDev)
    {
        // 1 - NextDouble() is in (0, 1], so the logarithm is finite.
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        var standard = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return mean + stdDev * standard;
    }

    /// <summary><c>rand::distributions::Uniform::new_inclusive(low, high).sample(rng)</c>: uniform in [low, high].</summary>
    internal static double SampleUniformInclusive(Random rng, double low, double high)
    {
        // NextDouble() is in [0, 1 - 2^-53]; scale so that its maximum maps to `high` (as rand's UniformFloat does).
        const double maxRand = 1.0 - 1.0 / (1UL << 53);
        var scale = (high - low) / maxRand;
        var value = low + scale * rng.NextDouble();
        return Math.Min(value, high);
    }
}

/// <summary>Triangular (TPDF) dithering: 2 LSB peak-to-peak. Upstream <c>TriangularDitherer</c>.</summary>
public sealed class TriangularDitherer : IDitherer
{
    /// <summary>Upstream <c>TriangularDitherer::NAME</c>.</summary>
    public const string NameConst = "tpdf";

    private readonly Random _cachedRng = Dither.CreateRng();

    /// <inheritdoc />
    public string Name => NameConst;

    /// <inheritdoc />
    public double Noise() =>
        // 2 LSB peak-to-peak needed to linearize the response: Triangular::new(-1.0, 1.0, 0.0)
        Dither.SampleTriangular(_cachedRng, -1.0, 1.0, 0.0);
}

/// <summary>Gaussian dithering. Upstream <c>GaussianDitherer</c>.</summary>
public sealed class GaussianDitherer : IDitherer
{
    /// <summary>Upstream <c>GaussianDitherer::NAME</c>.</summary>
    public const string NameConst = "gpdf";

    // For Gaussian to achieve equivalent decorrelation to triangular dithering, it needs
    // 3-4 dB higher amplitude than TPDF's optimal 0.408 LSB. If optimizing:
    // - minimum correlation: σ ≈ 0.58
    // - perceptual equivalence: σ ≈ 0.65
    // - worst-case performance: σ ≈ 0.70
    //
    // σ = 0.6 LSB is a reasonable compromise that balances mathematical theory with
    // empirical performance across various signal types.
    private const double StdDev = 0.6;

    private readonly Random _cachedRng = Dither.CreateRng();

    /// <inheritdoc />
    public string Name => NameConst;

    /// <inheritdoc />
    public double Noise() => Dither.SampleNormal(_cachedRng, 0.0, StdDev);
}

/// <summary>
/// High-passed triangular dithering: uniform noise differenced with the previous noise of the same channel.
/// Upstream <c>HighPassDitherer</c>.
/// </summary>
public sealed class HighPassDitherer : IDitherer
{
    /// <summary>Upstream <c>HighPassDitherer::NAME</c>.</summary>
    public const string NameConst = "tpdf_hp";

    private readonly double[] _previousNoises = new double[AudioConstants.NumChannels];
    private readonly Random _cachedRng = Dither.CreateRng();
    private int _activeChannel;

    /// <inheritdoc />
    public string Name => NameConst;

    /// <inheritdoc />
    public double Noise()
    {
        // 1 LSB +/- 1 LSB (previous) = 2 LSB: Uniform::new_inclusive(-0.5, 0.5)
        var newNoise = Dither.SampleUniformInclusive(_cachedRng, -0.5, 0.5);
        var highPassedNoise = newNoise - _previousNoises[_activeChannel];
        _previousNoises[_activeChannel] = newNoise;
        _activeChannel ^= 1;
        return highPassedNoise;
    }
}

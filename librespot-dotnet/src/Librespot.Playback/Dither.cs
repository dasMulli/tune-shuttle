// Port of playback/src/dither.rs (contract only).
// TODO(port: playback-pipeline): TriangularDitherer (Triangular(-1,1,0)), GaussianDitherer (Normal(0, 0.5)),
// HighPassDitherer (previous-noise differencing), find_ditherer by name ("tpdf", "gpdf", "tpdf_hp", "none").
namespace Librespot.Playback;

/// <summary>Upstream <c>trait Ditherer</c>.</summary>
public interface IDitherer
{
    string Name { get; }

    /// <summary>The next noise sample, in LSB units.</summary>
    double Noise();
}

/// <summary>Upstream <c>DithererBuilder = fn() -> Box&lt;dyn Ditherer&gt;</c>.</summary>
public delegate IDitherer DithererBuilder();

public static class Dither
{
    /// <summary>Upstream <c>mk_ditherer::&lt;T&gt;</c>.</summary>
    public static IDitherer MakeDitherer<T>() where T : IDitherer, new() => new T();

    /// <summary>Upstream <c>find_ditherer(name)</c>; null for "none" or unknown.</summary>
    public static DithererBuilder? FindDitherer(string? name) => throw new NotImplementedException("TODO(port: playback-pipeline)");
}

public sealed class TriangularDitherer : IDitherer
{
    public const string NameConst = "Triangular";
    public string Name => NameConst;
    public double Noise() => throw new NotImplementedException("TODO(port: playback-pipeline)");
}

public sealed class GaussianDitherer : IDitherer
{
    public const string NameConst = "Gaussian";
    public string Name => NameConst;
    public double Noise() => throw new NotImplementedException("TODO(port: playback-pipeline)");
}

public sealed class HighPassDitherer : IDitherer
{
    public const string NameConst = "Triangular, High Passed";
    public string Name => NameConst;
    public double Noise() => throw new NotImplementedException("TODO(port: playback-pipeline)");
}

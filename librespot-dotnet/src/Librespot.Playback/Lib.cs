// Port of playback/src/lib.rs (crate-level constants).
namespace Librespot.Playback;

public static class AudioConstants
{
    public const uint SampleRate = 44100;
    public const byte NumChannels = 2;
    public const uint SamplesPerSecond = SampleRate * NumChannels;
    public const double PagesPerMs = SampleRate / 1000.0;
    public const double MsPerPage = 1000.0 / SampleRate;
}

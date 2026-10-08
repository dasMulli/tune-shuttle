// Port of playback/src/audio_backend/rodio.rs. rodio + cpal are replaced by NAudio's WASAPI player (Windows only);
// the backend is registered as "naudio" instead of "rodio". The rodiojack variant (JACK host, Linux only) is not ported.
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Librespot.Core.Logging;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace Librespot.Playback.AudioBackend;

/// <summary>
/// Upstream <c>RodioSink</c>: plays through a WASAPI shared-mode stream on the default (or a named) output device.
/// Like rodio, samples are converted to f32 and queued; the stream itself is opened in the configured
/// <see cref="AudioFormat"/> when the device accepts it, otherwise in the device's default format.
/// </summary>
/// <remarks>
/// Lifecycle mirrors rodio: the device is opened when the sink is created and keeps running;
/// <see cref="Start"/> resumes and <see cref="Stop"/> drains the queue and pauses.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NAudioSink : ISink
{
    /// <summary>Backend name used by the registry (upstream <c>RodioSink::NAME</c> is <c>"rodio"</c>).</summary>
    public const string Name = "naudio";

    /// <summary>Requested WASAPI buffer latency in milliseconds.</summary>
    private const int StreamLatencyMs = 100;

    private const string UnknownName = "[unknown name]";

    /// <summary>
    /// Queue length at which <see cref="Write"/> blocks. Upstream waits while more than 26 chunks are queued:
    /// "Assuming they're on average 1628 then a half second buffer is 44100 elements", i.e. ~0.5 s of stereo audio.
    /// </summary>
    private static readonly TimeSpan MaxQueued = TimeSpan.FromMilliseconds(500);

    /// <summary>Upstream sleeps 10 ms between checks of the queue length.</summary>
    private static readonly TimeSpan DrainPollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>Capacity of the sample queue; it only needs to hold <see cref="MaxQueued"/> plus one packet.</summary>
    private static readonly TimeSpan QueueCapacity = TimeSpan.FromSeconds(5);

    private static readonly ILogger Log = LibrespotLog.CreateLogger<NAudioSink>();

    private readonly MMDeviceEnumerator _enumerator;
    private readonly MMDevice _device;
    private readonly BufferedWaveProvider _queue;
    private readonly WasapiPlayer _player;
    private Exception? _streamError;

    private NAudioSink(MMDeviceEnumerator enumerator, MMDevice device, BufferedWaveProvider queue, WasapiPlayer player)
    {
        _enumerator = enumerator;
        _device = device;
        _queue = queue;
        _player = player;
        _player.PlaybackStopped += OnPlaybackStopped;
    }

    /// <summary>
    /// The <see cref="SinkBuilder"/> for this backend (upstream <c>mk_rodio</c> / <c>open</c>). A device of
    /// <c>"?"</c> lists the output devices and exits the process, like upstream.
    /// </summary>
    /// <param name="device">Friendly name of the output device, or null for the default device.</param>
    /// <param name="format">Sample format the output stream is opened with.</param>
    /// <exception cref="SinkException">The device is not available or the stream cannot be opened.</exception>
    public static ISink Open(string? device, AudioFormat format)
    {
        Log.LogInformation("Using NAudio sink with format {Format} and host: {Host}", format, "WASAPI");

        // Upstream unwraps (panics on) a failed create_sink; here the SinkException propagates to the caller.
        var sink = CreateSink(device, format);

        Log.LogDebug("NAudio sink was created");
        return sink;
    }

    /// <summary>Resumes playback (upstream <c>rodio::Sink::play</c>).</summary>
    public void Start()
    {
        ThrowIfStreamFailed();

        try
        {
            if (_player.PlaybackState != PlaybackState.Playing)
            {
                _player.Play();
            }
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            throw StreamError(e);
        }
    }

    /// <summary>Waits until the queued audio has been played, then pauses (upstream <c>sleep_until_end</c> + <c>pause</c>).</summary>
    public void Stop()
    {
        // Deviation: also stop waiting when the stream is not running or failed, where upstream would block forever.
        while (_queue.BufferedBytes > 0 && _player.PlaybackState == PlaybackState.Playing && Volatile.Read(ref _streamError) is null)
        {
            Thread.Sleep(DrainPollInterval);
        }

        try
        {
            _player.Pause();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException)
        {
            throw StreamError(e);
        }
    }

    /// <inheritdoc />
    public void Write(AudioPacket packet, Converter converter)
    {
        double[] samples;
        try
        {
            samples = packet.SamplesOrThrow();
        }
        catch (AudioPacketException e)
        {
            throw SamplesError(e.Detail, e);
        }

        ThrowIfStreamFailed();

        var samplesF32 = converter.F64ToF32(samples);
        try
        {
            _queue.AddSamples(MemoryMarshal.AsBytes(samplesF32.AsSpan()));
        }
        catch (InvalidOperationException e)
        {
            throw PlayError(e);
        }

        // Chunk sizes seem to be about 256 to 3000 ish items long.
        // Assuming they're on average 1628 then a half second buffer is:
        // 44100 elements --> about 27 chunks
        while (_queue.BufferedDuration > MaxQueued && _player.PlaybackState == PlaybackState.Playing)
        {
            // sleep and wait for the stream to drain a bit
            ThrowIfStreamFailed();
            Thread.Sleep(DrainPollInterval);
        }
    }

    /// <summary>Closes the output stream and releases the device (upstream drops the rodio sink and stream).</summary>
    public void Dispose()
    {
        _player.PlaybackStopped -= OnPlaybackStopped;
        _player.Dispose();
        _device.Dispose();
        _enumerator.Dispose();
    }

    private void OnPlaybackStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception is { } error)
        {
            // cpal reports stream errors through its error callback; rodio prints them.
            Log.LogError("an error occurred on output stream: {Error}", error.Message);
            Volatile.Write(ref _streamError, error);
        }
    }

    private void ThrowIfStreamFailed()
    {
        if (Volatile.Read(ref _streamError) is { } error)
        {
            throw StreamError(error);
        }
    }

    /// <summary>Upstream <c>create_sink</c>.</summary>
    private static NAudioSink CreateSink(string? device, AudioFormat format)
    {
        MMDeviceEnumerator enumerator;
        try
        {
            enumerator = new MMDeviceEnumerator();
        }
        catch (COMException)
        {
            // No upstream counterpart (cpal's WASAPI host creates its enumerator infallibly); without the audio
            // endpoint service there is no output device at all, so report it the way a missing default device is.
            throw NoDeviceAvailable();
        }

        NAudioSink sink;
        try
        {
            string deviceId;
            using (var endpoint = SelectDevice(enumerator, device))
            {
                Log.LogInformation("Using audio device: {Name}", TryGetName(endpoint, out _) ?? UnknownName);
                deviceId = endpoint.ID;
            }

            // Like rodio's queue, the queue always holds stereo 44.1 kHz f32 samples.
            var queue = new BufferedWaveProvider(
                WaveFormat.CreateIeeeFloatWaveFormat((int)AudioConstants.SampleRate, AudioConstants.NumChannels),
                QueueCapacity)
            {
                DiscardOnBufferOverflow = false,
                ReadFully = true,
            };

            // First try native stereo 44.1 kHz playback in the requested sample format, then fall back to the
            // device's default config (its mix format), resampling to the device's sample rate when it differs.
            // In shared mode the audio engine adapts bit depth and channel count to the mix format.
            (WasapiPlayer Player, MMDevice Device) stream;
            try
            {
                stream = OpenStream(enumerator, deviceId, _ => WithSampleFormat(queue, format));
            }
            catch (Exception e) when (e is not SinkException)
            {
                Log.LogWarning("unable to create NAudio output, falling back to default: {Error}", e.Message);
                try
                {
                    stream = OpenStream(enumerator, deviceId, player => AtDeviceSampleRate(queue, player.DeviceMixFormat));
                }
                catch (Exception fallbackError) when (fallbackError is not SinkException)
                {
                    throw StreamError(fallbackError);
                }
            }

            sink = new NAudioSink(enumerator, stream.Device, queue, stream.Player);
        }
        catch (Exception e) when (e is not SinkException)
        {
            enumerator.Dispose();
            throw DevicesError(e);
        }
        catch
        {
            enumerator.Dispose();
            throw;
        }

        // A new rodio::Sink starts out playing.
        try
        {
            sink.Start();
        }
        catch
        {
            sink.Dispose();
            throw;
        }

        return sink;
    }

    private static MMDevice SelectDevice(MMDeviceEnumerator enumerator, string? device) => device switch
    {
        "?" => ListOutputsAndExit(enumerator),
        not null => FindDevice(enumerator, device) ?? throw DeviceNotAvailable(device),
        null => DefaultOutputDevice(enumerator) ?? throw NoDeviceAvailable(),
    };

    /// <summary>
    /// The default render device, or null when there is none. Like cpal's <c>default_output_device</c>, a failure
    /// to query it counts as "no default device" rather than as an error.
    /// </summary>
    private static MMDevice? DefaultOutputDevice(MMDeviceEnumerator enumerator)
    {
        try
        {
            return enumerator.TryGetDefaultAudioEndpoint(DataFlow.Render, Role.Console, out var device) ? device : null;
        }
        catch (COMException)
        {
            return null;
        }
    }

    private static (WasapiPlayer Player, MMDevice Device) OpenStream(
        MMDeviceEnumerator enumerator,
        string deviceId,
        Func<WasapiPlayer, IWaveProvider> source)
    {
        // A fresh device wrapper per attempt, so a failed attempt cannot leave a released device behind.
        var device = enumerator.GetDevice(deviceId);
        WasapiPlayer? player = null;
        try
        {
            player = new WasapiPlayerBuilder()
                .WithDevice(device)
                .WithSharedMode()
                .WithLatency(StreamLatencyMs)
                .Build();
            player.Init(source(player));
            return (player, device);
        }
        catch
        {
            player?.Dispose();
            device.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The f32 queue converted to the stream sample format upstream asks cpal for
    /// (<c>F64</c>, <c>F32</c>, <c>I32</c>, <c>I24</c> for both S24 and S24_3, <c>I16</c>).
    /// </summary>
    private static IWaveProvider WithSampleFormat(BufferedWaveProvider queue, AudioFormat format) => format switch
    {
        AudioFormat.F32 => queue,
        AudioFormat.F64 => new F32ConvertingProvider(queue, StreamFormat(64, ieeeFloat: true)),
        AudioFormat.S32 => new F32ConvertingProvider(queue, StreamFormat(32, ieeeFloat: false)),
        AudioFormat.S24 or AudioFormat.S24_3 => new F32ConvertingProvider(queue, StreamFormat(24, ieeeFloat: false)),
        AudioFormat.S16 => new F32ConvertingProvider(queue, StreamFormat(16, ieeeFloat: false)),
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>The f32 queue at the device's sample rate (rodio resamples when the device lacks 44.1 kHz).</summary>
    private static IWaveProvider AtDeviceSampleRate(BufferedWaveProvider queue, WaveFormat deviceFormat) =>
        deviceFormat.SampleRate == (int)AudioConstants.SampleRate
            ? queue
            : new WdlResamplingSampleProvider(queue.ToSampleProvider(), deviceFormat.SampleRate).ToWaveProvider();

    private static WaveFormatExtensible StreamFormat(int bits, bool ieeeFloat)
    {
        const int frontLeftAndRight = 0x1 | 0x2;
        return new WaveFormatExtensible((int)AudioConstants.SampleRate, bits, AudioConstants.NumChannels, ieeeFloat, bits, frontLeftAndRight);
    }

    /// <summary>Ignores devices for which getting the name fails.</summary>
    private static MMDevice? FindDevice(MMDeviceEnumerator enumerator, string deviceName)
    {
        using var devices = EnumerateOutputs(enumerator);
        foreach (var candidate in devices)
        {
            if (TryGetName(candidate, out _) == deviceName)
            {
                return candidate;
            }

            candidate.Dispose();
        }

        return null;
    }

    private static MMDeviceCollection EnumerateOutputs(MMDeviceEnumerator enumerator)
    {
        try
        {
            return enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
        }
        catch (COMException e)
        {
            throw DevicesError(e);
        }
    }

    private static MMDevice ListOutputsAndExit(MMDeviceEnumerator enumerator)
    {
        try
        {
            ListOutputs(enumerator);
        }
        catch (Exception e)
        {
            // Upstream's '?' path always ends the process; never let a listing failure escape as a sink error.
            Log.LogError("{Error}", e is SinkException s ? s.Detail : e.Message);
            Environment.Exit(1);
        }

        Environment.Exit(0);
        throw new InvalidOperationException("unreachable");
    }

    /// <summary>Upstream <c>list_outputs</c>.</summary>
    private static void ListOutputs(MMDeviceEnumerator enumerator)
    {
        string? defaultDeviceName = null;

        if (DefaultOutputDevice(enumerator) is { } defaultDevice)
        {
            using (defaultDevice)
            {
                defaultDeviceName = TryGetName(defaultDevice, out _);
                Console.WriteLine($"Default Audio Device:\n  {defaultDeviceName ?? UnknownName}");

                ListFormats(defaultDevice);
            }

            Console.WriteLine("Other Available Audio Devices:");
        }
        else
        {
            Log.LogWarning("No default device was found");
        }

        using var devices = EnumerateOutputs(enumerator);
        foreach (var device in devices)
        {
            using (device)
            {
                var name = TryGetName(device, out var error);
                if (name is not null && name == defaultDeviceName)
                {
                    continue;
                }

                if (name is not null)
                {
                    Console.WriteLine($"  {name}");
                }
                else
                {
                    Log.LogWarning("Cannot get device name: {Error}", error?.Message);
                    Console.WriteLine($"   {UnknownName}");
                }

                ListFormats(device);
            }
        }
    }

    /// <summary>Upstream <c>list_formats</c>.</summary>
    private static void ListFormats(MMDevice device)
    {
        try
        {
            using var client = device.CreateAudioClient();
            var config = client.MixFormat;
            Log.LogDebug("  Default config:");
            Log.LogDebug("    {Config}", config);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            // Use loglevel debug, since even the output is only debug
            Log.LogDebug("Error getting default NAudio config: {Error}", e.Message);
        }

        // cpal's supported_output_configs() has no WASAPI shared-mode counterpart: a shared-mode stream accepts the
        // mix format above (plus engine-side conversion), so the "Available configs" listing is not reproduced.
    }

    private static string? TryGetName(MMDevice device, out Exception? error)
    {
        try
        {
            error = null;
            return device.FriendlyName;
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or ArgumentException)
        {
            error = e;
            return null;
        }
    }

    // Upstream `RodioError` and its `From<RodioError> for SinkError` mapping.
    private static SinkException NoDeviceAvailable() =>
        new(SinkException.Reason.ConnectionRefused, "<NAudioSink> No Device Available");

    private static SinkException DeviceNotAvailable(string device) =>
        new(SinkException.Reason.ConnectionRefused, $"<NAudioSink> device \"{device}\" is Not Available");

    private static SinkException PlayError(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<NAudioSink> Play Error: {e.Message}", e);

    private static SinkException StreamError(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<NAudioSink> Stream Error: {e.Message}", e);

    private static SinkException DevicesError(Exception e) =>
        new(SinkException.Reason.InvalidParams, $"<NAudioSink> Cannot Get Audio Devices: {e.Message}", e);

    private static SinkException SamplesError(string detail, Exception e) =>
        new(SinkException.Reason.OnWrite, $"<NAudioSink> {detail}", e);

    /// <summary>
    /// Converts the f32 queue to the stream's sample format, the way rodio's output stream converts its f32 mixer
    /// output to the cpal sample format it was opened with.
    /// </summary>
    private sealed class F32ConvertingProvider(IWaveProvider source, WaveFormat waveFormat) : IWaveProvider
    {
        private readonly int _bytesPerSample = waveFormat.BitsPerSample / 8;
        private float[] _samples = [];

        public WaveFormat WaveFormat { get; } = waveFormat;

        public int Read(Span<byte> buffer)
        {
            var count = buffer.Length / _bytesPerSample;
            if (_samples.Length < count)
            {
                _samples = new float[count];
            }

            var read = source.Read(MemoryMarshal.AsBytes(_samples.AsSpan(0, count))) / sizeof(float);
            ReadOnlySpan<float> samples = _samples.AsSpan(0, read);
            var output = buffer[..(read * _bytesPerSample)];

            // The format is fixed, so dispatch once per buffer and run a tight loop per sample width. The class only
            // runs on Windows, which is little-endian, so the native-endian MemoryMarshal.Cast views are LE PCM.
            switch (_bytesPerSample)
            {
                case 8:
                    // F64 is the only 64-bit format; it is IEEE float, not integer PCM.
                    var f64 = MemoryMarshal.Cast<byte, double>(output);
                    for (var i = 0; i < samples.Length; i++)
                    {
                        f64[i] = samples[i];
                    }

                    break;
                case 2:
                    var s16 = MemoryMarshal.Cast<byte, short>(output);
                    for (var i = 0; i < samples.Length; i++)
                    {
                        s16[i] = (short)Math.Clamp(Math.Round(Clamp(samples[i]) * 32768.0), short.MinValue, short.MaxValue);
                    }

                    break;
                case 3:
                    for (int i = 0, o = 0; i < samples.Length; i++, o += 3)
                    {
                        var s24 = (int)Math.Clamp(Math.Round(Clamp(samples[i]) * 8388608.0), -8388608, 8388607);
                        output[o] = (byte)s24;
                        output[o + 1] = (byte)(s24 >> 8);
                        output[o + 2] = (byte)(s24 >> 16);
                    }

                    break;
                default:
                    var s32 = MemoryMarshal.Cast<byte, int>(output);
                    for (var i = 0; i < samples.Length; i++)
                    {
                        s32[i] = (int)Math.Clamp(Math.Round(Clamp(samples[i]) * 2147483648.0), int.MinValue, int.MaxValue);
                    }

                    break;
            }

            return output.Length;
        }

        private static double Clamp(float sample) => Math.Clamp((double)sample, -1.0, 1.0);
    }
}

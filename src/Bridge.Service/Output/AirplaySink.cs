using System.Threading.Channels;
using Airplay2.Sender.Playback;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;

namespace Bridge.Service.Output;

/// <summary>
/// The librespot <see cref="ISink"/> that feeds the AirPlay group player (SPEC-audio.md 8). <see cref="Write"/> converts
/// the f64 samples to S16 and blocks while the player's ring is above its high-water mark; without a connected
/// output the audio is discarded (docs/ARCHITECTURE.md, "Design decisions"). <see cref="Start"/>/<see cref="Stop"/> map to resume/pause on the player.
/// The bridge swaps the target player when the user selects another output.
/// </summary>
/// <remarks>
/// <para>
/// Threads (SPEC-audio.md 8.5): librespot calls every member on its player thread. <see cref="Write"/> blocking there is
/// the deliberate backpressure of SPEC-audio.md 8.1/8.2 (docs/DEVELOPMENT.md, "Code conventions", the documented exception).
/// <see cref="Stop"/> must not block: on the SpeakerA a <c>FLUSH</c> takes 2-3 s (SPEC-control.md 4.8 note), so the pause is
/// queued to a single consumer task that runs <see cref="IGroupPlayer.PauseAsync"/>. <see cref="Start"/> queues the
/// resume behind it and waits (bounded by <see cref="StartWait"/>) until it ran, so a quick pause/play cannot let the
/// pending pause drop the first samples of the resumed playback.
/// </para>
/// <para>
/// librespot's <c>Seeked</c>, track changes and volume are not sink calls; <see cref="Spotify.PlayerEventMapper"/> maps
/// those events (SPEC-audio.md 8.4) and correlates them with <see cref="FramesWritten"/>.
/// </para>
/// <para>
/// Ownership: the sink is a host singleton that the output manager keeps using after librespot is gone (it detaches the
/// player and tears the receivers down at shutdown, D7). librespot disposes its sink when its player thread ends
/// (<c>Player.Drop</c>), at every shutdown and after a crash, so it gets <see cref="LibrespotSink"/>, a view whose
/// <c>Dispose</c> only pauses; the sink itself is disposed by the host.
/// </para>
/// </remarks>
public sealed class AirplaySink : ISink, IAsyncDisposable
{
    private readonly ILogger<AirplaySink> _logger;
    private readonly Channel<Command> _commands = Channel.CreateUnbounded<Command>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _worker;
    private volatile IGroupPlayer? _player;
    private volatile bool _running;
    private long _framesWritten;
    private long _framesDiscarded;
    private int _disposed;

    /// <summary>Creates the sink; <paramref name="format"/> must be S16 (the ALAC input format).</summary>
    public AirplaySink(AudioFormat format, ILogger<AirplaySink> logger)
    {
        if (format != AudioFormat.S16)
        {
            throw new SinkException(SinkException.Reason.InvalidParams, "AirplaySink requires S16");
        }

        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
        LibrespotSink = new LibrespotView(this);
        _worker = RunCommandsAsync();
    }

    /// <summary>
    /// The sink to hand to librespot's <c>Player</c>: forwards every call to this sink, except that disposing it (librespot
    /// does when its player thread ends) only stops the playback instead of closing the sink.
    /// </summary>
    public ISink LibrespotSink { get; }

    /// <summary>
    /// The player audio currently goes to, or null to discard. Setting it does not start the new player; use
    /// <see cref="AttachAsync"/> to swap players while Spotify is playing.
    /// </summary>
    public IGroupPlayer? Player
    {
        get => _player;
        set => _player = value;
    }

    /// <summary>True between librespot's <see cref="Start"/> and <see cref="Stop"/> (Spotify is playing).</summary>
    public bool IsRunning => _running;

    /// <summary>Frames handed to a player since creation (the event mapper's frame counter).</summary>
    public long FramesWritten => Interlocked.Read(ref _framesWritten);

    /// <summary>Frames discarded because no output was connected.</summary>
    public long FramesDiscarded => Interlocked.Read(ref _framesDiscarded);

    /// <summary>How long <see cref="Start"/> waits for a queued pause (its FLUSH) and the player's start.</summary>
    public TimeSpan StartWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Points the sink at another player (or none) and, while Spotify is playing, starts it after any pause or start still
    /// queued for the previous one. The caller owns both players.
    /// </summary>
    public Task AttachAsync(IGroupPlayer? player, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _player = player;
        if (player is null || !_running)
        {
            return Task.CompletedTask;
        }

        var command = new Command(CommandKind.Start, player);
        _commands.Writer.TryWrite(command);
        return command.Done.Task.WaitAsync(cancellationToken);
    }

    /// <inheritdoc />
    /// <remarks>librespot calls this on play and resume (Player.cs:1858-1876) before writing.</remarks>
    public void Start()
    {
        _running = true;
        var player = _player;
        if (player is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        var command = new Command(CommandKind.Start, player);
        if (!_commands.Writer.TryWrite(command))
        {
            return;
        }

        // The librespot player thread waits here (not an async path): the pause queued before must have flushed the
        // receivers, and the player must be started, before samples of the resumed playback go into the ring.
        if (!command.Signal.Wait(StartWait))
        {
            _logger.LogWarning("Starting the AirPlay output takes longer than {Wait}; audio is queued meanwhile", StartWait);
        }
    }

    /// <inheritdoc />
    /// <remarks>Called on pause and at track end/stop (Player.cs:1878-1909); returns at once, the FLUSH runs in the background.</remarks>
    public void Stop()
    {
        _running = false;
        var player = _player;
        if (player is null || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        _commands.Writer.TryWrite(new Command(CommandKind.Pause, player));
    }

    /// <inheritdoc />
    public void Write(AudioPacket packet, Converter converter)
    {
        ArgumentNullException.ThrowIfNull(converter);
        if (packet is not AudioPacket.Samples samples)
        {
            return; // raw (passthrough) packets cannot be forwarded to AirPlay
        }

        var pcm = converter.F64ToS16(samples.Data);
        var frames = pcm.Length / 2;
        var player = _player;
        if (player is null)
        {
            Interlocked.Add(ref _framesDiscarded, frames);
            return;
        }

        player.Write(pcm); // blocks above the ring's high-water mark: this is the only clock librespot sees
        Interlocked.Add(ref _framesWritten, frames);
    }

    /// <inheritdoc />
    /// <remarks>Closes the sink for good (the host does, at the end); librespot gets <see cref="LibrespotSink"/>, which never closes it.</remarks>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _player = null;
        _running = false;
        _commands.Writer.TryComplete();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        Dispose();
        await _worker.ConfigureAwait(false);
    }

    private async Task RunCommandsAsync()
    {
        await foreach (var command in _commands.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                if (command.Kind == CommandKind.Start)
                {
                    await command.Player.StartAsync(CancellationToken.None).ConfigureAwait(false);
                }
                else
                {
                    await command.Player.PauseAsync(CancellationToken.None).ConfigureAwait(false);
                }

                command.Done.TrySetResult();
            }
            catch (ObjectDisposedException)
            {
                // The output was switched or closed meanwhile; the new player gets its own start.
                command.Done.TrySetResult();
            }
            catch (InvalidOperationException ex)
            {
                // GroupPlayer.IsFaulted: the output manager's supervisor rebuilds the player.
                _logger.LogError("The AirPlay player cannot {Action}: {Error}", command.Kind == CommandKind.Start ? "start" : "pause", ex.Message);
                command.Done.TrySetResult();
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException or System.Net.Sockets.SocketException)
            {
                _logger.LogWarning("AirPlay {Action} failed: {Error}", command.Kind == CommandKind.Start ? "start" : "pause", ex.Message);
                command.Done.TrySetResult();
            }
            catch (Exception ex)
            {
                // Not an expected failure (a bug): logged with its stack; the loop keeps serving, or every later pause would
                // be lost and every Start would wait StartWait on the librespot thread.
                _logger.LogError(ex, "AirPlay {Action} failed unexpectedly", command.Kind == CommandKind.Start ? "start" : "pause");
                command.Done.TrySetResult();
            }
            finally
            {
                command.Signal.Set();
            }
        }
    }

    /// <summary>librespot's end of the player thread (<c>Dispose</c>): stop the playback, keep the sink.</summary>
    private void ReleaseFromLibrespot()
    {
        if (_running)
        {
            Stop(); // the player thread is gone: no more audio, pause the output (its sessions stay up, SPEC-audio.md 8.4)
        }
    }

    private enum CommandKind
    {
        Start,
        Pause,
    }

    /// <summary>The non-owning <see cref="ISink"/> librespot gets (<see cref="LibrespotSink"/>).</summary>
    private sealed class LibrespotView(AirplaySink sink) : ISink
    {
        public void Start() => sink.Start();

        public void Stop() => sink.Stop();

        public void Write(AudioPacket packet, Converter converter) => sink.Write(packet, converter);

        public void Dispose() => sink.ReleaseFromLibrespot();
    }

    private sealed class Command(CommandKind kind, IGroupPlayer player)
    {
        public CommandKind Kind { get; } = kind;

        public IGroupPlayer Player { get; } = player;

        /// <summary>For async waiters (<see cref="AirplaySink.AttachAsync"/>).</summary>
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>For the librespot player thread (<see cref="AirplaySink.Start"/>), which waits synchronously.</summary>
        public ManualResetEventSlim Signal { get; } = new(false);
    }
}

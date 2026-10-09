// Port of src/player_event_handler.rs.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Librespot.Core.Logging;
using Librespot.Metadata.Audio;
using Librespot.Playback;
using Microsoft.Extensions.Logging;

namespace Librespot.Cli;

/// <summary>
/// Runs the <c>--onevent</c> program for every player event, passing the event data in environment variables
/// (upstream <c>EventHandler</c>; renamed because <c>EventHandler</c> would shadow <see cref="System.EventHandler"/>).
/// </summary>
/// <remarks>
/// Like upstream, the events are read on a dedicated thread and the program is run synchronously, one event after the
/// other. Dispose the handler (upstream: <c>Drop</c>) after the player has shut down; it joins the thread once the
/// remaining events are handled.
/// </remarks>
public sealed class PlayerEventHandler : IDisposable
{
    private static ILogger Log => LibrespotLog.CreateLogger<PlayerEventHandler>();

    private Thread? _threadHandle;
    private Exception? _threadError;

    /// <summary>Upstream <c>EventHandler::new</c>: starts the thread that reads <paramref name="playerEvents"/>.</summary>
    /// <param name="playerEvents">The player's event channel; the thread ends when it completes.</param>
    /// <param name="onEvent">The program (with arguments, split on whitespace) to run per event.</param>
    public PlayerEventHandler(PlayerEventChannel playerEvents, string onEvent)
    {
        var reader = playerEvents.Reader;
        _threadHandle = new Thread(() =>
        {
            try
            {
                // `player_events.blocking_recv()`: this is a dedicated thread, so blocking on the channel is intended.
                while (reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult())
                {
                    while (reader.TryRead(out var playerEvent))
                    {
                        var envVars = GetEnvVars(playerEvent);
                        if (envVars.Count != 0)
                        {
                            RunProgram(envVars, onEvent);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                // Upstream: a panic ends the thread and is reported when the thread is joined.
                _threadError = e;
            }
        })
        {
            IsBackground = true,
            Name = "player-event-handler",
        };

        _threadHandle.Start();
    }

    /// <summary>
    /// The environment variables for <paramref name="playerEvent"/>, in upstream's insertion order; empty for events
    /// that are irrelevant for the standalone binary (like <see cref="PlayerEvent.PositionChanged"/>).
    /// </summary>
    /// <param name="playerEvent">The event.</param>
    public static IReadOnlyDictionary<string, string> GetEnvVars(PlayerEvent playerEvent)
    {
        var envVars = new Dictionary<string, string>();

        switch (playerEvent)
        {
            case PlayerEvent.PlayRequestIdChanged e:
                envVars["PLAYER_EVENT"] = "play_request_id_changed";
                envVars["PLAY_REQUEST_ID"] = ToStr(e.PlayRequestId);
                break;

            case PlayerEvent.TrackChanged { AudioItem: var audioItem }:
                envVars["PLAYER_EVENT"] = "track_changed";
                envVars["TRACK_ID"] = audioItem.TrackId.ToId();
                envVars["URI"] = audioItem.Uri;
                envVars["NAME"] = audioItem.Name;
                envVars["COVERS"] = string.Join("\n", audioItem.Covers.Select(c => c.Url));
                envVars["LANGUAGE"] = string.Join("\n", audioItem.Language);
                envVars["DURATION_MS"] = ToStr(audioItem.DurationMs);
                envVars["IS_EXPLICIT"] = ToStr(audioItem.IsExplicit);

                switch (audioItem.UniqueFields)
                {
                    case UniqueFields.Track t:
                        envVars["ITEM_TYPE"] = "Track";
                        envVars["ARTISTS"] = string.Join("\n", t.Artists.Select(a => a.Name));
                        envVars["ALBUM_ARTISTS"] = string.Join("\n", t.AlbumArtists);
                        envVars["ALBUM"] = t.Album;
                        envVars["POPULARITY"] = ToStr(t.Popularity);
                        envVars["NUMBER"] = ToStr(t.Number);
                        envVars["DISC_NUMBER"] = ToStr(t.DiscNumber);
                        break;

                    case UniqueFields.Local l:
                        envVars["ITEM_TYPE"] = "Track";
                        envVars["ARTISTS"] = l.Artists ?? string.Empty;
                        envVars["ALBUM_ARTISTS"] = l.AlbumArtists ?? string.Empty;
                        envVars["ALBUM"] = l.Album ?? string.Empty;
                        envVars["NUMBER"] = l.Number is { } number ? ToStr(number) : string.Empty;
                        envVars["DISC_NUMBER"] = l.DiscNumber is { } discNumber ? ToStr(discNumber) : string.Empty;
                        envVars["LOCAL_FILE_PATH"] = l.Path;
                        break;

                    case UniqueFields.Episode ep:
                        envVars["ITEM_TYPE"] = "Episode";
                        envVars["DESCRIPTION"] = ep.Description;
                        envVars["PUBLISH_TIME"] = ToStr(ep.PublishTime.Value.ToUnixTimeSeconds());
                        envVars["SHOW_NAME"] = ep.ShowName;
                        break;
                }

                break;

            case PlayerEvent.Stopped e:
                envVars["PLAYER_EVENT"] = "stopped";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                break;

            case PlayerEvent.Playing e:
                envVars["PLAYER_EVENT"] = "playing";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                envVars["POSITION_MS"] = ToStr(e.PositionMs);
                break;

            case PlayerEvent.Paused e:
                envVars["PLAYER_EVENT"] = "paused";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                envVars["POSITION_MS"] = ToStr(e.PositionMs);
                break;

            case PlayerEvent.Loading e:
                envVars["PLAYER_EVENT"] = "loading";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                break;

            case PlayerEvent.Preloading e:
                envVars["PLAYER_EVENT"] = "preloading";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                break;

            case PlayerEvent.TimeToPreloadNextTrack e:
                envVars["PLAYER_EVENT"] = "preload_next";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                break;

            case PlayerEvent.EndOfTrack e:
                envVars["PLAYER_EVENT"] = "end_of_track";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                break;

            case PlayerEvent.Unavailable e:
                envVars["PLAYER_EVENT"] = "unavailable";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                break;

            case PlayerEvent.VolumeChanged e:
                envVars["PLAYER_EVENT"] = "volume_changed";
                envVars["VOLUME"] = ToStr(e.Volume);
                break;

            case PlayerEvent.Seeked e:
                envVars["PLAYER_EVENT"] = "seeked";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                envVars["POSITION_MS"] = ToStr(e.PositionMs);
                break;

            case PlayerEvent.PositionCorrection e:
                envVars["PLAYER_EVENT"] = "position_correction";
                envVars["TRACK_ID"] = e.TrackId.ToId();
                envVars["POSITION_MS"] = ToStr(e.PositionMs);
                break;

            case PlayerEvent.SessionConnected e:
                envVars["PLAYER_EVENT"] = "session_connected";
                envVars["CONNECTION_ID"] = e.ConnectionId;
                envVars["USER_NAME"] = e.UserName;
                break;

            case PlayerEvent.SessionDisconnected e:
                envVars["PLAYER_EVENT"] = "session_disconnected";
                envVars["CONNECTION_ID"] = e.ConnectionId;
                envVars["USER_NAME"] = e.UserName;
                break;

            case PlayerEvent.SessionClientChanged e:
                envVars["PLAYER_EVENT"] = "session_client_changed";
                envVars["CLIENT_ID"] = e.ClientId;
                envVars["CLIENT_NAME"] = e.ClientName;
                envVars["CLIENT_BRAND_NAME"] = e.ClientBrandName;
                envVars["CLIENT_MODEL_NAME"] = e.ClientModelName;
                break;

            case PlayerEvent.ShuffleChanged e:
                envVars["PLAYER_EVENT"] = "shuffle_changed";
                envVars["SHUFFLE"] = ToStr(e.Shuffle);
                break;

            case PlayerEvent.RepeatChanged e:
                envVars["PLAYER_EVENT"] = "repeat_changed";
                envVars["REPEAT"] = ToStr(e.Context);
                envVars["REPEAT_TRACK"] = ToStr(e.Track);
                break;

            case PlayerEvent.AutoPlayChanged e:
                envVars["PLAYER_EVENT"] = "auto_play_changed";
                envVars["AUTO_PLAY"] = ToStr(e.AutoPlay);
                break;

            case PlayerEvent.FilterExplicitContentChanged e:
                envVars["PLAYER_EVENT"] = "filter_explicit_content_changed";
                envVars["FILTER"] = ToStr(e.Filter);
                break;

            case PlayerEvent.SetQueue e:
                envVars["PLAYER_EVENT"] = "set_queue";
                envVars["CONTEXT_URI"] = e.ContextUri;
                if (e.CurrentTrack is { } track)
                {
                    envVars["CURRENT_TRACK"] = $"{track.Uri}\t{track.Provider}";
                }

                envVars["NEXT_TRACKS"] = string.Join("\n", e.NextTracks.Select(t => $"{t.Uri}\t{t.Provider}"));
                envVars["PREV_TRACKS"] = string.Join("\n", e.PrevTracks.Select(t => $"{t.Uri}\t{t.Provider}"));
                break;

            // Ignore event irrelevant for standalone binary like PositionChanged
            default:
                break;
        }

        return envVars;
    }

    /// <summary>Upstream <c>run_program_on_sink_events</c>: runs <paramref name="onEvent"/> with <c>PLAYER_EVENT=sink</c>.</summary>
    /// <param name="sinkStatus">The new sink status.</param>
    /// <param name="onEvent">The program (with arguments, split on whitespace) to run.</param>
    public static void RunProgramOnSinkEvents(SinkStatus sinkStatus, string onEvent) =>
        RunProgram(GetSinkEnvVars(sinkStatus), onEvent);

    /// <summary>The environment variables <see cref="RunProgramOnSinkEvents"/> passes for <paramref name="sinkStatus"/>.</summary>
    /// <param name="sinkStatus">The new sink status.</param>
    public static IReadOnlyDictionary<string, string> GetSinkEnvVars(SinkStatus sinkStatus) => new Dictionary<string, string>
    {
        ["PLAYER_EVENT"] = "sink",
        ["SINK_STATUS"] = sinkStatus switch
        {
            SinkStatus.Running => "running",
            SinkStatus.TemporarilyClosed => "temporarily_closed",
            SinkStatus.Closed => "closed",
            _ => throw new ArgumentOutOfRangeException(nameof(sinkStatus), sinkStatus, null),
        },
    };

    /// <summary>
    /// Upstream <c>run_program</c>: splits <paramref name="onEvent"/> on whitespace, starts the program with the
    /// variables added to the inherited environment, and waits for it to exit.
    /// </summary>
    /// <exception cref="InvalidOperationException"><paramref name="onEvent"/> is empty (upstream panics on <c>v.remove(0)</c>).</exception>
    private static void RunProgram(IReadOnlyDictionary<string, string> envVars, string onEvent)
    {
        var v = onEvent.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (v.Length == 0)
        {
            throw new InvalidOperationException("removal index (is 0) should be < len (is 0)");
        }

        Log.LogDebug("Running {OnEvent} with environment variables:\n{EnvVars}", onEvent, FormatEnvVars(envVars));

        var startInfo = new ProcessStartInfo(v[0]) { UseShellExecute = false };
        foreach (var arg in v.Skip(1))
        {
            startInfo.ArgumentList.Add(arg);
        }

        foreach (var (key, value) in envVars)
        {
            startInfo.Environment[key] = value;
        }

        Process? child;
        try
        {
            child = Process.Start(startInfo);
        }
        catch (Exception e)
        {
            Log.LogWarning("On event program {OnEvent} failed to start: {Error}", onEvent, e.Message);
            return;
        }

        if (child is null)
        {
            Log.LogWarning("On event program {OnEvent} failed to start: {Error}", onEvent, "no process was started");
            return;
        }

        using (child)
        {
            try
            {
                child.WaitForExit();
            }
            catch (Exception e)
            {
                Log.LogWarning("On event program {OnEvent} failed: {Error}", onEvent, e.Message);
                return;
            }

            // Upstream distinguishes a missing exit code (killed by a signal on Unix); .NET always reports one.
            if (child.ExitCode != 0)
            {
                Log.LogWarning("On event program {OnEvent} returned exit code {Code}", onEvent, child.ExitCode);
            }
        }
    }

    /// <summary>Renders the variables like Rust's pretty <c>{:#?}</c> of a <c>HashMap&lt;&amp;str, String&gt;</c>.</summary>
    internal static string FormatEnvVars(IReadOnlyDictionary<string, string> envVars)
    {
        if (envVars.Count == 0)
        {
            return "{}";
        }

        var sb = new StringBuilder("{\n");
        foreach (var (key, value) in envVars)
        {
            sb.Append("    ").Append(RustDebugString(key)).Append(": ").Append(RustDebugString(value)).Append(",\n");
        }

        return sb.Append('}').ToString();
    }

    /// <summary>Rust's <c>Debug</c> for <c>str</c>: quoted, with escapes for quotes, backslashes and control characters.</summary>
    internal static string RustDebugString(string s)
    {
        var sb = new StringBuilder(s.Length + 2).Append('"');
        foreach (var c in s)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                case '\0': sb.Append("\\0"); break;
                default:
                    if (char.IsControl(c))
                    {
                        sb.Append("\\u{").Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        return sb.Append('"').ToString();
    }

    private static string ToStr(bool value) => value ? "true" : "false";

    private static string ToStr<T>(T value) where T : IFormattable => value.ToString(null, CultureInfo.InvariantCulture);

    /// <summary>Upstream <c>Drop</c>: joins the thread (which ends when the player's event channel completes).</summary>
    public void Dispose()
    {
        Log.LogDebug("Shutting down EventHandler thread ...");
        if (Interlocked.Exchange(ref _threadHandle, null) is { } handle)
        {
            handle.Join();
            if (_threadError is { } e)
            {
                Log.LogError("EventHandler thread Error: {Error}", e);
            }
        }
    }
}

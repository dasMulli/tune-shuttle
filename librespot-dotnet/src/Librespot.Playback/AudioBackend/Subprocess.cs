// Port of playback/src/audio_backend/subprocess.rs (plus the `shell_words::split` function it uses).
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using Librespot.Core.Logging;
using Librespot.Playback.Decoder;
using Microsoft.Extensions.Logging;

namespace Librespot.Playback.AudioBackend;

/// <summary>
/// Upstream <c>SubprocessSink</c> (backend name <c>"subprocess"</c>): pipes raw PCM in the configured
/// <see cref="AudioFormat"/> into the stdin of a shell command given as the device. The command is split
/// with POSIX shell rules (no shell is involved), started by <see cref="Start"/> and killed by <see cref="Stop"/>.
/// </summary>
public sealed class SubprocessSink : ISinkAsBytes
{
    /// <summary>Backend name used by the registry (upstream <c>SubprocessSink::NAME</c>).</summary>
    public const string Name = "subprocess";

    private static readonly ILogger Log = LibrespotLog.CreateLogger<SubprocessSink>();

    private readonly string? _shellCommand;
    private Process? _child;

    /// <summary>Upstream <c>Open::open</c>. A device of <c>"?"</c> prints the usage and exits the process, like upstream.</summary>
    /// <param name="shellCommand">The command line to run; its stdin receives the audio.</param>
    /// <param name="format">Sample format written to the subprocess.</param>
    public SubprocessSink(string? shellCommand, AudioFormat format)
    {
        if (shellCommand == "?")
        {
            Console.WriteLine("\nUsage:\n\nOutput to a Subprocess:\n\n\t--backend subprocess --device {shell_command}\n");
            Environment.Exit(0);
        }

        Log.LogInformation("Using SubprocessSink with format: {Format}", format);

        _shellCommand = shellCommand;
        Format = format;
    }

    /// <inheritdoc />
    public AudioFormat Format { get; }

    /// <summary>The <see cref="SinkBuilder"/> for this backend (upstream <c>mk_sink::&lt;SubprocessSink&gt;</c>).</summary>
    public static ISink Open(string? shellCommand, AudioFormat format) => new SubprocessSink(shellCommand, format);

    /// <summary>Spawns the command with a piped stdin unless it is already running.</summary>
    public void Start()
    {
        if (_child is not null)
        {
            return;
        }

        var command = _shellCommand ?? throw MissingCommand();
        var args = ShellWords.Split(command) ?? throw InvalidArgs(command);

        // Deviation: upstream indexes args[0] and panics on an empty command line; report it as a missing command.
        if (args.Count == 0)
        {
            throw MissingCommand();
        }

        var startInfo = new ProcessStartInfo(args[0])
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
        };

        for (var i = 1; i < args.Count; i++)
        {
            startInfo.ArgumentList.Add(args[i]);
        }

        try
        {
            _child = Process.Start(startInfo) ?? throw new InvalidOperationException("no process was started");
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            throw SpawnFailure(command, e);
        }
    }

    /// <summary>Flushes the subprocess's stdin and kills it, unless it already exited.</summary>
    public void Stop()
    {
        var child = _child ?? throw NoChild();
        _child = null;

        try
        {
            bool exited;
            try
            {
                exited = child.HasExited;
            }
            catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException)
            {
                throw WaitFailure(e);
            }

            // The process has already exited, nothing to do.
            if (exited)
            {
                return;
            }

            // The process Must DIE!!!
            var stdin = Stdin(child);
            try
            {
                stdin.BaseStream.Flush();
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                throw FlushFailure(e);
            }
            finally
            {
                // Upstream takes stdin out of the child and drops it, which closes the pipe.
                CloseQuietly(stdin);
            }

            try
            {
                child.Kill();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException or NotSupportedException)
            {
                throw KillFailure(e);
            }

            try
            {
                child.WaitForExit();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                throw WaitFailure(e);
            }
        }
        finally
        {
            // Upstream drops the taken Child here, which also closes stdin if it is still open.
            CloseQuietly(child.StandardInput);
            child.Dispose();
        }
    }

    /// <inheritdoc />
    public void Write(AudioPacket packet, Converter converter) => this.WriteAsBytes(packet, converter);

    /// <inheritdoc />
    /// <remarks>
    /// A failed write restarts the subprocess once and retries; a second failure (or a failed restart) throws the
    /// original error. Upstream also handles short writes, <c>Ok(0)</c> (WriteZero) and <c>Interrupted</c>; a .NET
    /// <see cref="Stream.Write(ReadOnlySpan{byte})"/> either writes everything or throws, so those cases collapse
    /// into the error path, and the retry rewrites the whole buffer to the restarted process.
    /// </remarks>
    public void WriteBytes(ReadOnlySpan<byte> data)
    {
        // We get one attempted restart per write.
        // We don't want to get stuck in a restart loop.
        var restarted = false;

        while (true)
        {
            var child = _child ?? throw NoChild();
            var stdin = Stdin(child);

            try
            {
                stdin.BaseStream.Write(data);

                // Process.StandardInput's underlying stream is buffered (a 4 KiB FileStream on Windows), so flush so
                // each packet reaches the pipe immediately and a closed pipe is reported on this write, as
                // upstream's unbuffered ChildStdin would.
                stdin.BaseStream.Flush();
                return;
            }
            catch (Exception e) when (e is IOException or ObjectDisposedException)
            {
                // Very possibly fatal, but let's try a restart anyway if we haven't already.
                TryRestart(OnWrite(e), ref restarted);
            }
        }
    }

    /// <summary>Releases the process handle and closes its stdin; like upstream's <c>Drop</c>, the process is not killed.</summary>
    public void Dispose()
    {
        if (_child is { } child)
        {
            _child = null;
            CloseQuietly(child.StandardInput);
            child.Dispose();
        }
    }

    private void TryRestart(SinkException e, ref bool restarted)
    {
        // If the restart fails throw the original error back.
        if (!restarted && Succeeds(Stop) && Succeeds(Start))
        {
            restarted = true;
            return;
        }

        throw e;
    }

    private static bool Succeeds(Action action)
    {
        try
        {
            action();
            return true;
        }
        catch (SinkException)
        {
            return false;
        }
    }

    private static StreamWriter Stdin(Process child)
    {
        try
        {
            return child.StandardInput;
        }
        catch (InvalidOperationException)
        {
            throw NoStdin();
        }
    }

    private static void CloseQuietly(StreamWriter stdin)
    {
        try
        {
            stdin.Dispose();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // Dropping a ChildStdin upstream ignores close errors as well.
        }
    }

    // Upstream `SubprocessError` and its `From<SubprocessError> for SinkError` mapping.
    private static SinkException OnWrite(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<SubprocessSink> {e.Message}", e);

    private static SinkException SpawnFailure(string command, Exception e) =>
        new(SinkException.Reason.ConnectionRefused, $"<SubprocessSink> Command {command} Can Not be Executed, {e.Message}", e);

    private static SinkException InvalidArgs(string command) =>
        new(SinkException.Reason.InvalidParams, $"<SubprocessSink> Failed to Parse Command args for {command}, {ShellWords.ParseErrorMessage}");

    private static SinkException FlushFailure(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<SubprocessSink> Failed to Flush the Subprocess, {e.Message}", e);

    private static SinkException KillFailure(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<SubprocessSink> Failed to Kill the Subprocess, {e.Message}", e);

    private static SinkException WaitFailure(Exception e) =>
        new(SinkException.Reason.OnWrite, $"<SubprocessSink> Failed to Wait for the Subprocess to Exit, {e.Message}", e);

    private static SinkException MissingCommand() =>
        new(SinkException.Reason.InvalidParams, "<SubprocessSink> Missing Required Shell Command");

    private static SinkException NoChild() =>
        new(SinkException.Reason.NotConnected, "<SubprocessSink> The Subprocess is None");

    private static SinkException NoStdin() =>
        new(SinkException.Reason.NotConnected, "<SubprocessSink> The Subprocess's stdin is None");
}

/// <summary>
/// Port of <c>shell_words::split</c> (crate shell-words 1.1.0): splits a command line the way a POSIX shell
/// would, with word expansion limited to quote removal and no operator recognition.
/// </summary>
internal static class ShellWords
{
    /// <summary>Display text of upstream <c>shell_words::ParseError</c>.</summary>
    public const string ParseErrorMessage = "missing closing quote";

    private enum State
    {
        /// <summary>Within a delimiter.</summary>
        Delimiter,

        /// <summary>After backslash, but before starting word.</summary>
        Backslash,

        /// <summary>Within an unquoted word.</summary>
        Unquoted,

        /// <summary>After backslash in an unquoted word.</summary>
        UnquotedBackslash,

        /// <summary>Within a single quoted word.</summary>
        SingleQuoted,

        /// <summary>Within a double quoted word.</summary>
        DoubleQuoted,

        /// <summary>After backslash inside a double quoted word.</summary>
        DoubleQuotedBackslash,

        /// <summary>Inside a comment.</summary>
        Comment,
    }

    /// <summary>Splits <paramref name="s"/> into words; null when a quote is not closed (upstream <c>Err(ParseError)</c>).</summary>
    public static List<string>? Split(string s)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var state = State.Delimiter;
        var i = 0;

        void PushWord()
        {
            words.Add(word.ToString());
            word.Clear();
        }

        while (true)
        {
            char? c = i < s.Length ? s[i] : null;
            i++;

            switch (state)
            {
                case State.Delimiter:
                    switch (c)
                    {
                        case null:
                            return words;
                        case '\'':
                            state = State.SingleQuoted;
                            break;
                        case '"':
                            state = State.DoubleQuoted;
                            break;
                        case '\\':
                            state = State.Backslash;
                            break;
                        case '\t' or ' ' or '\n':
                            state = State.Delimiter;
                            break;
                        case '#':
                            state = State.Comment;
                            break;
                        default:
                            word.Append(c.Value);
                            state = State.Unquoted;
                            break;
                    }

                    break;

                case State.Backslash:
                    switch (c)
                    {
                        case null:
                            word.Append('\\');
                            PushWord();
                            return words;
                        case '\n':
                            state = State.Delimiter;
                            break;
                        default:
                            word.Append(c.Value);
                            state = State.Unquoted;
                            break;
                    }

                    break;

                case State.Unquoted:
                    switch (c)
                    {
                        case null:
                            PushWord();
                            return words;
                        case '\'':
                            state = State.SingleQuoted;
                            break;
                        case '"':
                            state = State.DoubleQuoted;
                            break;
                        case '\\':
                            state = State.UnquotedBackslash;
                            break;
                        case '\t' or ' ' or '\n':
                            PushWord();
                            state = State.Delimiter;
                            break;
                        default:
                            word.Append(c.Value);
                            state = State.Unquoted;
                            break;
                    }

                    break;

                case State.UnquotedBackslash:
                    switch (c)
                    {
                        case null:
                            word.Append('\\');
                            PushWord();
                            return words;
                        case '\n':
                            state = State.Unquoted;
                            break;
                        default:
                            word.Append(c.Value);
                            state = State.Unquoted;
                            break;
                    }

                    break;

                case State.SingleQuoted:
                    switch (c)
                    {
                        case null:
                            return null;
                        case '\'':
                            state = State.Unquoted;
                            break;
                        default:
                            word.Append(c.Value);
                            state = State.SingleQuoted;
                            break;
                    }

                    break;

                case State.DoubleQuoted:
                    switch (c)
                    {
                        case null:
                            return null;
                        case '"':
                            state = State.Unquoted;
                            break;
                        case '\\':
                            state = State.DoubleQuotedBackslash;
                            break;
                        default:
                            word.Append(c.Value);
                            state = State.DoubleQuoted;
                            break;
                    }

                    break;

                case State.DoubleQuotedBackslash:
                    switch (c)
                    {
                        case null:
                            return null;
                        case '\n':
                            state = State.DoubleQuoted;
                            break;
                        case '$' or '`' or '"' or '\\':
                            word.Append(c.Value);
                            state = State.DoubleQuoted;
                            break;
                        default:
                            word.Append('\\').Append(c.Value);
                            state = State.DoubleQuoted;
                            break;
                    }

                    break;

                case State.Comment:
                    switch (c)
                    {
                        case null:
                            return words;
                        case '\n':
                            state = State.Delimiter;
                            break;
                        default:
                            state = State.Comment;
                            break;
                    }

                    break;
            }
        }
    }
}

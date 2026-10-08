using System.Text;
using Librespot.Playback;
using Librespot.Playback.AudioBackend;
using Librespot.Playback.Decoder;
using Xunit;

namespace Librespot.Tests.Playback;

/// <summary>
/// Tests for <see cref="SubprocessSink"/> (port of playback/src/audio_backend/subprocess.rs, which has no unit tests)
/// and for <see cref="ShellWords"/>, whose cases are the unit tests of the shell-words 1.1.0 crate's <c>split</c>.
/// </summary>
public sealed class SubprocessTests : IDisposable
{
    private static readonly TimeSpan ProcessTimeout = TimeSpan.FromSeconds(15);

    private readonly string _dir = Directory.CreateTempSubdirectory("librespot-subprocess-").FullName;

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>A command line that copies its stdin to <paramref name="path"/>.</summary>
    private static string CopyStdinTo(string path) =>
        OperatingSystem.IsWindows() ? $"cmd /c \"more > {path}\"" : $"sh -c \"cat > '{path}'\"";

    /// <summary>A command line that keeps running and ignores its stdin.</summary>
    private static string LongRunning() =>
        OperatingSystem.IsWindows() ? "ping -n 30 127.0.0.1" : "sleep 30";

    [Fact]
    public async Task Subprocess_receives_the_written_bytes()
    {
        var path = Path.Combine(_dir, "out.txt");
        var sink = new SubprocessSink(CopyStdinTo(path), AudioFormat.S16);

        sink.Start();
        sink.Write(new AudioPacket.Raw(Encoding.ASCII.GetBytes("hello subprocess\n")), new Converter(null));

        // Dispose closes stdin without killing the process (upstream Drop), so the copy completes on its own.
        sink.Dispose();

        var deadline = DateTime.UtcNow + ProcessTimeout;
        string content = string.Empty;
        while (DateTime.UtcNow < deadline)
        {
            content = TryRead(path);
            if (content.Contains("hello subprocess", StringComparison.Ordinal))
            {
                break;
            }

            await Task.Delay(50);
        }

        Assert.Contains("hello subprocess", content);
    }

    [Fact]
    public void Stop_kills_a_running_subprocess_and_resets_the_sink()
    {
        var sink = new SubprocessSink(LongRunning(), AudioFormat.S16);
        try
        {
            sink.Start();
            sink.WriteBytes([0, 0, 0, 0]);

            var started = DateTime.UtcNow;
            sink.Stop();
            Assert.True(DateTime.UtcNow - started < ProcessTimeout);

            var e = Assert.Throws<SinkException>(sink.Stop);
            Assert.Equal(SinkException.Reason.NotConnected, e.Cause);
            Assert.Equal("Audio Sink Error Not Connected: <SubprocessSink> The Subprocess is None", e.Detail);

            // Starting again spawns a new process.
            sink.Start();
            sink.Stop();
        }
        finally
        {
            sink.Dispose();
        }
    }

    [Fact]
    public async Task Write_to_an_exited_subprocess_restarts_it_once_or_reports_on_write()
    {
        var command = OperatingSystem.IsWindows() ? "cmd /c exit 0" : "true";
        var sink = new SubprocessSink(command, AudioFormat.S16);
        try
        {
            sink.Start();
            await Task.Delay(TimeSpan.FromSeconds(1));

            // The first write hits a closed pipe and restarts the process; the retry either lands in the new
            // process's pipe or fails again, which must surface as an OnWrite error rather than loop.
            var e = Record.Exception(() => sink.WriteBytes(new byte[64 * 1024]));
            if (e is not null)
            {
                var sinkError = Assert.IsType<SinkException>(e);
                Assert.Equal(SinkException.Reason.OnWrite, sinkError.Cause);
            }
        }
        finally
        {
            sink.Dispose();
        }
    }

    [Fact]
    public async Task Write_to_an_exited_subprocess_restarts_it_and_retries_in_the_new_process()
    {
        var marker = Path.Combine(_dir, "first-run");
        var path = Path.Combine(_dir, "restarted.txt");

        // The first run only creates the marker and exits; the restarted process copies its stdin to the file.
        var command = OperatingSystem.IsWindows()
            ? $"cmd /c \"if exist {marker} (more > {path}) else (type nul > {marker})\""
            : $"sh -c \"if [ -e '{marker}' ]; then cat > '{path}'; else touch '{marker}'; fi\"";
        var sink = new SubprocessSink(command, AudioFormat.S16);
        try
        {
            sink.Start();

            var deadline = DateTime.UtcNow + ProcessTimeout;
            while (!File.Exists(marker) && DateTime.UtcNow < deadline)
            {
                await Task.Delay(50);
            }

            Assert.True(File.Exists(marker), "the first process did not run");

            // Let the first process exit and close its end of the pipe.
            await Task.Delay(TimeSpan.FromSeconds(1));

            // The write hits the closed pipe, restarts the command once, and the retry lands in the new process.
            sink.WriteBytes(Encoding.ASCII.GetBytes("written after restart\n"));
        }
        finally
        {
            // Closes stdin without killing the process, so the copy completes on its own.
            sink.Dispose();
        }

        Assert.Contains("written after restart", await ReadEventually(path, "written after restart"));
    }

    [Fact]
    public void Missing_command_is_invalid_params()
    {
        using var sink = new SubprocessSink(null, AudioFormat.S16);

        var e = Assert.Throws<SinkException>(sink.Start);
        Assert.Equal(SinkException.Reason.InvalidParams, e.Cause);
        Assert.Equal("Audio Sink Error Invalid Parameters: <SubprocessSink> Missing Required Shell Command", e.Detail);
    }

    [Fact]
    public void Unbalanced_quotes_are_invalid_params()
    {
        using var sink = new SubprocessSink("'abc", AudioFormat.S16);

        var e = Assert.Throws<SinkException>(sink.Start);
        Assert.Equal(SinkException.Reason.InvalidParams, e.Cause);
        Assert.Equal(
            "Audio Sink Error Invalid Parameters: <SubprocessSink> Failed to Parse Command args for 'abc, missing closing quote",
            e.Detail);
    }

    [Fact]
    public void Unknown_executable_is_connection_refused()
    {
        using var sink = new SubprocessSink("librespot-no-such-executable-4711 --flag", AudioFormat.S16);

        var e = Assert.Throws<SinkException>(sink.Start);
        Assert.Equal(SinkException.Reason.ConnectionRefused, e.Cause);
        Assert.StartsWith(
            "Audio Sink Error Connection Refused: <SubprocessSink> Command librespot-no-such-executable-4711 --flag Can Not be Executed, ",
            e.Detail);
    }

    [Fact]
    public void Writing_before_start_is_not_connected()
    {
        using var sink = new SubprocessSink("anything", AudioFormat.S16);

        var e = Assert.Throws<SinkException>(() => sink.WriteBytes([1, 2]));
        Assert.Equal(SinkException.Reason.NotConnected, e.Cause);
    }

    /// <summary>Reads <paramref name="path"/> until it contains <paramref name="expected"/> or the timeout passes.</summary>
    private static async Task<string> ReadEventually(string path, string expected)
    {
        var deadline = DateTime.UtcNow + ProcessTimeout;
        var content = TryRead(path);
        while (!content.Contains(expected, StringComparison.Ordinal) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
            content = TryRead(path);
        }

        return content;
    }

    private static string TryRead(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }

    // ---- shell_words::split (tests from the shell-words 1.1.0 crate) ----

    private static void SplitOk(params (string Input, string[] Expected)[] cases)
    {
        foreach (var (input, expected) in cases)
        {
            var actual = ShellWords.Split(input);
            Assert.NotNull(actual);
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void split_empty() => SplitOk(("", []));

    [Fact]
    public void split_initial_whitespace_is_removed() => SplitOk(
        ("     a", ["a"]),
        ("\t\t\t\tbar", ["bar"]),
        ("\t \nc", ["c"]));

    [Fact]
    public void split_trailing_whitespace_is_removed() => SplitOk(
        ("a  ", ["a"]),
        ("b\t", ["b"]),
        ("c\t \n \n \n", ["c"]),
        ("d\n\n", ["d"]));

    [Fact]
    public void split_carriage_return_is_not_special() => SplitOk(("c\ra\r'\r'\r", ["c\ra\r\r\r"]));

    [Fact]
    public void split_single_quotes() => SplitOk(
        ("''", [""]),
        ("'a'", ["a"]),
        ("'\\'", ["\\"]),
        ("' \\ '", [" \\ "]),
        ("'#'", ["#"]));

    [Fact]
    public void split_double_quotes() => SplitOk(
        ("\"\"", [""]),
        ("\"\"\"\"", [""]),
        ("\"a b c' d\"", ["a b c' d"]),
        ("\"\\a\"", ["\\a"]),
        ("\"$\"", ["$"]),
        ("\"\\$\"", ["$"]),
        ("\"`\"", ["`"]),
        ("\"\\`\"", ["`"]),
        ("\"\\\"\"", ["\""]),
        ("\"\\\\\"", ["\\"]),
        ("\"\n\"", ["\n"]),
        ("\"\\\n\"", [""]));

    [Fact]
    public void split_unquoted() => SplitOk(
        ("\\|\\&\\;", ["|&;"]),
        ("\\<\\>", ["<>"]),
        ("\\(\\)", ["()"]),
        ("\\$", ["$"]),
        ("\\`", ["`"]),
        ("\\\"", ["\""]),
        ("\\'", ["'"]),
        ("\\\n", []),
        (" \\\n \n", []),
        ("a\nb\nc", ["a", "b", "c"]),
        ("a\\\nb\\\nc", ["abc"]),
        ("foo bar baz", ["foo", "bar", "baz"]),
        ("\\\U0001F989", ["\U0001F989"]));

    [Fact]
    public void split_trailing_backslash() => SplitOk(
        ("\\", ["\\"]),
        (" \\", ["\\"]),
        ("a\\", ["a\\"]));

    [Fact]
    public void split_errors()
    {
        Assert.Null(ShellWords.Split("'abc"));
        Assert.Null(ShellWords.Split("\""));
        Assert.Null(ShellWords.Split("'\\"));
    }

    [Fact]
    public void split_comments() => SplitOk(
        (" x # comment ", ["x"]),
        (" w1#w2 ", ["w1#w2"]),
        ("'not really a # comment'", ["not really a # comment"]),
        (" a # very long comment \n b # another comment", ["a", "b"]));

    [Fact]
    public void split_windows_paths_in_double_quotes_keep_backslashes() => SplitOk(
        ("cmd /c \"more > C:\\Temp\\out.txt\"", ["cmd", "/c", "more > C:\\Temp\\out.txt"]));
}

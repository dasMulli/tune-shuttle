using System.Text.RegularExpressions;
using Airplay2.Sender.Crypto;
using Airplay2.Sender.Playback;
using Airplay2.Sender.Rtp;
using Airplay2.Sender.Rtsp;
using Airplay2.Sender.Session;
using Airplay2.Sender.Streams;
using Airplay2.Sender.Tlv8;
using Xunit;
using Xunit.Abstractions;

namespace Airplay2.Sender.Tests.Integration;

/// <summary>
/// Release checks: checks over the whole repository that the
/// plan's exit conditions hold: no contract stub marker remains, production code throws no
/// <see cref="NotImplementedException"/>, every live test is gated by <c>AIRPLAY_LIVE</c>, and <c>build.ps1</c> excludes
/// the live categories. The checks read the source tree. When the tests run from a copy without it (the Docker VM
/// scripts build into <c>/tmp/artifacts</c> but mount the repository as the working directory), they still find it
/// through the current directory; when there is no source tree at all they log that and return.
/// </summary>
public sealed class RepositoryHygieneTests(ITestOutputHelper output)
{
    // Built at run time so this file does not contain the marker it looks for.
    private static readonly string StubMarker = "TODO(" + "impl:";

    private static readonly string[] SourceExtensions = [".cs", ".razor", ".ps1", ".psm1", ".csproj", ".props", ".targets", ".sql", ".json", ".yaml", ".yml", ".service", ".css", ".html", ".js"];

    [Fact]
    public void No_contract_stub_marker_remains_in_code_scripts_or_configuration()
    {
        if (RepositoryRoot.Find() is not { } root)
        {
            output.WriteLine("repository not found; skipped");
            return;
        }

        var hits = new List<string>();
        foreach (var file in RepositoryRoot.SourceFiles(root, ["src", "tests", "docker", "install"], SourceExtensions).Append(Path.Combine(root, "build.ps1")).Append(Path.Combine(root, "docker", "Dockerfile")))
        {
            if (!File.Exists(file))
            {
                continue;
            }

            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(StubMarker, StringComparison.Ordinal))
                {
                    hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        Assert.True(hits.Count == 0, "Contract stubs left:\n" + string.Join('\n', hits));
    }

    // The AirPlay receiver is built in waves (SPEC-receiver.md 13); until wave 5 its pending surface throws
    // NotImplementedException("SPEC-receiver.md <section>, wave <n>"). Only that exact shape is exempt, and the wave 5
    // integration package removes the exemption once no stub remains.
    private static readonly Regex ReceiverWaveStub = new("""NotImplementedException\("SPEC-receiver\.md [0-9][0-9., -]*, wave [1-5]"\)""", RegexOptions.Compiled);

    [Fact]
    public void Production_code_throws_no_NotImplementedException()
    {
        if (RepositoryRoot.Find() is not { } root)
        {
            output.WriteLine("repository not found; skipped");
            return;
        }

        var hits = new List<string>();
        var pendingStubs = 0;
        foreach (var file in RepositoryRoot.SourceFiles(root, ["src"], [".cs", ".razor"]))
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (!line.Contains(nameof(NotImplementedException), StringComparison.Ordinal) || line.TrimStart().StartsWith("///", StringComparison.Ordinal))
                {
                    continue;
                }

                if (ReceiverWaveStub.IsMatch(line))
                {
                    pendingStubs++;
                    continue;
                }

                hits.Add($"{Path.GetRelativePath(root, file)}:{i + 1}: {line.Trim()}");
            }
        }

        output.WriteLine($"{pendingStubs} receiver stubs pending (SPEC-receiver.md 13)");
        Assert.True(hits.Count == 0, "NotImplementedException in:\n" + string.Join('\n', hits));
    }

    [Fact]
    public void Every_live_test_file_is_gated_by_AIRPLAY_LIVE()
    {
        if (RepositoryRoot.Find() is not { } root)
        {
            output.WriteLine("repository not found; skipped");
            return;
        }

        var liveFiles = new List<string>();
        var ungated = new List<string>();
        foreach (var file in RepositoryRoot.SourceFiles(root, ["tests"], [".cs"]))
        {
            var text = File.ReadAllText(file);
            if (!text.Contains("\"ReceiverLive\")]", StringComparison.Ordinal) && !text.Contains("\"ShairportLive\")]", StringComparison.Ordinal))
            {
                continue;
            }

            liveFiles.Add(Path.GetRelativePath(root, file));
            if (!HasLiveGate(text))
            {
                ungated.Add(Path.GetRelativePath(root, file));
            }
        }

        output.WriteLine($"{liveFiles.Count} live test files: {string.Join(", ", liveFiles)}");
        Assert.NotEmpty(liveFiles);
        Assert.True(ungated.Count == 0, "Live tests without the AIRPLAY_LIVE gate: " + string.Join(", ", ungated));
    }

    [Fact]
    public void The_live_gate_check_ignores_comments_and_accepts_the_inline_and_property_forms()
    {
        const string CommentOnly = """
            /// <summary>Returns early unless <c>AIRPLAY_LIVE=1</c>.</summary>
            // if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1") return;
            public void Live() { }
            """;
        const string Inline = """
            if (Environment.GetEnvironmentVariable("AIRPLAY_LIVE") != "1")
            {
                return;
            }
            """;
        const string Property = """
            private static bool LiveEnabled => Environment.GetEnvironmentVariable("AIRPLAY_LIVE") == "1";
            public void Live() { if (!LiveEnabled) { return; } }
            """;
        const string PropertyUnused = """
            private static bool LiveEnabled => Environment.GetEnvironmentVariable("AIRPLAY_LIVE") == "1";
            public void Live() { }
            """;

        Assert.False(HasLiveGate(CommentOnly));
        Assert.True(HasLiveGate(Inline));
        Assert.True(HasLiveGate(Property));
        Assert.False(HasLiveGate(PropertyUnused));
    }

    [Fact]
    public void The_build_script_excludes_the_live_categories_and_clears_the_live_switch()
    {
        if (RepositoryRoot.Find() is not { } root || !File.Exists(Path.Combine(root, "build.ps1")))
        {
            output.WriteLine("repository or build.ps1 not found; skipped");
            return;
        }

        var script = File.ReadAllText(Path.Combine(root, "build.ps1"));
        Assert.Contains("Category!=ReceiverLive&Category!=ShairportLive", script, StringComparison.Ordinal);
        Assert.Contains("Remove-Item Env:AIRPLAY_LIVE", script, StringComparison.Ordinal);
        Assert.Contains("docker/Dockerfile", script, StringComparison.Ordinal);
        Assert.Contains("-r\", $Runtime", script, StringComparison.Ordinal);
    }

    [Fact]
    public void The_documents_the_readme_points_to_exist()
    {
        if (RepositoryRoot.Find() is not { } root || !File.Exists(Path.Combine(root, "README.md")))
        {
            output.WriteLine("repository or README.md not found; skipped");
            return;
        }

        var readme = File.ReadAllText(Path.Combine(root, "README.md"));
        var missing = new List<string>();
        foreach (var link in MarkdownLinks(readme).Where(l => !l.Contains("://", StringComparison.Ordinal) && !l.StartsWith('#')))
        {
            var path = link.Split('#')[0];
            if (path.Length > 0 && !File.Exists(Path.Combine(root, path)) && !Directory.Exists(Path.Combine(root, path)))
            {
                missing.Add(link);
            }
        }

        Assert.True(missing.Count == 0, "README.md links to missing files: " + string.Join(", ", missing));
    }

    /// <summary>
    /// True when the code (comments stripped) compares <c>AIRPLAY_LIVE</c> with <c>"1"</c> (docs/DEVELOPMENT.md, "Live tests") and the
    /// comparison is used as a gate: either inline in an <c>if</c>, or through a property such as <c>LiveEnabled</c> that an
    /// <c>if</c> tests. A mention in a doc comment alone does not count.
    /// </summary>
    internal static bool HasLiveGate(string source)
    {
        var code = string.Join('\n', source.Split('\n').Select(StripLineComment));
        const string Read = @"Environment\.GetEnvironmentVariable\(\s*""AIRPLAY_LIVE""\s*\)\s*(==|!=)\s*""1""";
        if (Regex.IsMatch(code, @"\bif\s*\(\s*" + Read))
        {
            return true;
        }

        foreach (Match property in Regex.Matches(code, @"\bbool\s+(\w+)\s*=>\s*" + Read))
        {
            if (Regex.IsMatch(code, @"\bif\s*\(\s*!\s*" + property.Groups[1].Value + @"\b"))
            {
                return true;
            }
        }

        return false;
    }

    private static string StripLineComment(string line)
    {
        // Good enough for the test sources: no "//" inside the string literals that matter here.
        var at = line.IndexOf("//", StringComparison.Ordinal);
        return at < 0 ? line : line[..at];
    }

    private static IEnumerable<string> MarkdownLinks(string markdown)
    {
        var at = 0;
        while ((at = markdown.IndexOf("](", at, StringComparison.Ordinal)) >= 0)
        {
            var end = markdown.IndexOf(')', at + 2);
            if (end < 0)
            {
                yield break;
            }

            yield return markdown[(at + 2)..end];
            at = end;
        }
    }
}

/// <summary>
/// The defaults that follow from the live probes, as recorded in SPEC.md section 4 ("Resulting defaults"). If one of these
/// changes, the table there must change with it.
/// </summary>
public sealed class QuirkDefaultsTests
{
    [Fact]
    public void Srcvers366_speaker_runs_the_realtime_stream_under_NTP_with_the_wrapped_retransmit_form()
    {
        // P1 (control and audio part): NTP accepted, 0xD2 requests arrive; P14: wrapped 0xD6 answers are taken.
        var speakerA = KnownReceiverQuirks.For("Test Speaker ABCD", "366.0");
        Assert.Equal(TimingProtocol.Ntp, speakerA.TimingProtocol);
        Assert.False(speakerA.RetransmitOnDataPort);
        Assert.False(speakerA.SendAuthSetup);
        Assert.Equal(0, speakerA.OffsetFrames);
        Assert.True(speakerA.VolumeAfterStreamSetup); // P3: a volume before the stream SETUP is not applied
        Assert.Same(speakerA, KnownReceiverQuirks.For(model: null, "366.0")); // keyed on srcvers, not on the model
    }

    [Fact]
    public void Shairport_sync_runs_under_PTP_and_unknown_receivers_take_the_defaults()
    {
        // shairport-sync 5.5.2 answers an NTP session SETUP with 200 and no body (SPEC-control.md 4.2 note).
        // shairport-sync also reports srcvers 366.0; its model rule wins.
        Assert.Equal(TimingProtocol.Ptp, KnownReceiverQuirks.For("ShairportSync", "366.0").TimingProtocol);
        Assert.Same(ReceiverQuirks.Default, KnownReceiverQuirks.For("Some Apple receiver", "870.14.1")); // receiver C: untested (P20)
        Assert.Null(ReceiverQuirks.Default.TimingProtocol);
        Assert.False(ReceiverQuirks.Default.RetransmitOnDataPort);
        Assert.False(ReceiverQuirks.Default.SendAuthSetup);
    }

    [Fact]
    public void Sender_defaults_follow_D1_D6_and_the_timeout_observations()
    {
        var options = new AirplaySenderOptions();
        Assert.Equal(TimingProtocol.Ntp, options.DefaultTimingProtocol);
        Assert.Equal(StreamType.Realtime, options.PreferredStream); // P16: the buffered stream does not play on the srcvers-366 speaker
        Assert.Equal(TimeSpan.FromSeconds(30), options.Connection.RequestTimeout); // receiver A's answers took up to ~10 s
        Assert.Equal(TimeSpan.FromSeconds(25), options.Connection.ConnectTimeout); // D6
        Assert.Equal(TlvMethod.PairSetup, options.Session.PinPairingMethod); // D2; P6: receiver A accepts method 0 and 1
    }

    [Fact]
    public void Wire_constants_follow_P21_D1_and_D10()
    {
        Assert.Equal(1024, HapFrameCipher.MaxSendFramePlaintext); // P21: a 2048-byte frame makes receiver A close the connection
        Assert.Equal(ushort.MaxValue, HapFrameCipher.MaxReceiveFramePlaintext);
        Assert.Equal(77175, RealtimeStreamConstants.DefaultLatencyFrames); // D1; P7 (microphone) still open
        Assert.Equal(352, RealtimeStreamConstants.FramesPerPacket);
        Assert.Equal(-25.5, VolumeMapping.LevelToDb(15), 6); // the live-test level, read back by GET_PARAMETER as -25.5
        Assert.Equal(-144.0, VolumeMapping.LevelToDb(0), 6);
    }

    [Fact]
    public void Player_and_grandmaster_defaults_follow_D10_P17_and_the_teardown_observation()
    {
        var player = new GroupPlayerOptions();
        Assert.Equal(TimeSpan.FromSeconds(3), player.VolumePollInterval); // D10 / P4 (no event-channel volume seen yet)
        Assert.Equal(TimeSpan.FromSeconds(10), player.TeardownTimeout); // receiver A sometimes leaves TEARDOWN unanswered
        Assert.Equal(RealtimeStreamConstants.DefaultLatencyFrames, player.LatencyFrames);

        var ptp = new PtpMasterOptions();
        Assert.False(ptp.DropSilentPeers); // P17: nqptp goes silent after two Announces but follows our clock
        Assert.Equal(TimeSpan.FromSeconds(15), ptp.PeerTimeout);
    }

    [Fact]
    public void Rtsp_connection_defaults_are_D6()
    {
        var options = new RtspConnectionOptions();
        Assert.Equal(TimeSpan.FromSeconds(25), options.ConnectTimeout);
        Assert.Equal(TimeSpan.FromSeconds(10), options.RequestTimeout); // the facade and the probes raise it to 30 s
    }
}

/// <summary>Finds the repository root (the folder with <c>TuneShuttle.sln</c>) and lists its source files.</summary>
internal static class RepositoryRoot
{
    private static readonly string[] SkippedFolders = ["bin", "obj", "TestResults", "_framework", "node_modules", ".vs"];

    /// <summary>The repository root, searched upwards from the test binaries and from the current directory; null if absent.</summary>
    public static string? Find()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "TuneShuttle.sln")))
                {
                    return dir.FullName;
                }
            }
        }

        return null;
    }

    /// <summary>Files under <paramref name="folders"/> of <paramref name="root"/> with one of <paramref name="extensions"/>, skipping build output.</summary>
    public static IEnumerable<string> SourceFiles(string root, IEnumerable<string> folders, IReadOnlyCollection<string> extensions)
    {
        foreach (var folder in folders)
        {
            var start = Path.Combine(root, folder);
            if (!Directory.Exists(start))
            {
                continue;
            }

            var pending = new Stack<string>();
            pending.Push(start);
            while (pending.Count > 0)
            {
                var dir = pending.Pop();
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    if (!SkippedFolders.Contains(Path.GetFileName(sub), StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(sub);
                    }
                }

                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    if (extensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    {
                        yield return file;
                    }
                }
            }
        }
    }
}

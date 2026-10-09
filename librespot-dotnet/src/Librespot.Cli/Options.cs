// Port of src/main.rs (the getopts option table of `get_setup`) plus a getopts-compatible parser.
//
// Upstream uses the `getopts` crate (0.2). There is no stable .NET equivalent that keeps getopts' parsing rules and
// help layout (System.CommandLine is still prerelease and formats help differently), so the parts of getopts that
// librespot uses (plus `optflagopt`) are re-implemented here: `optflag`/`optopt`/`optmulti`, `parse` (short clusters, `-xVALUE`,
// `--long=VALUE`, `--long VALUE`, `--` terminator, free arguments), the `Matches` queries and `usage`.
using System.Text;

namespace Librespot.Cli;

/// <summary>Whether an option takes an argument (getopts <c>HasArg</c>).</summary>
public enum HasArg
{
    /// <summary>The option requires an argument.</summary>
    Yes,

    /// <summary>The option takes no argument.</summary>
    No,

    /// <summary>The option argument is optional.</summary>
    Maybe,
}

/// <summary>How often an option may occur (getopts <c>Occur</c>).</summary>
public enum Occur
{
    /// <summary>The option must occur exactly once.</summary>
    Req,

    /// <summary>The option occurs at most once.</summary>
    Optional,

    /// <summary>The option may occur any number of times.</summary>
    Multi,
}

/// <summary>The getopts <c>Fail</c> variants.</summary>
public enum GetoptsFail
{
    /// <summary>The option requires an argument but none was passed.</summary>
    ArgumentMissing,

    /// <summary>Passed an unrecognized option.</summary>
    UnrecognizedOption,

    /// <summary>A required option is not present.</summary>
    OptionMissing,

    /// <summary>A single occurrence option is being used multiple times.</summary>
    OptionDuplicated,

    /// <summary>There's an argument being passed to a non-argument option.</summary>
    UnexpectedArgument,
}

/// <summary>A command line parsing failure (getopts <c>Fail</c>); <see cref="Exception.Message"/> renders like its <c>Display</c>.</summary>
public sealed class GetoptsException : Exception
{
    /// <summary>Creates the failure.</summary>
    /// <param name="fail">The variant.</param>
    /// <param name="name">The option name the variant refers to.</param>
    public GetoptsException(GetoptsFail fail, string name)
        : base(Describe(fail, name))
    {
        Fail = fail;
        Name = name;
    }

    /// <summary>The variant.</summary>
    public GetoptsFail Fail { get; }

    /// <summary>The option name the failure refers to.</summary>
    public string Name { get; }

    private static string Describe(GetoptsFail fail, string name) => fail switch
    {
        GetoptsFail.ArgumentMissing => $"Argument to option '{name}' missing",
        GetoptsFail.UnrecognizedOption => $"Unrecognized option: '{name}'",
        GetoptsFail.OptionMissing => $"Required option '{name}' missing",
        GetoptsFail.OptionDuplicated => $"Option '{name}' given more than once",
        GetoptsFail.UnexpectedArgument => $"Option '{name}' does not take an argument",
        _ => name,
    };
}

/// <summary>
/// A description of the options a program accepts, and the parser for them (getopts <c>Options</c>).
/// Only the default parsing style (<c>FloatingFrees</c>, not long-only) is implemented, which is what librespot uses.
/// </summary>
public sealed class Options
{
    private readonly List<OptGroup> _groups = [];

    /// <summary>One registered option (getopts <c>OptGroup</c>).</summary>
    internal sealed record OptGroup(string ShortName, string LongName, string Hint, string Desc, HasArg HasArg, Occur Occur);

    /// <summary>The option name as matched while parsing (getopts <c>Name</c>): a single character is a short name.</summary>
    internal readonly record struct Name(string Value, bool IsShort)
    {
        public static Name FromStr(string nm) => new(nm, nm.Length == 1);

        public override string ToString() => Value;
    }

    /// <summary>The flattened option used for lookups (getopts <c>Opt</c>): long name (if any) with the short name as alias.</summary>
    internal sealed record Opt(Name Name, HasArg HasArg, Occur Occur, Name? Alias);

    /// <summary>The registered options, in registration order.</summary>
    internal IReadOnlyList<OptGroup> Groups => _groups;

    /// <summary>Creates a flag that takes no argument (getopts <c>optflag</c>).</summary>
    /// <param name="shortName">The one-character short name, or empty for none.</param>
    /// <param name="longName">The long name, or empty for none.</param>
    /// <param name="desc">The description shown in the help.</param>
    public Options OptFlag(string shortName, string longName, string desc) =>
        Add(shortName, longName, string.Empty, desc, HasArg.No, Occur.Optional);

    /// <summary>Creates an option that takes an argument and may occur at most once (getopts <c>optopt</c>).</summary>
    /// <param name="shortName">The one-character short name, or empty for none.</param>
    /// <param name="longName">The long name, or empty for none.</param>
    /// <param name="desc">The description shown in the help.</param>
    /// <param name="hint">The argument placeholder shown in the help.</param>
    public Options OptOpt(string shortName, string longName, string desc, string hint) =>
        Add(shortName, longName, hint, desc, HasArg.Yes, Occur.Optional);

    /// <summary>Creates an option that takes an argument and may occur multiple times (getopts <c>optmulti</c>).</summary>
    /// <param name="shortName">The one-character short name, or empty for none.</param>
    /// <param name="longName">The long name, or empty for none.</param>
    /// <param name="desc">The description shown in the help.</param>
    /// <param name="hint">The argument placeholder shown in the help.</param>
    public Options OptMulti(string shortName, string longName, string desc, string hint) =>
        Add(shortName, longName, hint, desc, HasArg.Yes, Occur.Multi);

    /// <summary>Creates an option that may take an argument and may occur at most once (getopts <c>optflagopt</c>; unused by librespot).</summary>
    /// <param name="shortName">The one-character short name, or empty for none.</param>
    /// <param name="longName">The long name, or empty for none.</param>
    /// <param name="desc">The description shown in the help.</param>
    /// <param name="hint">The argument placeholder shown in the help.</param>
    public Options OptFlagOpt(string shortName, string longName, string desc, string hint) =>
        Add(shortName, longName, hint, desc, HasArg.Maybe, Occur.Optional);

    private Options Add(string shortName, string longName, string hint, string desc, HasArg hasArg, Occur occur)
    {
        // getopts `validate_names`
        if (shortName.Length > 1)
        {
            throw new ArgumentException("the short_name (first argument) should be a single character, or an empty string for none", nameof(shortName));
        }

        if (longName.Length == 1)
        {
            throw new ArgumentException("the long_name (second argument) should be longer than a single character, or an empty string for none", nameof(longName));
        }

        _groups.Add(new OptGroup(shortName, longName, hint, desc, hasArg, occur));
        return this;
    }

    /// <summary>
    /// Parses command line arguments according to the provided options (getopts <c>parse</c>). The arguments must not
    /// include the program name.
    /// </summary>
    /// <param name="args">The arguments after the program name.</param>
    /// <exception cref="GetoptsException">The arguments do not match the options.</exception>
    public Matches Parse(IEnumerable<string> args)
    {
        var opts = _groups.Select(LongToShort).ToList();
        var vals = opts.Select(_ => new List<(int Pos, string? Val)>()).ToList();
        var free = new List<string>();

        // An indexed list stands in for getopts' peekable iterator.
        var argList = args.ToList();
        var argIndex = 0;
        var argPos = 0;
        while (argIndex < argList.Count)
        {
            var cur = argList[argIndex++];
            if (!IsArg(cur))
            {
                // ParsingStyle::FloatingFrees
                free.Add(cur);
            }
            else if (cur == "--")
            {
                free.AddRange(argList.Skip(argIndex));
                break;
            }
            else
            {
                var names = new List<Name>();
                string? iArg = null;
                var wasLong = cur[1] == '-';

                if (wasLong)
                {
                    var tail = cur[2..];
                    var eq = tail.IndexOf('=');
                    if (eq >= 0)
                    {
                        names.Add(Name.FromStr(tail[..eq]));
                        iArg = tail[(eq + 1)..];
                    }
                    else
                    {
                        names.Add(Name.FromStr(tail));
                    }
                }
                else
                {
                    for (var j = 1; j < cur.Length; j++)
                    {
                        // A non-BMP character is one Rust `char` but two UTF-16 code units.
                        var chLen = char.IsHighSurrogate(cur[j]) && j + 1 < cur.Length && char.IsLowSurrogate(cur[j + 1]) ? 2 : 1;
                        var opt = new Name(cur.Substring(j, chLen), true);
                        var optId = FindOpt(opts, opt) ?? throw new GetoptsException(GetoptsFail.UnrecognizedOption, opt.ToString());
                        names.Add(opt);

                        var argFollows = opts[optId].HasArg is HasArg.Yes or HasArg.Maybe;
                        if (argFollows)
                        {
                            var next = j + chLen;
                            if (next < cur.Length)
                            {
                                iArg = cur[next..];
                                break;
                            }
                        }

                        j += chLen - 1;
                    }
                }

                var namePos = 0;
                foreach (var nm in names)
                {
                    namePos++;
                    var optId = FindOpt(opts, nm) ?? throw new GetoptsException(GetoptsFail.UnrecognizedOption, nm.ToString());
                    switch (opts[optId].HasArg)
                    {
                        case HasArg.No:
                            if (namePos == names.Count && iArg is not null)
                            {
                                throw new GetoptsException(GetoptsFail.UnexpectedArgument, nm.ToString());
                            }

                            vals[optId].Add((argPos, null));
                            break;

                        case HasArg.Maybe:
                            // Note that here we do not handle `--arg value`.
                            // This matches GNU getopt behavior; but also
                            // makes sense, because if this were accepted,
                            // then users could only write a "Maybe" long
                            // option at the end of the arguments when
                            // FloatingFrees is in use.
                            if (iArg is not null)
                            {
                                vals[optId].Add((argPos, iArg));
                                iArg = null;
                            }
                            else if (wasLong || argIndex >= argList.Count || IsArg(argList[argIndex]))
                            {
                                vals[optId].Add((argPos, null));
                            }
                            else
                            {
                                // A short option without an attached value takes the next argument unless it is an option.
                                vals[optId].Add((argPos, argList[argIndex++]));
                            }

                            break;

                        case HasArg.Yes:
                            if (iArg is not null)
                            {
                                vals[optId].Add((argPos, iArg));
                                iArg = null;
                            }
                            else if (argIndex < argList.Count)
                            {
                                vals[optId].Add((argPos, argList[argIndex++]));
                            }
                            else
                            {
                                throw new GetoptsException(GetoptsFail.ArgumentMissing, nm.ToString());
                            }

                            break;
                    }
                }
            }

            argPos++;
        }

        for (var i = 0; i < opts.Count; i++)
        {
            if (opts[i].Occur == Occur.Req && vals[i].Count == 0)
            {
                throw new GetoptsException(GetoptsFail.OptionMissing, opts[i].Name.ToString());
            }

            if (opts[i].Occur != Occur.Multi && vals[i].Count > 1)
            {
                throw new GetoptsException(GetoptsFail.OptionDuplicated, opts[i].Name.ToString());
            }
        }

        return new Matches(opts, vals, free);
    }

    /// <summary>
    /// Derives a usage message from the options (getopts <c>usage</c>): <paramref name="brief"/>, an empty line,
    /// "Options:" and one row per option, descriptions wrapped at 54 columns starting at column 24.
    /// </summary>
    /// <param name="brief">The text printed before the option list.</param>
    public string Usage(string brief) => $"{brief}\n\nOptions:\n{string.Join("\n", UsageItems())}\n";

    private IEnumerable<string> UsageItems()
    {
        var descSep = "\n" + new string(' ', 24);
        var anyShort = _groups.Any(g => g.ShortName.Length != 0);

        foreach (var g in _groups)
        {
            var row = new StringBuilder("    ");

            // short option
            switch (g.ShortName.Length)
            {
                case 0:
                    if (anyShort)
                    {
                        row.Append("    ");
                    }

                    break;
                default:
                    row.Append('-').Append(g.ShortName);
                    // Only a single space when there is no long name, so that any argument is printed in the correct spot.
                    row.Append(g.LongName.Length > 0 ? ", " : " ");
                    break;
            }

            // long option
            if (g.LongName.Length > 0)
            {
                row.Append("--").Append(g.LongName).Append(' ');
            }

            // arg
            switch (g.HasArg)
            {
                case HasArg.Yes:
                    row.Append(g.Hint);
                    break;
                case HasArg.Maybe:
                    row.Append('[').Append(g.Hint).Append(']');
                    break;
            }

            var rowLen = row.Length;
            if (rowLen < 24)
            {
                row.Append(' ', 24 - rowLen);
            }
            else
            {
                row.Append(descSep);
            }

            row.Append(string.Join(descSep, EachSplitWithin(g.Desc, 54)));

            yield return row.ToString();
        }
    }

    /// <summary>getopts <c>each_split_within</c>: greedy word wrap of every line of <paramref name="desc"/> at <paramref name="lim"/> columns.</summary>
    internal static List<string> EachSplitWithin(string desc, int lim)
    {
        var rows = new List<string>();
        foreach (var line in desc.Trim().Split('\n'))
        {
            // Rust `str::lines` also strips a trailing '\r'.
            var l = line.EndsWith('\r') ? line[..^1] : line;
            var words = l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            var row = new StringBuilder();
            foreach (var word in words)
            {
                var sepWidth = row.Length > 0 ? 1 : 0;
                var width = row.Length + word.Length + sepWidth;

                if (width <= lim)
                {
                    if (sepWidth > 0)
                    {
                        row.Append(' ');
                    }

                    row.Append(word);
                    continue;
                }

                if (row.Length > 0)
                {
                    rows.Add(row.ToString());
                    row.Clear();
                }

                row.Append(word);
            }

            if (row.Length > 0)
            {
                rows.Add(row.ToString());
            }
        }

        return rows;
    }

    private static bool IsArg(string arg) => arg.Length > 1 && arg[0] == '-';

    private static Opt LongToShort(OptGroup g)
    {
        return (g.ShortName.Length, g.LongName.Length) switch
        {
            (0, 0) => throw new InvalidOperationException("this long-format option was given no name"),
            (0, _) => new Opt(new Name(g.LongName, false), g.HasArg, g.Occur, null),
            (_, 0) => new Opt(new Name(g.ShortName, true), g.HasArg, g.Occur, null),
            _ => new Opt(new Name(g.LongName, false), g.HasArg, g.Occur, new Name(g.ShortName, true)),
        };
    }

    /// <summary>getopts <c>find_opt</c>: search the main names first, then the aliases.</summary>
    private static int? FindOpt(List<Opt> opts, Name nm)
    {
        for (var i = 0; i < opts.Count; i++)
        {
            if (opts[i].Name == nm)
            {
                return i;
            }
        }

        for (var i = 0; i < opts.Count; i++)
        {
            if (opts[i].Alias == nm)
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The result of a successful <see cref="Parse"/> (getopts <c>Matches</c>).</summary>
    public sealed class Matches
    {
        private readonly List<Opt> _opts;
        private readonly List<List<(int Pos, string? Val)>> _vals;

        internal Matches(List<Opt> opts, List<List<(int Pos, string? Val)>> vals, List<string> free)
        {
            _opts = opts;
            _vals = vals;
            Free = free;
        }

        /// <summary>Free arguments (everything that is not an option or an option argument).</summary>
        public IReadOnlyList<string> Free { get; }

        private List<(int Pos, string? Val)> OptVals(string nm)
        {
            var id = FindOpt(_opts, Name.FromStr(nm)) ?? throw new InvalidOperationException($"No option '{nm}' defined");
            return _vals[id];
        }

        /// <summary>Returns true if an option was defined (getopts <c>opt_defined</c>).</summary>
        /// <param name="nm">A short or long option name.</param>
        public bool OptDefined(string nm) => FindOpt(_opts, Name.FromStr(nm)) is not null;

        /// <summary>Returns true if an option was matched (getopts <c>opt_present</c>).</summary>
        /// <param name="nm">A short or long option name.</param>
        /// <exception cref="InvalidOperationException">The option is not defined (getopts panics).</exception>
        public bool OptPresent(string nm) => OptVals(nm).Count > 0;

        /// <summary>
        /// Returns the first argument supplied to a matching option, or null when the option is absent or was given
        /// without an argument (getopts <c>opt_str</c>).
        /// </summary>
        /// <param name="nm">A short or long option name.</param>
        /// <exception cref="InvalidOperationException">The option is not defined (getopts panics).</exception>
        public string? OptStr(string nm)
        {
            var vals = OptVals(nm);
            return vals.Count > 0 ? vals[0].Val : null;
        }

        /// <summary>Returns every argument supplied to a matching option, in order (getopts <c>opt_strs</c>).</summary>
        /// <param name="nm">A short or long option name.</param>
        /// <exception cref="InvalidOperationException">The option is not defined (getopts panics).</exception>
        public IReadOnlyList<string> OptStrs(string nm) =>
            [.. OptVals(nm).Where(v => v.Val is not null).Select(v => v.Val!)];
    }
}

/// <summary>
/// The librespot option table of upstream <c>get_setup</c>: long names, short names, descriptions and hints, in
/// upstream's registration order (which is also the order of the help output).
/// </summary>
/// <remarks>
/// The descriptions are the ones of an upstream build with the default features (rodio backend, libmdns, no alsa,
/// with the passthrough decoder), because this port has a rodio-like default backend (NAudio), the built-in mDNS
/// responder, no alsa, and the passthrough decoder.
/// </remarks>
public static class CliOptions
{
    public const string AccessToken = "access-token";
    public const string ApPort = "ap-port";
    public const string Autoplay = "autoplay";
    public const string Backend = "backend";
    public const string Bitrate = "bitrate";
    public const string Cache = "cache";
    public const string CacheSizeLimit = "cache-size-limit";
    public const string Device = "device";
    public const string DeviceType = "device-type";
    public const string DeviceIsGroup = "group";
    public const string DisableAudioCache = "disable-audio-cache";
    public const string DisableCredentialCache = "disable-credential-cache";
    public const string DisableDiscovery = "disable-discovery";
    public const string DisableGapless = "disable-gapless";
    public const string Dither = "dither";
    public const string EmitSinkEvents = "emit-sink-events";
    public const string EnableDeviceAuth = "enable-device-auth";
    public const string EnableOAuth = "enable-oauth";
    public const string EnableVolumeNormalisation = "enable-volume-normalisation";
    public const string Format = "format";
    public const string Help = "help";
    public const string InitialVolume = "initial-volume";
    public const string MixerType = "mixer";
    public const string AlsaMixerDevice = "alsa-mixer-device";
    public const string AlsaMixerIndex = "alsa-mixer-index";
    public const string AlsaMixerControl = "alsa-mixer-control";
    public const string Name = "name";
    public const string NormalisationAttack = "normalisation-attack";
    public const string NormalisationGainType = "normalisation-gain-type";
    public const string NormalisationKnee = "normalisation-knee";
    public const string NormalisationMethod = "normalisation-method";
    public const string NormalisationPregain = "normalisation-pregain";
    public const string NormalisationRelease = "normalisation-release";
    public const string NormalisationThreshold = "normalisation-threshold";
    public const string OAuthPort = "oauth-port";
    public const string OnEvent = "onevent";
    public const string Passthrough = "passthrough";
    public const string Password = "password";
    public const string Proxy = "proxy";
    public const string Quiet = "quiet";
    public const string SystemCache = "system-cache";
    public const string TempDir = "tmp";
    public const string Username = "username";
    public const string Verbose = "verbose";
    public const string Version = "version";
    public const string VolumeCtrl = "volume-ctrl";
    public const string VolumeRange = "volume-range";
    public const string VolumeSteps = "volume-steps";
    public const string ZeroconfPort = "zeroconf-port";
    public const string ZeroconfInterface = "zeroconf-interface";
    public const string ZeroconfBackend = "zeroconf-backend";
    public const string LocalFileDir = "local-file-dir";

    // Mostly arbitrary.
    public const string ApPortShort = "a";
    public const string AutoplayShort = "A";
    public const string BackendShort = "B";
    public const string BitrateShort = "b";
    public const string SystemCacheShort = "C";
    public const string CacheShort = "c";
    public const string DitherShort = "D";
    public const string DeviceShort = "d";
    public const string VolumeCtrlShort = "E";
    public const string VolumeRangeShort = "e";
    public const string VolumeStepsShort = ""; // no short flag
    public const string DeviceTypeShort = "F";
    public const string FormatShort = "f";
    public const string DisableAudioCacheShort = "G";
    public const string DisableGaplessShort = "g";
    public const string DisableCredentialCacheShort = "H";
    public const string HelpShort = "h";
    public const string ZeroconfInterfaceShort = "i";
    public const string EnableOAuthShort = "j";
    public const string EnableDeviceAuthShort = ""; // no short flag
    public const string OAuthPortShort = "K";
    public const string AccessTokenShort = "k";
    public const string CacheSizeLimitShort = "M";
    public const string MixerTypeShort = "m";
    public const string EnableVolumeNormalisationShort = "N";
    public const string NameShort = "n";
    public const string DisableDiscoveryShort = "O";
    public const string OnEventShort = "o";
    public const string PassthroughShort = "P";
    public const string PasswordShort = "p";
    public const string EmitSinkEventsShort = "Q";
    public const string QuietShort = "q";
    public const string InitialVolumeShort = "R";
    public const string AlsaMixerDeviceShort = "S";
    public const string AlsaMixerIndexShort = "s";
    public const string AlsaMixerControlShort = "T";
    public const string TempDirShort = "t";
    public const string NormalisationAttackShort = "U";
    public const string UsernameShort = "u";
    public const string VersionShort = "V";
    public const string VerboseShort = "v";
    public const string NormalisationGainTypeShort = "W";
    public const string NormalisationKneeShort = "w";
    public const string NormalisationMethodShort = "X";
    public const string ProxyShort = "x";
    public const string NormalisationPregainShort = "Y";
    public const string NormalisationReleaseShort = "y";
    public const string NormalisationThresholdShort = "Z";
    public const string ZeroconfPortShort = "z";
    public const string ZeroconfBackendShort = ""; // no short flag
    public const string LocalFileDirShort = "l";

    // Options that have different descriptions depending on what backends were enabled at build time
    // (upstream `#[cfg(not(feature = "alsa-backend"))]` and `#[cfg(feature = "rodio-backend")]` variants).
    public const string MixerTypeDesc = "Not supported by the included audio backend(s).";
    public const string DeviceDesc = "Audio device to use. Use ? to list options if using alsa, portaudio or rodio. Defaults to the backend's default.";
    public const string AlsaMixerControlDesc = "Not supported by the included audio backend(s).";
    public const string AlsaMixerDeviceDesc = "Not supported by the included audio backend(s).";
    public const string AlsaMixerIndexDesc = "Not supported by the included audio backend(s).";
    public const string InitialVolumeDesc = "Initial volume in % from 0 - 100. Defaults to 50.";
    public const string VolumeRangeDesc = "Range of the volume control (dB) from 0.0 to 100.0. Defaults to 60.0.";
    public const string VolumeStepsDesc = "Number of incremental steps when responding to volume control updates. Defaults to 64.";

    /// <summary>Builds the option table exactly as upstream <c>get_setup</c> registers it.</summary>
    public static Options Build()
    {
        var opts = new Options();
        opts.OptFlag(HelpShort, Help, "Print this help menu.")
            .OptFlag(VersionShort, Version, "Display librespot version string.")
            .OptFlag(VerboseShort, Verbose, "Enable verbose log output.")
            .OptFlag(QuietShort, Quiet, "Only log warning and error messages.")
            .OptFlag(DisableAudioCacheShort, DisableAudioCache, "Disable caching of the audio data.")
            .OptFlag(DisableCredentialCacheShort, DisableCredentialCache, "Disable caching of credentials.")
            .OptFlag(DisableDiscoveryShort, DisableDiscovery, "Disable zeroconf discovery mode.")
            .OptFlag(DisableGaplessShort, DisableGapless, "Disable gapless playback.")
            .OptFlag(EmitSinkEventsShort, EmitSinkEvents, "Run PROGRAM set by `--onevent` before the sink is opened and after it is closed.")
            .OptFlag(EnableVolumeNormalisationShort, EnableVolumeNormalisation, "Play all tracks at approximately the same apparent volume.")
            .OptFlag(EnableOAuthShort, EnableOAuth, "Perform interactive OAuth sign in.")
            .OptFlag(EnableDeviceAuthShort, EnableDeviceAuth, "Perform OAuth sign in by pairing a code at spotify.com/pair. Needs no browser on this machine.")
            .OptOpt(NameShort, Name, "Device name. Defaults to Librespot.", "NAME")
            .OptOpt(BitrateShort, Bitrate, "Bitrate (kbps) {96|160|320}. Defaults to 160.", "BITRATE")
            .OptOpt(FormatShort, Format, "Output format {F64|F32|S32|S24|S24_3|S16}. Defaults to S16.", "FORMAT")
            .OptOpt(DitherShort, Dither, "Specify the dither algorithm to use {none|gpdf|tpdf|tpdf_hp}. Defaults to tpdf for formats S16, S24, S24_3 and none for other formats.", "DITHER")
            .OptOpt(DeviceTypeShort, DeviceType, "Displayed device type. Defaults to speaker.", "TYPE")
            .OptFlag("", DeviceIsGroup, "Whether the device represents a group. Defaults to false.")
            .OptOpt(TempDirShort, TempDir, "Path to a directory where files will be temporarily stored while downloading.", "PATH")
            .OptOpt(CacheShort, Cache, "Path to a directory where files will be cached after downloading.", "PATH")
            .OptOpt(SystemCacheShort, SystemCache, "Path to a directory where system files (credentials, volume) will be cached. May be different from the `--cache` option value.", "PATH")
            .OptOpt(CacheSizeLimitShort, CacheSizeLimit, "Limits the size of the cache for audio files. It's possible to use suffixes like K, M or G, e.g. 16G for example.", "SIZE")
            .OptOpt(BackendShort, Backend, "Audio backend to use. Use ? to list options.", "NAME")
            .OptOpt(UsernameShort, Username, "Username used to sign in with.", "USERNAME")
            .OptOpt(PasswordShort, Password, "Password used to sign in with.", "PASSWORD")
            .OptOpt(AccessTokenShort, AccessToken, "Spotify access token to sign in with.", "TOKEN")
            .OptOpt(OAuthPortShort, OAuthPort, "The port the oauth redirect server uses 1 - 65535. Ports <= 1024 may require root privileges.", "PORT")
            .OptOpt(OnEventShort, OnEvent, "Run PROGRAM when a playback event occurs.", "PROGRAM")
            .OptOpt(AlsaMixerControlShort, AlsaMixerControl, AlsaMixerControlDesc, "NAME")
            .OptOpt(AlsaMixerDeviceShort, AlsaMixerDevice, AlsaMixerDeviceDesc, "DEVICE")
            .OptOpt(AlsaMixerIndexShort, AlsaMixerIndex, AlsaMixerIndexDesc, "NUMBER")
            .OptOpt(MixerTypeShort, MixerType, MixerTypeDesc, "MIXER")
            .OptOpt(DeviceShort, Device, DeviceDesc, "NAME")
            .OptOpt(InitialVolumeShort, InitialVolume, InitialVolumeDesc, "VOLUME")
            .OptOpt(VolumeCtrlShort, VolumeCtrl, "Volume control scale type {cubic|fixed|linear|log}. Defaults to log.", "VOLUME_CTRL")
            .OptOpt(VolumeRangeShort, VolumeRange, VolumeRangeDesc, "RANGE")
            .OptOpt(VolumeStepsShort, VolumeSteps, VolumeStepsDesc, "STEPS")
            .OptOpt(NormalisationMethodShort, NormalisationMethod, "Specify the normalisation method to use {basic|dynamic}. Defaults to dynamic.", "METHOD")
            .OptOpt(NormalisationGainTypeShort, NormalisationGainType, "Specify the normalisation gain type to use {track|album|auto}. Defaults to auto.", "TYPE")
            .OptOpt(NormalisationPregainShort, NormalisationPregain, "Pregain (dB) applied by volume normalisation from -10.0 to 10.0. Defaults to 0.0.", "PREGAIN")
            .OptOpt(NormalisationThresholdShort, NormalisationThreshold, "Threshold (dBFS) at which point the dynamic limiter engages to prevent clipping from 0.0 to -10.0. Defaults to -2.0.", "THRESHOLD")
            .OptOpt(NormalisationAttackShort, NormalisationAttack, "Attack time (ms) in which the dynamic limiter reduces gain from 1 to 500. Defaults to 5.", "TIME")
            .OptOpt(NormalisationReleaseShort, NormalisationRelease, "Release or decay time (ms) in which the dynamic limiter restores gain from 1 to 1000. Defaults to 100.", "TIME")
            .OptOpt(NormalisationKneeShort, NormalisationKnee, "Knee width (dB) of the dynamic limiter from 0.0 to 10.0. Defaults to 5.0.", "KNEE")
            .OptOpt(ZeroconfPortShort, ZeroconfPort, "The port the internal server advertises over zeroconf 1 - 65535. Ports <= 1024 may require root privileges.", "PORT")
            .OptOpt(ProxyShort, Proxy, "HTTP proxy to use when connecting.", "URL")
            .OptOpt(ApPortShort, ApPort, "Connect to an AP with a specified port 1 - 65535. Available ports are usually 80, 443 and 4070.", "PORT")
            .OptOpt(AutoplayShort, Autoplay, "Explicitly set autoplay {on|off}. Defaults to following the client setting.", "OVERRIDE")
            .OptOpt(ZeroconfInterfaceShort, ZeroconfInterface, "Comma-separated interface IP addresses on which zeroconf will bind. Defaults to all interfaces. Ignored by DNS-SD.", "IP")
            .OptOpt(ZeroconfBackendShort, ZeroconfBackend, "Zeroconf (MDNS/DNS-SD) backend to use. Valid values are 'avahi', 'dns-sd' and 'libmdns', if librespot is compiled with the corresponding feature flags.", "BACKEND")
            .OptMulti(LocalFileDirShort, LocalFileDir, "Directory to search for local file playback. Can be specified multiple times to add multiple search directories", "DIRECTORY");

        // #[cfg(feature = "passthrough-decoder")]
        opts.OptFlag(PassthroughShort, Passthrough, "Pass a raw stream to the output. Only works with the pipe and subprocess backends.");

        return opts;
    }
}

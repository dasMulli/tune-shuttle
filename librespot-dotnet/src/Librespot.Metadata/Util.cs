// Port of metadata/src/util.rs.
//
// Upstream defines four macros that C# does not need as such:
//   impl_deref_wrapped!      -> every list wrapper derives from List<T> / Dictionary<K,V>, so it *is* its inner collection.
//   impl_from_repeated!      -> a list wrapper constructor taking IEnumerable<TMessage> that converts each element.
//   impl_try_from_repeated!  -> the same constructor; a failing element conversion throws LibrespotException.
//   impl_from_repeated_copy! -> a list wrapper constructor taking IEnumerable<TEnum> that copies (and, like
//                               rust-protobuf's enum_value_or_default, maps unknown values to the default).
// The helpers below cover the rust-protobuf accessor semantics the conversions rely on.
using Google.Protobuf;
using Librespot.Core;

namespace Librespot.Metadata;

/// <summary>Helpers that reproduce rust-protobuf accessor semantics for the metadata conversions.</summary>
internal static class Util
{
    /// <summary>
    /// rust-protobuf <c>MessageField::get_or_default()</c>: the message itself, or a default instance when the
    /// (optional) field is absent. C# protobuf represents an absent message field as <c>null</c>.
    /// </summary>
    public static T OrDefault<T>(T? message)
        where T : class, IMessage<T>, new() => message ?? new T();

    /// <summary>
    /// rust-protobuf <c>EnumOrUnknown::enum_value_or(default)</c> / <c>enum_value_or_default()</c>: C# protobuf keeps
    /// unknown enum numbers as an out-of-range cast, upstream replaces them with the default variant.
    /// </summary>
    public static TEnum EnumOrDefault<TEnum>(TEnum value, TEnum defaultValue = default)
        where TEnum : struct, Enum => Enum.IsDefined(value) ? value : defaultValue;

    /// <summary>
    /// Upstream <c>i32::try_into::&lt;u16&gt;()?</c>: a <c>TryFromIntError</c> converts into
    /// <see cref="ErrorKind.FailedPrecondition"/>.
    /// </summary>
    public static ushort ToUInt16(int value)
    {
        if (value is < ushort.MinValue or > ushort.MaxValue)
        {
            throw LibrespotException.FailedPrecondition("out of range integral type conversion attempted");
        }

        return (ushort)value;
    }
}

// Port of connect/src/state.rs (ConnectConfig only; ConnectState and the state/ submodules are package `connect-state`).
using Librespot.Core;

namespace Librespot.Connect;

/// <summary>Configuration of the connect device.</summary>
public sealed record ConnectConfig
{
    /// <summary>The name of the connect device (default: librespot).</summary>
    public string Name { get; init; } = "librespot";

    /// <summary>The icon type of the connect device (default: Speaker).</summary>
    public DeviceType DeviceType { get; init; } = DeviceType.Speaker;

    /// <summary>Displays the device type twice in the UI to show up as a group (default: false).</summary>
    public bool IsGroup { get; init; }

    /// <summary>The volume with which the connect device will be initialized (default: 50%).</summary>
    public ushort InitialVolume { get; init; } = ushort.MaxValue / 2;

    /// <summary>Disables the option to control the volume remotely (default: false).</summary>
    public bool DisableVolume { get; init; }

    /// <summary>Number of incremental steps (default: 64).</summary>
    public ushort VolumeSteps { get; init; } = 64;

    /// <summary>Emit <c>SetQueue</c> player events when the queue changes (default: false).</summary>
    public bool EmitSetQueueEvents { get; init; }
}

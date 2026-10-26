using System.Net;

namespace Airplay2.Receiver;

/// <summary>
/// What the session SETUP tells about the sender (SPEC-receiver.md 4.3: <c>name</c>, <c>model</c>, <c>deviceID</c>),
/// shown in the web app as "AirPlay · &lt;sender&gt;" (SPEC-receiver.md 9.2).
/// </summary>
/// <param name="Name">The sender's <c>name</c> (for example the phone's name), or null when it sent none.</param>
/// <param name="Model">The sender's <c>model</c>, or null.</param>
/// <param name="DeviceId">The sender's <c>deviceID</c>, or null.</param>
/// <param name="Address">The remote address of the control connection.</param>
public sealed record SenderInfo(string? Name, string? Model, string? DeviceId, IPAddress Address);

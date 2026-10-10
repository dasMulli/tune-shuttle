using Bridge.Web.Localization;

namespace Bridge.Web.Components;

/// <summary>
/// The event connection's error (<see cref="BridgeState.ConnectionError"/>) as the connection banner and the status
/// pill's title show it (UI-SPEC 3.5, 4.10, 7). <see cref="BridgeEventClient"/> stays unchanged (section 8) and writes
/// English: one sentence of its own when the bridge closes the socket, otherwise the runtime's exception message. That
/// text originates in the client, not in the service, so it is not shown as sent: the known sentence comes from the
/// catalog (banner.closedByBridge), and any other text, client-runtime English, is shown only while the app speaks
/// English. In every other language it is left out and the banner and the title use their error-free sentences (the
/// client logs the detail to the browser console).
/// </summary>
internal static class ConnectionError
{
    /// <summary>The sentence <see cref="BridgeEventClient"/> sets when the bridge closes the socket (its literal).</summary>
    internal const string ClosedByBridge = "The bridge closed the connection.";

    /// <summary>The language whose speakers see the runtime's own (English) error text.</summary>
    private const string English = "en";

    /// <summary>The error to embed in banner.pausedError / status.title.*, or null when none should be shown.</summary>
    public static string? Text(string? error, I18n l)
    {
        ArgumentNullException.ThrowIfNull(l);
        return error switch
        {
            null or "" => null,
            ClosedByBridge => l["banner.closedByBridge"],
            _ when l.Code == English => error,
            _ => null,
        };
    }
}

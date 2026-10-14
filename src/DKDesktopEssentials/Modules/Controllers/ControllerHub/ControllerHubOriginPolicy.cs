namespace DKDesktopEssentials.Modules.Controllers.ControllerHub;

/// <summary>
/// Reject non-local schemes, deceptive hostnames, credentials and alternate ports
/// before any native action is accepted from a WebView2 message.
/// </summary>
public static class ControllerHubOriginPolicy
{
    public const string TrustedHost = "dk.local";

    public static bool IsTrustedResourceUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == Uri.UriSchemeHttps &&
               uri.Port == 443 &&
               string.Equals(uri.Host, TrustedHost, StringComparison.OrdinalIgnoreCase) &&
               string.IsNullOrEmpty(uri.UserInfo);
    }

    public static bool IsTrustedDocumentUri(string? url)
    {
        if (!IsTrustedResourceUri(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        // This app is a single-page application: no other top-level pages are required.
        return uri.AbsolutePath is "/" or "/index.html";
    }
}

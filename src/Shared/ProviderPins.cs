namespace DKDesktopEssentials.Security;

// Digests of the exact root entries in the archive-hash-pinned official releases.
// These are compiled trust anchors, never read from source.json or caller arguments.
internal static class ProviderPins
{
    internal const string HidMaestroSdkSha256 = "4c4ad3c2a70837566794f4c2f2b671fb5a0a2b4523fe180a0f7e4ec6d482bd18";
    internal const string Sdl3LibrarySha256 = "1f98969319302a100931f4385e5918a0bd53ab07773040682d22e7edb54858c0";
}

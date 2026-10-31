using System.Diagnostics;
using System.IO;

namespace DKDesktopEssentials.Modules.Downloads;

internal static class DownloadFolderOpener
{
    internal static Task OpenAsync(string validatedDirectory)
    {
        // Only called with a native, leased directory belonging to a completed job.
        // Never launch the downloaded file or accept an executable/command from JS.
        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        var start = new ProcessStartInfo(explorer) { UseShellExecute = false };
        start.ArgumentList.Add(validatedDirectory);
        using var process = Process.Start(start);
        if (process is null) throw new DownloadEngineException(DownloadErrorCode.DestinationInvalid);
        return Task.CompletedTask;
    }
}

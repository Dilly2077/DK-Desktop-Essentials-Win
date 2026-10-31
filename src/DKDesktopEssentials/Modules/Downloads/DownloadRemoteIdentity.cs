using System.Net.Http.Headers;

namespace DKDesktopEssentials.Modules.Downloads;

internal static class DownloadRemoteIdentity
{
    internal static RangeConditionHeaderValue? IfRange(string? tag, DateTimeOffset? modified, DateTimeOffset? date)
    {
        if (tag is not null)
            return EntityTagHeaderValue.TryParse(tag, out var parsed) && !parsed.IsWeak && parsed.Tag != "*"
                ? new RangeConditionHeaderValue(parsed) : null;
        // RFC 9110 permits date validators only without an ETag and with a strong date.
        // Use a conservative 60-second separation to reduce clock ambiguity.
        return modified is { } m && date is { } d && d - m >= TimeSpan.FromSeconds(60)
            ? new RangeConditionHeaderValue(m) : null;
    }

    internal static RangeConditionHeaderValue? IfRange(DownloadProbe probe) => IfRange(probe.ETag, probe.LastModifiedUtc, probe.ResponseDateUtc);

    internal static void CheckResume(DownloadJobRecord saved, DownloadProbe current)
    {
        if (IfRange(saved.ETag, saved.LastModifiedUtc, saved.ResponseDateUtc) is null || IfRange(current) is null)
            throw new DownloadEngineException(DownloadErrorCode.RangeNotSupported);
        if (saved.ExpectedBytes is not { } size || current.ContentLength != size ||
            (saved.ResourceKey is { } key && current.ResourceKey != key) ||
            (saved.ETag is { } tag ? current.ETag != tag : current.LastModifiedUtc != saved.LastModifiedUtc))
            throw new DownloadEngineException(DownloadErrorCode.RemoteContentChanged);
    }
}

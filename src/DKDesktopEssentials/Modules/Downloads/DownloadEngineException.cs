namespace DKDesktopEssentials.Modules.Downloads;

/// <summary>Safe to report by code. Never retains URLs, headers, paths or transport exceptions.</summary>
public sealed class DownloadEngineException : Exception
{
    public DownloadErrorCode Code { get; }
    public TimeSpan? RetryAfter { get; }

    public DownloadEngineException(DownloadErrorCode code, TimeSpan? retryAfter = null) : base(Describe(code))
    { Code = code; RetryAfter = retryAfter > TimeSpan.Zero ? retryAfter : null; }

    private static string Describe(DownloadErrorCode code) => code switch
    {
        DownloadErrorCode.InvalidUrl => "Enter a valid HTTP or HTTPS URL and check the download details.",
        DownloadErrorCode.UnsupportedProtocol => "Only direct HTTP and HTTPS links are supported.",
        DownloadErrorCode.DestinationInvalid => "Choose an existing local folder and a valid Windows filename.",
        DownloadErrorCode.DestinationExists => "That filename already exists or is in use. Choose a different name.",
        DownloadErrorCode.PermissionDenied => "Windows denied access. Choose a folder you can write to.",
        DownloadErrorCode.DiskFull => "There is not enough available space at the destination. Free space, then choose Retry.",
        DownloadErrorCode.NetworkUnavailable or DownloadErrorCode.DnsFailure => "The server could not be reached. Check the connection and link.",
        DownloadErrorCode.Timeout => "The server stopped responding. Check the connection and try again.",
        DownloadErrorCode.TlsFailure => "The secure connection could not be verified. Check the link.",
        DownloadErrorCode.HttpUnauthorized or DownloadErrorCode.HttpForbidden => "The server denied access. Authenticated browser downloads are not supported yet.",
        DownloadErrorCode.HttpNotFound => "The requested file was not found on the server.",
        DownloadErrorCode.HttpRateLimited => "The server has limited requests. Wait before trying again.",
        DownloadErrorCode.HttpServerError => "The server reported an error. Try again later.",
        DownloadErrorCode.RangeNotSupported => "This server did not provide safe resume support. Your partial file is unchanged; submit a new download with a different name.",
        DownloadErrorCode.RemoteContentChanged => "The remote file changed between requests. Submit a new download.",
        DownloadErrorCode.PartialFileInvalid => "The temporary file could not be safely opened. Submit a new download.",
        DownloadErrorCode.ContentLengthMismatch => "The downloaded size did not match the expected size. The file was not completed.",
        DownloadErrorCode.ChecksumMismatch => "The SHA-256 checksum is invalid or does not match. The file was not completed.",
        DownloadErrorCode.StorageFailure => "Local download data or Windows safety metadata could not be saved or verified. Retained files have not been removed.",
        DownloadErrorCode.EngineUnavailable => "Downloads are busy or shutting down. Wait and try again.",
        DownloadErrorCode.QueueFull => "The local queue has reached its safety limit. Cancel older paused or failed jobs before adding more.",
        _ => "The download could not be completed. No successful save was confirmed."
    };
}

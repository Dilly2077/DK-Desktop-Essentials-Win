namespace DKDesktopEssentials.Modules.Downloads;

public enum DownloadTransitionCause
{
    SchedulerDispatch,
    ProbeSucceeded,
    TransferFinished,
    VerificationSucceeded,
    UserPause,
    UserResume,
    UserRetry,
    UserCancel,
    RetryableFailure,
    FatalFailure,
    StartupRecovery
}

/// <summary>
/// Only approved, reason-specific lifecycle transitions are legal.
/// Pure logic: no network, disk, shell or database side effects.
/// </summary>
public static class DownloadStateMachine
{
    public static bool CanTransition(
        DownloadJobState from, DownloadJobState to, DownloadTransitionCause cause)
    {
        if (!Enum.IsDefined(from) || !Enum.IsDefined(to) || !Enum.IsDefined(cause))
            return false;

        return (from, to, cause) switch
        {
            (DownloadJobState.Queued, DownloadJobState.Resolving, DownloadTransitionCause.SchedulerDispatch) => true,
            (DownloadJobState.Retrying, DownloadJobState.Resolving, DownloadTransitionCause.SchedulerDispatch) => true,
            (DownloadJobState.Resolving, DownloadJobState.Downloading, DownloadTransitionCause.ProbeSucceeded) => true,
            (DownloadJobState.Downloading, DownloadJobState.Verifying, DownloadTransitionCause.TransferFinished) => true,
            (DownloadJobState.Verifying, DownloadJobState.Completed, DownloadTransitionCause.VerificationSucceeded) => true,

            (DownloadJobState.Queued or DownloadJobState.Resolving or DownloadJobState.Downloading
                or DownloadJobState.Retrying or DownloadJobState.Verifying,
             DownloadJobState.Paused, DownloadTransitionCause.UserPause) => true,

            (DownloadJobState.Paused, DownloadJobState.Queued, DownloadTransitionCause.UserResume) => true,
            (DownloadJobState.Failed, DownloadJobState.Queued, DownloadTransitionCause.UserRetry) => true,

            (DownloadJobState.Queued or DownloadJobState.Resolving or DownloadJobState.Downloading
                or DownloadJobState.Paused or DownloadJobState.Retrying or DownloadJobState.Verifying
                or DownloadJobState.Failed,
             DownloadJobState.Cancelled, DownloadTransitionCause.UserCancel) => true,

            (DownloadJobState.Resolving or DownloadJobState.Downloading,
             DownloadJobState.Retrying, DownloadTransitionCause.RetryableFailure) => true,

            (DownloadJobState.Queued or DownloadJobState.Resolving or DownloadJobState.Downloading
                or DownloadJobState.Retrying or DownloadJobState.Verifying,
             DownloadJobState.Failed, DownloadTransitionCause.FatalFailure) => true,

            (DownloadJobState.Resolving or DownloadJobState.Downloading or DownloadJobState.Retrying
                or DownloadJobState.Verifying,
             DownloadJobState.Queued, DownloadTransitionCause.StartupRecovery) => true,

            _ => false
        };
    }

    public static DownloadJobRecord Advance(
        DownloadJobRecord job, DownloadJobState next, DownloadTransitionCause cause,
        DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(job);
        if (!CanTransition(job.State, next, cause))
            throw new InvalidOperationException(
                $"Illegal download transition {job.State} -> {next} ({cause}).");
        if (nowUtc.Offset != TimeSpan.Zero || nowUtc < job.UpdatedUtc)
            throw new ArgumentOutOfRangeException(nameof(nowUtc),
                "A transition requires a non-decreasing UTC timestamp.");
        if (job.Revision < 0)
            throw new ArgumentOutOfRangeException(nameof(job), "Invalid revision.");

        return job with
        {
            State = next,
            UpdatedUtc = nowUtc,
            Revision = checked(job.Revision + 1)
        };
    }

    public static bool IsTerminal(DownloadJobState state) =>
        state is DownloadJobState.Completed or DownloadJobState.Cancelled;

    public static bool HoldsTransferSlot(DownloadJobState state) =>
        state is DownloadJobState.Resolving or DownloadJobState.Downloading
            or DownloadJobState.Verifying;

    public static bool RequiresStartupRecovery(DownloadJobState state) =>
        state is DownloadJobState.Resolving or DownloadJobState.Downloading
            or DownloadJobState.Retrying or DownloadJobState.Verifying;
}

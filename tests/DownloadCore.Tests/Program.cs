using DKDesktopEssentials.Modules.Downloads;

static void Check(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static DownloadJobRecord Seed(DownloadJobState state = DownloadJobState.Queued) => new()
{
    Id = Guid.NewGuid(),
    SourceKey = "secure-vault-token",
    DisplayHost = "example.org",
    DestinationPath = @"C:\Downloads\test.bin",
    TemporaryPath = @"C:\Downloads\.test.part",
    CreatedUtc = DateTimeOffset.UnixEpoch,
    UpdatedUtc = DateTimeOffset.UnixEpoch,
    State = state,
    ExpectedBytes = 1024,
    TransferredBytes = 128,
    Revision = 0
};
static void Reject(Action operation)
{
    try { operation(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected invalid transition to be rejected.");
}
static Task Run(Action a) { a(); return Task.CompletedTask; }

var tests = new (string Name, Func<Task> Run)[]
{
    ("Happy path requires verification", () => Run(() =>
    {
        var t = DateTimeOffset.UnixEpoch.AddSeconds(1);
        var job = Seed();
        job = DownloadStateMachine.Advance(job, DownloadJobState.Resolving, DownloadTransitionCause.SchedulerDispatch, t);
        job = DownloadStateMachine.Advance(job, DownloadJobState.Downloading, DownloadTransitionCause.ProbeSucceeded, t);
        job = DownloadStateMachine.Advance(job, DownloadJobState.Verifying, DownloadTransitionCause.TransferFinished, t);
        Check(job.State != DownloadJobState.Completed, "Transfer completion incorrectly marks verified.");
        job = DownloadStateMachine.Advance(job, DownloadJobState.Completed, DownloadTransitionCause.VerificationSucceeded, t);
        Check(job.Revision == 4 && DownloadStateMachine.IsTerminal(job.State), "Completed job revision/terminal wrong.");
    })),
    ("Direct download-to-complete forbidden", () => Run(() =>
        Reject(() => DownloadStateMachine.Advance(Seed(DownloadJobState.Downloading),
            DownloadJobState.Completed, DownloadTransitionCause.VerificationSucceeded, DateTimeOffset.UnixEpoch)))),
    ("Pause and resume must requeue", () => Run(() =>
    {
        var paused = DownloadStateMachine.Advance(Seed(DownloadJobState.Downloading),
            DownloadJobState.Paused, DownloadTransitionCause.UserPause, DateTimeOffset.UnixEpoch);
        Check(!DownloadStateMachine.HoldsTransferSlot(paused.State), "Paused job holds transfer slot.");
        Reject(() => DownloadStateMachine.Advance(paused, DownloadJobState.Downloading,
            DownloadTransitionCause.UserResume, DateTimeOffset.UnixEpoch));
        var resumed = DownloadStateMachine.Advance(paused, DownloadJobState.Queued,
            DownloadTransitionCause.UserResume, DateTimeOffset.UnixEpoch);
        Check(resumed.State == DownloadJobState.Queued, "Resume not requeued.");
    })),
    ("Explicit manual retry only", () => Run(() =>
    {
        var failed = Seed(DownloadJobState.Failed);
        Reject(() => DownloadStateMachine.Advance(failed, DownloadJobState.Queued,
            DownloadTransitionCause.StartupRecovery, DateTimeOffset.UnixEpoch));
        var retried = DownloadStateMachine.Advance(failed, DownloadJobState.Queued,
            DownloadTransitionCause.UserRetry, DateTimeOffset.UnixEpoch);
        Check(retried.State == DownloadJobState.Queued, "Manual retry rejected.");
    })),
    ("Cancelled and completed jobs cannot restart", () => Run(() =>
    {
        foreach (var s in new[] { DownloadJobState.Cancelled, DownloadJobState.Completed })
        foreach (var dest in Enum.GetValues<DownloadJobState>())
        foreach (var cause in Enum.GetValues<DownloadTransitionCause>())
            Check(!DownloadStateMachine.CanTransition(s, dest, cause), $"Terminal state {s} escaped.");
    })),
    ("Recovery requeues in-flight work only", () => Run(() =>
    {
        var active = new[] { DownloadJobState.Resolving, DownloadJobState.Downloading,
            DownloadJobState.Retrying, DownloadJobState.Verifying };
        foreach (var state in Enum.GetValues<DownloadJobState>())
        {
            var needsRecovery = active.Contains(state);
            Check(DownloadStateMachine.RequiresStartupRecovery(state) == needsRecovery, "Recovery flag mismatch.");
            Check(DownloadStateMachine.CanTransition(state, DownloadJobState.Queued,
                DownloadTransitionCause.StartupRecovery) == needsRecovery, "Recovery transition mismatch.");
            if (!needsRecovery) continue;
            var original = Seed(state);
            var recovered = DownloadStateMachine.Advance(original, DownloadJobState.Queued,
                DownloadTransitionCause.StartupRecovery, DateTimeOffset.UnixEpoch);
            Check(recovered.TransferredBytes == original.TransferredBytes
                  && recovered.SourceKey == original.SourceKey, "Recovery discarded resume metadata.");
        }
    })),
    ("Retry queue path", () => Run(() =>
    {
        var retry = DownloadStateMachine.Advance(Seed(DownloadJobState.Downloading),
            DownloadJobState.Retrying, DownloadTransitionCause.RetryableFailure, DateTimeOffset.UnixEpoch);
        Check(!DownloadStateMachine.HoldsTransferSlot(retry.State), "Retry occupies slot.");
        Check(DownloadStateMachine.CanTransition(retry.State, DownloadJobState.Resolving,
            DownloadTransitionCause.SchedulerDispatch), "Retry cannot dispatch.");
    })),
    ("Invalid causes and duplicate transitions rejected", () => Run(() =>
    {
        Check(!DownloadStateMachine.CanTransition(DownloadJobState.Queued,
            DownloadJobState.Queued, DownloadTransitionCause.SchedulerDispatch), "Duplicate accepted.");
        Check(!DownloadStateMachine.CanTransition((DownloadJobState)999,
            DownloadJobState.Completed, DownloadTransitionCause.VerificationSucceeded), "Unknown enum accepted.");
        Check(!DownloadStateMachine.CanTransition(DownloadJobState.Verifying,
            DownloadJobState.Completed, DownloadTransitionCause.TransferFinished), "Invalid cause accepted.");
    })),
    ("UTC and revision monotonicity", () => Run(() =>
    {
        var job = Seed();
        var later = DateTimeOffset.UnixEpoch.AddSeconds(5);
        var valid = DownloadStateMachine.Advance(job, DownloadJobState.Resolving,
            DownloadTransitionCause.SchedulerDispatch, later);
        Check(valid.Revision == job.Revision + 1 && valid.UpdatedUtc == later, "Revision/time did not advance.");
        try
        {
            DownloadStateMachine.Advance(valid, DownloadJobState.Downloading,
                DownloadTransitionCause.ProbeSucceeded, DateTimeOffset.UnixEpoch);
            throw new Exception("Backwards timestamp accepted.");
        }
        catch (ArgumentOutOfRangeException) { }
    })),
    ("UI projection excludes source and temporary path", () => Run(() =>
    {
        var view = DownloadJobProjection.ToView(Seed());
        var properties = typeof(DownloadJobView).GetProperties().Select(p => p.Name).ToArray();
        Check(!properties.Contains("SourceKey") && !properties.Contains("TemporaryPath")
            && !properties.Contains("DestinationPath"), "Sensitive persistence fields leaked.");
        Check(view.DisplayHost == "example.org" && view.FileName == "test.bin", "View projection inaccurate.");
    }))
};

return await TestHarness.RunAsync("Downloads Slice 32.1 contracts", tests, args);

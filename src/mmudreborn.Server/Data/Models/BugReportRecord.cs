namespace mmudreborn.Data.Models;

public enum BugReportStatus
{
    Open = 0,
    Complete = 1,
    Verified = 2,
    StillPresent = 3,
    // A sysop closed the report as working-as-intended. Distinct from Complete because Complete
    // means "we changed something, please re-test" — it queues a reporter re-test prompt and the
    // reporter can bounce it back with STILLBUG. A not-a-bug close changed nothing, so re-testing
    // would (correctly) reproduce the behavior and reopen a report that was never wrong. This
    // status carries a required ResolutionNote instead, so the reporter gets the explanation.
    NotABug = 4,
}

public sealed class BugReportRecord
{
    public int Id { get; set; }
    public string ReporterBbsUserName { get; set; } = string.Empty;
    public string ReporterPlayerName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string BriefDescription { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string LocationText { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public BugReportStatus Status { get; set; }
    public bool IsCompleted
    {
        get => Status == BugReportStatus.Complete;
        set => Status = value ? BugReportStatus.Complete : BugReportStatus.Open;
    }
    // CompletedAt/CompletedByBbsUserName are the general sysop-resolution stamp: they record who
    // closed the report and when, for BOTH Complete and NotABug. Only the status distinguishes
    // which kind of close it was.
    public string CompletedAt { get; set; } = string.Empty;
    public string CompletedByBbsUserName { get; set; } = string.Empty;
    // The sysop's explanation, required when closing as NotABug and empty for every other status.
    // This is the whole point of the NotABug close — the reporter is owed a reason, not a silent
    // dismissal — so MarkBugNotABug refuses to write a blank one.
    public string ResolutionNote { get; set; } = string.Empty;
    public string ReporterReviewedAt { get; set; } = string.Empty;
    // Verified-by identity is tracked independently of the reporter (anyone may verify a
    // completed bug). VerifiedAt is the timestamp; ReporterReviewedAt continues to carry the
    // STILLBUG timestamp, which remains reporter-only.
    public string VerifiedAt { get; set; } = string.Empty;
    public string VerifiedByBbsUserName { get; set; } = string.Empty;
    public string VerifiedByPlayerName { get; set; } = string.Empty;
}

public sealed class BugReportSummary
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string BriefDescription { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
    public BugReportStatus Status { get; set; }
    public bool IsCompleted => Status == BugReportStatus.Complete;
    // Populated only by GetNotABugReportsForReporter, which needs the reason to show at login.
    public string ResolutionNote { get; set; } = string.Empty;
    public string CompletedByBbsUserName { get; set; } = string.Empty;
}
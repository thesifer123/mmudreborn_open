using System.Globalization;
using mmudreborn.Data.Models;
using mmudreborn.Game;

namespace mmudreborn.Server;

public partial class CommandParser
{
    private const int MaxBugTitleLength = 80;
    private const int MaxBugBriefLength = 120;
    private const int MaxBugLocationLength = 120;
    private const int MaxBugItemLength = 80;
    // Roomier than the one-line fields: a not-a-bug close has to carry an actual explanation
    // ("stock rolls Picklocks + difficulty, 0 means pure skill, not unpickable"), not a label.
    private const int MaxBugResolutionNoteLength = 500;

    /// <summary>
    /// The argument that means "show me the bug commands", in either number. The command family this
    /// documents is plural-heavy (BUGS / LISTBUGS / SHOWBUG), so "help bugs" is what people type; it
    /// used to fall through to the unknown-topic path because only the singular was matched.
    /// </summary>
    internal static bool IsBugHelpArgument(string args)
    {
        string trimmed = args.Trim();
        return trimmed.Equals("bug", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("bugs", StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleBug(string args)
    {
        if (args.Trim().Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await RenderBugHelpAsync();
            return;
        }

        await StartBugReportAsync(args);
    }

    private async Task HandleListBugs(string args = "")
    {
        // "bugs help" reads as naturally as "bug help"; without this it silently listed instead.
        if (args.Trim().Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await RenderBugHelpAsync();
            return;
        }

        var reports = _world.PlayerRepo.GetBugReports();

        await _client.SendLineAsync();
        await _client.SendLineAsync("Bug Reports");
        await _client.SendLineAsync("===========");

        if (reports.Count == 0)
        {
            await _client.SendLineAsync("No bug reports have been submitted yet.");
            return;
        }

        foreach (var report in reports.OrderBy(report => report.Id))
        {
            await _client.SendLineAsync($"#{report.Id} {FormatBugListTag(report.Status)}{report.Title} - {report.BriefDescription}");
        }
    }

    public async Task ShowCompletedBugReviewPromptsAsync()
    {
        string reporterBbsUserName = GetCurrentBugReporterBbsUserName();
        string reporterPlayerName = GetCurrentBugReporterPlayerName();

        var reports = _world.PlayerRepo.GetCompletedBugReportsForReporter(reporterBbsUserName, reporterPlayerName);
        var notABugReports = _world.PlayerRepo.GetUnseenNotABugReportsForReporter(reporterBbsUserName, reporterPlayerName);

        if (reports.Count == 0 && notABugReports.Count == 0)
            return;

        await _client.SendLineAsync();

        foreach (var report in reports.OrderBy(report => report.Id))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightYellow}You have Bug #{report.Id} marked as Complete. Please re-test and verify.{MudAnsi.Reset}");
        }

        // A not-a-bug close has no re-test step, so it gets no VERIFY/STILLBUG call to action — just
        // the explanation, shown once. Acknowledging inside the loop (rather than after it) means a
        // mid-render disconnect leaves the un-shown rows unstamped, so they surface next login.
        foreach (var report in notABugReports.OrderBy(report => report.Id))
        {
            await _client.SendLineAsync($"{MudAnsi.BrightCyan}Bug #{report.Id} was reviewed and closed as Not a Bug{FormatNotABugCloser(report)}.{MudAnsi.Reset}");
            foreach (string line in SplitBugNoteLines(report.ResolutionNote))
                await _client.SendLineAsync($"{MudAnsi.BrightCyan}  {line}{MudAnsi.Reset}");
            await _client.SendLineAsync($"{MudAnsi.BrightCyan}  (SHOWBUG {report.Id} to re-read this.){MudAnsi.Reset}");
            _world.PlayerRepo.AcknowledgeNotABugReport(report.Id);
        }

        await _client.SendLineAsync();
    }

    private static string FormatNotABugCloser(BugReportSummary report)
    {
        return string.IsNullOrWhiteSpace(report.CompletedByBbsUserName)
            ? string.Empty
            : $" by {report.CompletedByBbsUserName}";
    }

    private static IEnumerable<string> SplitBugNoteLines(string note)
    {
        return (note ?? string.Empty)
            .Split([Environment.NewLine, "\n"], StringSplitOptions.None)
            .Where(line => !string.IsNullOrWhiteSpace(line));
    }

    // Shared bug-load + not-found prologue. Returns the loaded report or null after sending the
    // standard "That bug report was not found." line. Five SHOW/COMPLETE/VERIFY/STILL/DELETE
    // handlers used to inline the same 4-line block.
    private async Task<mmudreborn.Data.Models.BugReportRecord?> LoadBugReportOrSendNotFoundAsync(int bugId)
    {
        var report = _world.PlayerRepo.LoadBugReport(bugId);
        if (report == null)
            await _client.SendLineAsync("That bug report was not found.");
        return report;
    }

    private async Task HandleShowBug(string args)
    {
        if (!TryParseBugId(args, out int bugId))
        {
            await _client.SendLineAsync("Syntax: SHOWBUG <number>");
            return;
        }

        await _client.SendLineAsync();
        var report = await LoadBugReportOrSendNotFoundAsync(bugId);
        if (report == null)
            return;

        await _client.SendLineAsync($"Bug #{report.Id}: {report.Title}");
        await _client.SendLineAsync($"Reported: {FormatBugTimestamp(report.CreatedAt)}");
        await _client.SendLineAsync($"Reporter: {FormatBugReporter(report)}");
        await _client.SendLineAsync($"Status: {FormatBugStatus(report)}");
        await _client.SendLineAsync($"Brief: {report.BriefDescription}");

        if (!string.IsNullOrWhiteSpace(report.ResolutionNote))
        {
            await _client.SendLineAsync("Sysop response:");
            foreach (string line in SplitBugNoteLines(report.ResolutionNote))
                await _client.SendLineAsync($"  {line}");
        }

        if (!string.IsNullOrWhiteSpace(report.LocationText))
            await _client.SendLineAsync($"Location: {report.LocationText}");

        if (!string.IsNullOrWhiteSpace(report.ItemName))
            await _client.SendLineAsync($"Item/Subject: {report.ItemName}");

        await _client.SendLineAsync("Description:");
        foreach (string line in report.Description.Split([Environment.NewLine], StringSplitOptions.None))
            await _client.SendLineAsync(line);
    }

    private async Task HandleCompleteBug(string args)
    {
        if (!_player.IsSysop)
        {
            await _client.SendLineAsync("Only MMUDREBORN sysops can moderate bug reports.");
            return;
        }

        if (!TryParseBugId(args, out int bugId))
        {
            await _client.SendLineAsync("Syntax: COMPLETEBUG <number>");
            return;
        }

        var report = await LoadBugReportOrSendNotFoundAsync(bugId);
        if (report == null)
            return;

        if (report.Status == BugReportStatus.Complete)
        {
            await _client.SendLineAsync($"Bug #{bugId} is already complete.");
            return;
        }

        if (report.Status == BugReportStatus.Verified)
        {
            await _client.SendLineAsync($"Bug #{bugId} is already verified.");
            return;
        }

        string moderatorName = string.IsNullOrWhiteSpace(_client.CurrentBbsUserName)
            ? _player.Name
            : _client.CurrentBbsUserName;

        if (!_world.PlayerRepo.CompleteBugReport(bugId, moderatorName))
        {
            await _client.SendLineAsync($"Bug #{bugId} could not be marked complete right now.");
            return;
        }

        await _client.SendLineAsync($"Bug #{bugId} marked complete.");
    }

    private async Task HandleNotABug(string args)
    {
        if (!_player.IsSysop)
        {
            await _client.SendLineAsync("Only MMUDREBORN sysops can moderate bug reports.");
            return;
        }

        if (!TryParseBugIdAndReason(args, out int bugId, out string reason))
        {
            await _client.SendLineAsync("Syntax: NOTABUG <number> <reason>");
            await _client.SendLineAsync("The reason is required — the reporter is shown it at login.");
            return;
        }

        if (!TryValidateBugFieldLength(reason, MaxBugResolutionNoteLength, out string reasonError))
        {
            await _client.SendLineAsync(reasonError);
            return;
        }

        var report = await LoadBugReportOrSendNotFoundAsync(bugId);
        if (report == null)
            return;

        if (report.Status == BugReportStatus.NotABug)
        {
            await _client.SendLineAsync($"Bug #{bugId} is already closed as not a bug.");
            return;
        }

        string moderatorName = string.IsNullOrWhiteSpace(_client.CurrentBbsUserName)
            ? _player.Name
            : _client.CurrentBbsUserName;

        if (!_world.PlayerRepo.MarkBugNotABug(bugId, moderatorName, reason))
        {
            await _client.SendLineAsync($"Bug #{bugId} could not be closed as not a bug right now.");
            return;
        }

        await _client.SendLineAsync($"Bug #{bugId} closed as not a bug. The reporter will see your reason at login.");
    }

    private async Task HandleVerifyBug(string args)
    {
        if (!TryParseBugId(args, out int bugId))
        {
            await _client.SendLineAsync("Syntax: VERIFYBUG <number>");
            return;
        }

        var report = await LoadBugReportOrSendNotFoundAsync(bugId);
        if (report == null)
            return;

        if (report.Status != BugReportStatus.Complete)
        {
            await _client.SendLineAsync($"Bug #{bugId} must be marked complete before you can verify it.");
            return;
        }

        string verifierBbsUserName = GetCurrentBugReporterBbsUserName();
        string verifierPlayerName = GetCurrentBugReporterPlayerName();

        if (!_world.PlayerRepo.VerifyBugReport(bugId, verifierBbsUserName, verifierPlayerName))
        {
            await _client.SendLineAsync($"Bug #{bugId} could not be marked verified right now.");
            return;
        }

        await _client.SendLineAsync($"Bug #{bugId} marked verified.");
    }

    private async Task HandleStillBug(string args)
    {
        if (!TryParseBugId(args, out int bugId))
        {
            await _client.SendLineAsync("Syntax: STILLBUG <number>");
            return;
        }

        var report = await LoadBugReportOrSendNotFoundAsync(bugId);
        if (report == null)
            return;

        if (!IsCurrentPlayerBugReporter(report))
        {
            await _client.SendLineAsync("Only the reporting player may mark that bug report still present.");
            return;
        }

        if (report.Status != BugReportStatus.Complete)
        {
            await _client.SendLineAsync($"Bug #{bugId} must be marked complete before you can mark it still present.");
            return;
        }

        if (!_world.PlayerRepo.MarkBugStillPresent(bugId))
        {
            await _client.SendLineAsync($"Bug #{bugId} could not be marked still present right now.");
            return;
        }

        await _client.SendLineAsync($"Bug #{bugId} marked still present.");
    }

    private async Task HandleDeleteBug(string args)
    {
        if (!_player.IsSysop)
        {
            await _client.SendLineAsync("Only MMUDREBORN sysops can moderate bug reports.");
            return;
        }

        if (!TryParseBugId(args, out int bugId))
        {
            await _client.SendLineAsync("Syntax: DELETEBUG <number>");
            return;
        }

        if (!_world.PlayerRepo.DeleteBugReport(bugId))
        {
            await _client.SendLineAsync("That bug report was not found.");
            return;
        }

        await _client.SendLineAsync($"Bug #{bugId} removed.");
    }

    private async Task RenderBugHelpAsync()
    {
        await _client.SendLineAsync();
        await _client.SendLineAsync("Bug Command Help");
        await _client.SendLineAsync("================");
        await _client.SendLineAsync("BUG              Start the guided game bug report wizard");
        await _client.SendLineAsync("BUG <title>      Start the wizard with a title already filled in");
        await _client.SendLineAsync("BUGS             List recent game bug reports as '# Title - Brief'");
        await _client.SendLineAsync("SHOWBUG #        Show the full stored game bug report");
        await _client.SendLineAsync("VERIFYBUG #      Mark a completed game bug report verified (anyone can verify)");
        await _client.SendLineAsync("STILLBUG #       Mark your completed game bug report still present");
        if (_player.IsSysop)
        {
            await _client.SendLineAsync("COMPLETEBUG #    Mark a game bug report complete (MMUDREBORN sysops)");
            await _client.SendLineAsync("NOTABUG # <why>  Close a report as working-as-intended, with a reason (MMUDREBORN sysops)");
            await _client.SendLineAsync("DELETEBUG #      Remove a game bug report (MMUDREBORN sysops)");
        }
        await _client.SendLineAsync("Type 'cancel' during the wizard to abort.");
        await _client.SendLineAsync("Use ;TICKET for BBS issues from anywhere on the board.\n");
    }

    private async Task StartBugReportAsync(string initialTitle)
    {
        var draft = new GameBugReportDraft
        {
            ReporterBbsUserName = GetCurrentBugReporterBbsUserName(),
            ReporterPlayerName = _player.Name,
            DefaultLocationText = ResolveCurrentBugLocationText(),
            Step = GameBugReportDraftStep.AwaitingTitle,
        };

        if (!string.IsNullOrWhiteSpace(initialTitle))
        {
            if (!TryValidateBugFieldLength(initialTitle, MaxBugTitleLength, out string titleError))
            {
                await _client.SendLineAsync(titleError);
                return;
            }

            draft.Title = initialTitle.Trim();
            draft.Step = GameBugReportDraftStep.AwaitingBriefDescription;
        }

        _pendingBugReportDraft = draft;

        await _client.SendLineAsync();
        await _client.SendLineAsync("Bug report started. Type 'cancel' at any prompt to abort.");

        if (draft.Step == GameBugReportDraftStep.AwaitingBriefDescription)
        {
            await _client.SendLineAsync($"Title: {draft.Title}");
            await PromptForBugBriefAsync();
            return;
        }

        await PromptForBugTitleAsync();
    }

    private async Task HandlePendingBugReportAsync(string input)
    {
        if (_pendingBugReportDraft == null)
            return;

        string normalized = (input ?? string.Empty).Trim();
        var draft = _pendingBugReportDraft;

        if (normalized.Equals("cancel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Equals("bug cancel", StringComparison.OrdinalIgnoreCase))
        {
            _pendingBugReportDraft = null;
            await _client.SendLineAsync("Bug report canceled.");
            return;
        }

        switch (draft.Step)
        {
            case GameBugReportDraftStep.AwaitingTitle:
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    await _client.SendLineAsync("A short title is required.");
                    await PromptForBugTitleAsync();
                    return;
                }

                if (!TryValidateBugFieldLength(normalized, MaxBugTitleLength, out string titleError))
                {
                    await _client.SendLineAsync(titleError);
                    await PromptForBugTitleAsync();
                    return;
                }

                draft.Title = normalized;
                draft.Step = GameBugReportDraftStep.AwaitingBriefDescription;
                await PromptForBugBriefAsync();
                return;

            case GameBugReportDraftStep.AwaitingBriefDescription:
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    await _client.SendLineAsync("A one-line summary is required.");
                    await PromptForBugBriefAsync();
                    return;
                }

                if (!TryValidateBugFieldLength(normalized, MaxBugBriefLength, out string briefError))
                {
                    await _client.SendLineAsync(briefError);
                    await PromptForBugBriefAsync();
                    return;
                }

                draft.BriefDescription = normalized;
                draft.Step = GameBugReportDraftStep.AwaitingLocation;
                await PromptForBugLocationAsync(draft);
                return;

            case GameBugReportDraftStep.AwaitingLocation:
                if (string.IsNullOrWhiteSpace(normalized))
                {
                    draft.LocationText = draft.DefaultLocationText;
                }
                else if (normalized.Equals("-", StringComparison.Ordinal))
                {
                    draft.LocationText = string.Empty;
                }
                else
                {
                    if (!TryValidateBugFieldLength(normalized, MaxBugLocationLength, out string locationError))
                    {
                        await _client.SendLineAsync(locationError);
                        await PromptForBugLocationAsync(draft);
                        return;
                    }

                    draft.LocationText = normalized;
                }

                draft.Step = GameBugReportDraftStep.AwaitingItemName;
                await PromptForBugItemAsync();
                return;

            case GameBugReportDraftStep.AwaitingItemName:
                if (string.IsNullOrWhiteSpace(normalized) || normalized.Equals("-", StringComparison.Ordinal))
                {
                    draft.ItemName = string.Empty;
                }
                else
                {
                    if (!TryValidateBugFieldLength(normalized, MaxBugItemLength, out string itemError))
                    {
                        await _client.SendLineAsync(itemError);
                        await PromptForBugItemAsync();
                        return;
                    }

                    draft.ItemName = normalized;
                }

                draft.Step = GameBugReportDraftStep.AwaitingDescription;
                await PromptForBugDescriptionAsync();
                return;

            case GameBugReportDraftStep.AwaitingDescription:
                if (normalized.Equals(".", StringComparison.Ordinal))
                {
                    if (draft.DescriptionLines.Count == 0)
                    {
                        await _client.SendLineAsync("Enter at least one line of detail before finishing with '.'.");
                        return;
                    }

                    var report = new BugReportRecord
                    {
                        ReporterBbsUserName = draft.ReporterBbsUserName,
                        ReporterPlayerName = draft.ReporterPlayerName,
                        Title = draft.Title,
                        BriefDescription = draft.BriefDescription,
                        Description = string.Join(Environment.NewLine, draft.DescriptionLines),
                        LocationText = draft.LocationText,
                        ItemName = draft.ItemName,
                    };

                    int bugId = _world.PlayerRepo.CreateBugReport(report);
                    _pendingBugReportDraft = null;

                    await _client.SendLineAsync($"Bug #{bugId} submitted. Use SHOWBUG {bugId} to review it.");
                    return;
                }

                if (string.IsNullOrWhiteSpace(normalized))
                {
                    await _client.SendLineAsync("Enter a description line, or type '.' to submit.");
                    return;
                }

                draft.DescriptionLines.Add(normalized);
                await _client.SendLineAsync("Added. Continue typing details, or '.' to submit.");
                return;
        }
    }

    private Task PromptForBugTitleAsync()
    {
        return _client.SendLineAsync($"Title (required, up to {MaxBugTitleLength} characters):");
    }

    private Task PromptForBugBriefAsync()
    {
        return _client.SendLineAsync($"Brief summary (required, one line, up to {MaxBugBriefLength} characters):");
    }

    private Task PromptForBugLocationAsync(GameBugReportDraft draft)
    {
        return string.IsNullOrWhiteSpace(draft.DefaultLocationText)
            ? _client.SendLineAsync($"Room or location (optional, Enter or '-' to skip, up to {MaxBugLocationLength} characters):")
            : _client.SendLineAsync($"Room or location (Enter to use '{draft.DefaultLocationText}', '-' to skip, up to {MaxBugLocationLength} characters):");
    }

    private Task PromptForBugItemAsync()
    {
        return _client.SendLineAsync($"Item, monster, or subject (optional, Enter or '-' to skip, up to {MaxBugItemLength} characters):");
    }

    private async Task PromptForBugDescriptionAsync()
    {
        await _client.SendLineAsync("Enter the full description, one line at a time.");
        await _client.SendLineAsync("Include what happened, what you expected, and any steps to reproduce it.");
        await _client.SendLineAsync("Type a single '.' on its own line when you are finished.");
    }

    private string ResolveCurrentBugLocationText()
    {
        var room = _world.GetRoom(_player.CurrentMapNumber, _player.CurrentRoomNumber);
        string roomName = room?.Name?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(roomName)
            ? $"{_player.CurrentMapNumber}/{_player.CurrentRoomNumber}"
            : $"{roomName} ({_player.CurrentMapNumber}/{_player.CurrentRoomNumber})";
    }

    private static bool TryValidateBugFieldLength(string value, int maxLength, out string errorMessage)
    {
        if (value.Trim().Length <= maxLength)
        {
            errorMessage = string.Empty;
            return true;
        }

        errorMessage = $"Please keep that under {maxLength} characters.";
        return false;
    }

    private static bool TryParseBugId(string args, out int bugId)
    {
        return int.TryParse(args, out bugId) && bugId > 0;
    }

    // "NOTABUG 220 working as intended, see stock" → (220, "working as intended, see stock").
    // Both halves are mandatory: a number with no reason fails, which is what forces the sysop to
    // actually write the feedback instead of silently dismissing the report.
    private static bool TryParseBugIdAndReason(string args, out int bugId, out string reason)
    {
        bugId = 0;
        reason = string.Empty;

        string trimmed = (args ?? string.Empty).Trim();
        int split = trimmed.IndexOf(' ');
        if (split <= 0)
            return false;

        if (!TryParseBugId(trimmed[..split], out bugId))
            return false;

        reason = trimmed[(split + 1)..].Trim();
        return reason.Length > 0;
    }

    private static string FormatBugTimestamp(string timestamp)
    {
        if (DateTime.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
            return parsed.ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture);

        return timestamp;
    }

    private static string FormatBugReporter(BugReportRecord report)
    {
        if (!string.IsNullOrWhiteSpace(report.ReporterPlayerName) &&
            !string.IsNullOrWhiteSpace(report.ReporterBbsUserName) &&
            !string.Equals(report.ReporterPlayerName, report.ReporterBbsUserName, StringComparison.OrdinalIgnoreCase))
        {
            return $"{report.ReporterPlayerName} ({report.ReporterBbsUserName})";
        }

        if (!string.IsNullOrWhiteSpace(report.ReporterPlayerName))
            return report.ReporterPlayerName;

        return string.IsNullOrWhiteSpace(report.ReporterBbsUserName) ? "Unknown" : report.ReporterBbsUserName;
    }

    private static string FormatBugStatus(BugReportRecord report)
    {
        return report.Status switch
        {
            BugReportStatus.Complete => FormatCompletedBugStatus(report),
            BugReportStatus.Verified => FormatVerifiedBugStatus(report),
            BugReportStatus.StillPresent => FormatReporterReviewedStatus(report, "Still Present per"),
            BugReportStatus.NotABug => FormatNotABugStatus(report),
            _ => "Open",
        };
    }

    private static string FormatNotABugStatus(BugReportRecord report)
    {
        string closedBy = string.IsNullOrWhiteSpace(report.CompletedByBbsUserName) ? "Unknown" : report.CompletedByBbsUserName;
        if (string.IsNullOrWhiteSpace(report.CompletedAt))
            return $"Not a Bug per {closedBy}";

        return $"Not a Bug per {closedBy} on {FormatBugTimestamp(report.CompletedAt)}";
    }

    private static string FormatBugListTag(BugReportStatus status)
    {
        return status switch
        {
            BugReportStatus.Complete => $"{MudAnsi.BrightGreen}[Complete]{MudAnsi.Reset} ",
            BugReportStatus.Verified => $"{MudAnsi.BrightCyan}[Verified]{MudAnsi.Reset} ",
            BugReportStatus.StillPresent => $"{MudAnsi.BrightYellow}[Still Present]{MudAnsi.Reset} ",
            BugReportStatus.NotABug => $"{MudAnsi.BrightBlack}[Not a Bug]{MudAnsi.Reset} ",
            _ => string.Empty,
        };
    }

    private static string FormatCompletedBugStatus(BugReportRecord report)
    {
        string completedBy = string.IsNullOrWhiteSpace(report.CompletedByBbsUserName) ? "Unknown" : report.CompletedByBbsUserName;
        if (string.IsNullOrWhiteSpace(report.CompletedAt))
            return $"Complete by {completedBy}";

        return $"Complete by {completedBy} on {FormatBugTimestamp(report.CompletedAt)}";
    }

    // Legacy fallback: rows verified before the VerifiedBy columns existed have no
    // VerifiedByPlayerName/BbsUserName populated. Pre-change semantics were reporter-only, so
    // displaying the reporter for those rows is accurate. New rows use the explicit fields.
    private static string FormatVerifiedBugStatus(BugReportRecord report)
    {
        string verifier;
        if (!string.IsNullOrWhiteSpace(report.VerifiedByPlayerName) &&
            !string.IsNullOrWhiteSpace(report.VerifiedByBbsUserName) &&
            !string.Equals(report.VerifiedByPlayerName, report.VerifiedByBbsUserName, StringComparison.OrdinalIgnoreCase))
        {
            verifier = $"{report.VerifiedByPlayerName} ({report.VerifiedByBbsUserName})";
        }
        else if (!string.IsNullOrWhiteSpace(report.VerifiedByPlayerName))
        {
            verifier = report.VerifiedByPlayerName;
        }
        else if (!string.IsNullOrWhiteSpace(report.VerifiedByBbsUserName))
        {
            verifier = report.VerifiedByBbsUserName;
        }
        else
        {
            verifier = FormatBugReporter(report);
        }

        string timestamp = !string.IsNullOrWhiteSpace(report.VerifiedAt)
            ? report.VerifiedAt
            : report.ReporterReviewedAt;

        if (string.IsNullOrWhiteSpace(timestamp))
            return $"Verified by {verifier}";

        return $"Verified by {verifier} on {FormatBugTimestamp(timestamp)}";
    }

    private static string FormatReporterReviewedStatus(BugReportRecord report, string prefix)
    {
        string reporter = FormatBugReporter(report);
        if (string.IsNullOrWhiteSpace(report.ReporterReviewedAt))
            return $"{prefix} {reporter}";

        return $"{prefix} {reporter} on {FormatBugTimestamp(report.ReporterReviewedAt)}";
    }

    private bool IsCurrentPlayerBugReporter(BugReportRecord report)
    {
        string currentBbsUserName = GetCurrentBugReporterBbsUserName();
        string currentPlayerName = GetCurrentBugReporterPlayerName();

        return (!string.IsNullOrWhiteSpace(report.ReporterBbsUserName) &&
                string.Equals(report.ReporterBbsUserName, currentBbsUserName, StringComparison.OrdinalIgnoreCase)) ||
               (!string.IsNullOrWhiteSpace(report.ReporterPlayerName) &&
                string.Equals(report.ReporterPlayerName, currentPlayerName, StringComparison.OrdinalIgnoreCase));
    }

    private string GetCurrentBugReporterBbsUserName()
    {
        string currentBbsUserName = string.IsNullOrWhiteSpace(_client.CurrentBbsUserName)
            ? _player.BbsUserId
            : _client.CurrentBbsUserName;
        return Player.NormalizeNamePart(currentBbsUserName ?? string.Empty);
    }

    private string GetCurrentBugReporterPlayerName()
    {
        return Player.NormalizeNamePart(_player.Name ?? string.Empty);
    }

    private enum GameBugReportDraftStep
    {
        AwaitingTitle,
        AwaitingBriefDescription,
        AwaitingLocation,
        AwaitingItemName,
        AwaitingDescription,
    }

    private sealed class GameBugReportDraft
    {
        public string ReporterBbsUserName { get; set; } = string.Empty;
        public string ReporterPlayerName { get; set; } = string.Empty;
        public string DefaultLocationText { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string BriefDescription { get; set; } = string.Empty;
        public string LocationText { get; set; } = string.Empty;
        public string ItemName { get; set; } = string.Empty;
        public List<string> DescriptionLines { get; } = [];
        public GameBugReportDraftStep Step { get; set; }
    }
}
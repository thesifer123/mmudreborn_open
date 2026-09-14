namespace mmudreborn.Data.Models;

public sealed class PendingRerollRecord
{
    public string BbsUserName { get; set; } = "";
    // The rerolled character's previous given name, used to pre-fill (still editable) the new
    // character's name on the create screen.
    public string PreservedName { get; set; } = "";
    public long KeptExperience { get; set; }
    public bool PreservedIsSysop { get; set; }
    public bool PreservedToptenDisabled { get; set; }
    public string PreservedSuicideRerollPassword { get; set; } = "";
    public string CreatedAt { get; set; } = "";
}

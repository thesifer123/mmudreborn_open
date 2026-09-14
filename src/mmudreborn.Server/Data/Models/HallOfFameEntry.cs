namespace mmudreborn.Data.Models;

// A dead-player record written on permadeath (the stock permanent-info save, run from
// the death path when lives < 1). Stock keeps a fixed-size binary archive of fallen characters;
// we persist the same shape to the public HallOfFame table and expose it via the `hall` command.
public sealed class HallOfFameEntry
{
    public int Id { get; set; }
    public string PlayerName { get; set; } = string.Empty;
    public string LastName { get; set; } = string.Empty;
    public int Level { get; set; }
    public int RaceId { get; set; }
    public int ClassId { get; set; }
    public long Experience { get; set; }
    public int Alignment { get; set; }       // EvilPoints at death (positive = evil)
    public string KillerName { get; set; } = string.Empty;
    public string CreatedAt { get; set; } = string.Empty;
}

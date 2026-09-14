namespace mmudreborn.Data.Models;

/// <summary>Persisted ownership of one of the ten gang houses (HouseId 1..10). A record exists
/// only while the house is owned; eviction/disband deletes it (returning the deed to the pool).</summary>
public sealed class GangHouseRecord
{
    public int HouseId { get; set; }
    public string OwnerGang { get; set; } = "";
    public string OwnerPlayer { get; set; } = "";
    /// <summary>UTC "o" timestamp the deed was purchased.</summary>
    public string PurchasedAt { get; set; } = "";
    /// <summary>UTC "o" timestamp tax was last successfully paid (also set on purchase).</summary>
    public string LastTaxAt { get; set; } = "";
}

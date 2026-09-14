namespace mmudreborn.Data.Models;

/// <summary>Persisted state of one gang-owned shop (shop type 11). Owner-managed stock plus the
/// shop markup stock keeps in the shop record, alongside the per-slot
/// item/qty/price/currency arrays. One record exists per shop that has ever been stocked or had its markup
/// changed; the row is deleted when the shop is emptied (e.g. on eviction). The ten slots are stored
/// as a compact <see cref="Slots"/> string ("itemId:qty:price:currency" entries joined by ';') to
/// mirror the stock "save the whole shop" behaviour with a single upsert.</summary>
public sealed class GangShopRecord
{
    public int ShopId { get; set; }
    /// <summary>Shop markup percent the owner set via MARKUP (clamped 0..1000).</summary>
    public int MarkupPercent { get; set; }
    /// <summary>Serialized stock slots: "itemId:qty:price:currency" entries joined by ';'.</summary>
    public string Slots { get; set; } = "";
}

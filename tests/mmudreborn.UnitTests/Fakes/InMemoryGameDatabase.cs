using mmudreborn.Data;
using mmudreborn.Data.Models;

namespace mmudreborn.UnitTests.Fakes;

public sealed class InMemoryGameDatabase : IGameDatabase
{
    public Dictionary<int, Race> Races { get; } = [];
    public Dictionary<int, CharacterClass> Classes { get; } = [];
    public Dictionary<(int Map, int Room), Room> Rooms { get; } = [];
    public Dictionary<(int Map, int Room), string> RoomDescriptions { get; } = [];
    public Dictionary<int, Monster> Monsters { get; } = [];
    public Dictionary<int, Item> Items { get; } = [];
    public Dictionary<int, GameSpell> Spells { get; } = [];
    public Dictionary<int, Shop> Shops { get; } = [];
    public Dictionary<int, RoomMessage> Messages { get; } = [];
    public Dictionary<string, SocialAction> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<SocialAction> OrderedActions { get; } = [];
    public Dictionary<int, string> TextBlocks { get; } = [];
    public Dictionary<int, int> TextBlockLinks { get; } = [];
    public Dictionary<string, string> HelpTopics { get; } = new(StringComparer.OrdinalIgnoreCase);

    public void ReloadActions()
    {
    }

    public void SaveAction(SocialAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        string normalizedName = string.IsNullOrWhiteSpace(action.Name)
            ? string.Empty
            : action.Name.Trim().ToLowerInvariant();
        if (normalizedName.Length == 0)
            throw new ArgumentException("Action name is required.", nameof(action));

        var copy = new SocialAction
        {
            Name = normalizedName,
            DisplayOrder = action.DisplayOrder ?? OrderedActions.Where(candidate => candidate.DisplayOrder.HasValue)
                .Select(candidate => candidate.DisplayOrder!.Value)
                .DefaultIfEmpty(0)
                .Max() + 1,
            SingleToUser = action.SingleToUser,
            SingleToRoom = action.SingleToRoom,
            UserToUser = action.UserToUser,
            UserToOtherUser = action.UserToOtherUser,
            UserToRoom = action.UserToRoom,
            MonsterToUser = action.MonsterToUser,
            MonsterToRoom = action.MonsterToRoom,
            InventoryToUser = action.InventoryToUser,
            InventoryToRoom = action.InventoryToRoom,
            FloorItemToUser = action.FloorItemToUser,
            FloorItemToRoom = action.FloorItemToRoom,
        };

        OrderedActions.RemoveAll(candidate => candidate.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase));
        OrderedActions.Add(copy);
        OrderedActions.Sort(static (left, right) =>
        {
            int leftOrder = left.DisplayOrder ?? int.MaxValue;
            int rightOrder = right.DisplayOrder ?? int.MaxValue;
            int orderComparison = leftOrder.CompareTo(rightOrder);
            return orderComparison != 0
                ? orderComparison
                : string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        });

        Actions[normalizedName] = copy;
    }

    public bool DeleteAction(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            return false;

        OrderedActions.RemoveAll(candidate => candidate.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase));
        return Actions.Remove(name.Trim());
    }
}

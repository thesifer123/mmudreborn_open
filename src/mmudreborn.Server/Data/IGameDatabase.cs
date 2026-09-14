using mmudreborn.Data.Models;

namespace mmudreborn.Data;

public interface IGameDatabase
{
    Dictionary<int, Race> Races { get; }
    Dictionary<int, CharacterClass> Classes { get; }
    Dictionary<(int Map, int Room), Room> Rooms { get; }
    Dictionary<(int Map, int Room), string> RoomDescriptions { get; }
    Dictionary<int, Monster> Monsters { get; }
    Dictionary<int, Item> Items { get; }
    Dictionary<int, GameSpell> Spells { get; }
    Dictionary<int, Shop> Shops { get; }
    Dictionary<int, RoomMessage> Messages { get; }
    Dictionary<string, SocialAction> Actions { get; }
    List<SocialAction> OrderedActions { get; }
    Dictionary<int, string> TextBlocks { get; }
    Dictionary<int, int> TextBlockLinks { get; }
    Dictionary<string, string> HelpTopics { get; }

    void ReloadActions();
    void SaveAction(SocialAction action);
    bool DeleteAction(string name);
}

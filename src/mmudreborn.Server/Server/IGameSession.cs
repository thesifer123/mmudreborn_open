namespace mmudreborn.Server;

public interface IGameSession
{
    Task HandleExternalPlayerDeathAsync();

    // Re-render the player's current room (used after a forced relocation, e.g. a fear flee).
    Task ShowCurrentRoomAsync();
}
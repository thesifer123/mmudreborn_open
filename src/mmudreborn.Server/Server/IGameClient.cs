using CWGaming.Shared;

namespace mmudreborn.Server;

/// <summary>
/// The mmudreborn door's view of a connection: the generic <see cref="IBbsConnection"/> the host owns,
/// plus the game-specific <see cref="Player"/>/<see cref="Session"/> back-references and the MUD prompt.
/// Implemented by <see cref="GameClient"/>, a thin wrapper around the host connection. Game code keeps
/// taking <c>IGameClient</c>; only the host transport implements the generic base.
/// </summary>
public interface IGameClient : IBbsConnection
{
    Game.Player? Player { get; set; }
    IGameSession? Session { get; set; }

    /// <summary>
    /// The current game prompt for this session, rendered from the attached <see cref="Player"/>.
    /// The host re-emits this opaquely (via <see cref="IBbsConnection.PromptProvider"/>) after any
    /// out-of-band line; a session with no active player yields an empty prompt.
    /// </summary>
    string CurrentPrompt => Player is null ? string.Empty : MudAnsi.Prompt(Player);
}

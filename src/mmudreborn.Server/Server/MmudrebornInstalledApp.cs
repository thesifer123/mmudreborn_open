using CWGaming.Shared;
using mmudreborn.Game;

namespace mmudreborn.Server;

public sealed class MmudrebornInstalledApp : IBbsInstalledApp
{
    private readonly GameWorld _world;

    public MmudrebornInstalledApp(GameWorld world)
    {
        _world = world;
    }

    public async Task<BbsInstalledAppResult> RunAsync(IBbsConnection connection, BbsUserAccount account, BbsInstalledAppLaunchMode launchMode, CancellationToken ct)
    {
        // The host hands us the generic connection; the door wraps it once in its IGameClient view
        // (attaching Player/Session + the MUD prompt) for the lifetime of this door session.
        var client = new GameClient(connection, _world);
        var currentLaunchMode = launchMode;

        while (!ct.IsCancellationRequested && client.Connected)
        {
            Player? player;
            if (currentLaunchMode == BbsInstalledAppLaunchMode.EnterRealmDirectly)
            {
                player = await EnsurePlayerForRealmAsync(client, account, ct);
                if (player == null)
                    return BbsInstalledAppResult.Disconnected;
            }
            else
            {
                var mudMenuSession = new MudMainMenuSession(client, _world);
                var mudChoice = await mudMenuSession.RunAsync(
                    currentLaunchMode == BbsInstalledAppLaunchMode.AutoAdvanceMainMenu,
                    account.UserName,
                    ct);

                switch (mudChoice)
                {
                    case MudMainMenuResult.ReturnToBbs:
                        return BbsInstalledAppResult.ReturnedToBbs;
                    case MudMainMenuResult.Disconnect:
                        return BbsInstalledAppResult.Disconnected;
                }

                player = await EnsurePlayerForRealmAsync(client, account, ct);
                if (player == null)
                    return BbsInstalledAppResult.Disconnected;
            }

            await client.DiscardBufferedLineEndingsAsync(ct);
            await connection.ReassertGameplayInputModeAsync();
            await client.SendAsync("\r");

            var gameSession = new GameSession(client, _world);
            client.Session = gameSession;
            bool returningToMenu = await gameSession.RunForPlayerAsync(player, ct);

            if (!returningToMenu || !client.Connected)
                return BbsInstalledAppResult.Disconnected;

            if (launchMode == BbsInstalledAppLaunchMode.EnterRealmDirectly)
            {
                currentLaunchMode = BbsInstalledAppLaunchMode.EnterRealmDirectly;
                continue;
            }

            if (gameSession.ReturnToMudMenu)
            {
                currentLaunchMode = BbsInstalledAppLaunchMode.ShowMainMenu;
                continue;
            }

            return BbsInstalledAppResult.ReturnedToBbs;
        }

        return BbsInstalledAppResult.Disconnected;
    }

    private async Task<Player?> EnsurePlayerForRealmAsync(IGameClient client, BbsUserAccount account, CancellationToken ct)
    {
        // Resolve the account's character by the authoritative account->character link: the BBS user id
        // stored ON the player row (Player.BbsUserId). The BBS no longer stores a player name, so a second
        // door/realm simply keys its own player store by the same BBS user id — no BBS-side field to add.
        var existing = _world.PlayerRepo.LoadPlayerByBbsUserId(account.UserName);
        if (existing != null)
            return existing;

        var pendingReroll = _world.PlayerRepo.LoadPendingReroll(account.UserName);
        if (pendingReroll != null)
        {
            await client.SendLineAsync();
            await client.SendLineAsync($"{MudAnsi.BrightYellow}You have a pending reroll. Please create your new character.{MudAnsi.Reset}");
            if (pendingReroll.KeptExperience > 0)
                await client.SendLineAsync($"{MudAnsi.White}You will start with {pendingReroll.KeptExperience:N0} experience points.{MudAnsi.Reset}");
            await client.SendLineAsync();
        }

        var creation = new CharacterCreation(client, _world);
        // CreateCharacterAsync stamps the new player's BbsUserId from account.UserName, establishing the
        // link — no BBS write-back needed.
        // On a reroll, pre-fill the new character's name with the old one (still editable); otherwise the
        // create screen seeds it from the BBS account name.
        var player = await creation.CreateCharacterAsync(account, rerollPreviousName: pendingReroll?.PreservedName, ct);
        if (player == null)
            return null;

        if (pendingReroll != null)
        {
            player.Experience = pendingReroll.KeptExperience;
            if (pendingReroll.PreservedIsSysop)
                player.IsSysop = true;
            player.IsToptenDisabled = pendingReroll.PreservedToptenDisabled;
            player.SuicideRerollPassword = pendingReroll.PreservedSuicideRerollPassword;
            _world.PlayerRepo.SavePlayer(player);
            _world.PlayerRepo.DeletePendingReroll(account.UserName);
        }

        return player;
    }
}

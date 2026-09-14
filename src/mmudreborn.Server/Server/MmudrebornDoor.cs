using CWGaming.Shared;
using mmudreborn.Data;
using Npgsql;

namespace mmudreborn.Server;

/// <summary>
/// The mmudreborn door's entry point. The BBS host discovers this by reflection (it implements the
/// Shared <see cref="IBbsDoorFactory"/>) and calls <see cref="Create"/>, handing in the BBS services the
/// world needs. The factory owns the door's bootstrap — game database load, player repository, legacy
/// account migration, world construction — so none of it lives in the host any more.
/// </summary>
public sealed class MmudrebornDoorFactory : IBbsDoorFactory
{
    public IBbsDoor Create(BbsDoorServices services)
    {
        ArgumentNullException.ThrowIfNull(services);

        string gameConnectionString = RuntimeConfiguration.ResolveGamePostgresConnectionString();
        Console.WriteLine($"[mmudreborn door] Game PostgreSQL: {FormatConnectionString(gameConnectionString)}");

        var bootstrapper = new PostgresBootstrapper(gameConnectionString);
        bootstrapper.EnsureReady();

        string? helpTopicsPath = MmudrebornWorkspaceFileResolver.ResolveOptionalFile("help_topics.json");
        var database = new GameDatabase(gameConnectionString, helpTopicsPath);
        database.LoadAll();

        var playerRepo = new PlayerRepository(gameConnectionString);

        // The account→character link lives authoritatively on the player row (Players.BbsUserId), stamped
        // at character creation, and BBS accounts live in the host's own BBS database. The historical
        // legacy game-DB `bbs.users` mirror has been fully migrated out and dropped, so there is no
        // BBS-account seeding or link maintenance to run here.
        var world = new GameWorld(database, playerRepo, services.BbsUserRepository, services.CommandDispatcher);

        // In-process game-data reload for `sys reloaddata` (no disconnects): build a fresh, fully loaded
        // database on demand.
        world.GameDataReloader = () =>
        {
            var reloaded = new GameDatabase(gameConnectionString, helpTopicsPath);
            reloaded.LoadAll();
            return reloaded;
        };

        // Production runs player persistence write-behind + proactive health alerting (off in tests).
        world.PlayerWriteBehindEnabled = true;
        world.HealthAlertsEnabled = true;
        // Run the spawn-bubble BFS off the world gate (a background worker) so a move command's gate hold
        // no longer absorbs it. Off in tests, which rely on synchronous, deterministic bubble updates.
        world.BackgroundSpawnBubbleEnabled = true;
        // Web→game realm bus: LISTEN on the shared realm DB so the web explorer can push gossip/auction
        // into the live realm (no polling). Unset in tests => no listener.
        world.RealmBusConnectionString = gameConnectionString;

        var installedApp = new MmudrebornInstalledApp(world);
        return new MmudrebornDoor(world, installedApp);
    }

    private static string FormatConnectionString(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        if (!string.IsNullOrEmpty(builder.Password))
            builder.Password = "******";

        return builder.ToString();
    }
}

/// <summary>The live mmudreborn door wrapping its <see cref="GameWorld"/>; see <see cref="IBbsDoor"/>.</summary>
public sealed class MmudrebornDoor : IBbsDoor
{
    private readonly GameWorld _world;
    private readonly IBbsInstalledApp _installedApp;

    public MmudrebornDoor(GameWorld world, IBbsInstalledApp installedApp)
    {
        _world = world;
        _installedApp = installedApp;
    }

    public string AppId => HostedAppIds.Mmudreborn;
    public IBbsDoorContext Context => _world;
    public IBbsInstalledApp InstalledApp => _installedApp;

    public Action<int, string>? OnShutdownRequested
    {
        get => _world.OnShutdownRequested;
        set => _world.OnShutdownRequested = value;
    }

    public int ShutdownExitCode => GameWorld.ShutdownExitCode;
    public int RestartExitCode => GameWorld.RestartExitCode;

    public void AttachHost(IBbsHost host) => _world.SetHost(host);
    public void Start() => _world.Start();
    public void Stop() => _world.Stop();
}

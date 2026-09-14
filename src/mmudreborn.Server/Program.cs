using System.Text;
using mmudreborn.Data;
using mmudreborn.Server;
using Npgsql;

// Register CP437 encoding provider (for box-drawing chars in telnet)
Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

string postgresConnectionString = RuntimeConfiguration.ResolveGamePostgresConnectionString();

Console.WriteLine($"PostgreSQL: {FormatConnectionString(postgresConnectionString)}");

var bootstrapper = new PostgresBootstrapper(postgresConnectionString);
bootstrapper.EnsureReady();

var playerRepo = new PlayerRepository(postgresConnectionString);
bool forcePasswordSync = HasArgument(args, "sync-users-force-passwords");
bool syncUsers = HasArgument(args, "sync-users") || forcePasswordSync;

if (syncUsers)
{
    string bbsApiBaseUrl = RuntimeConfiguration.ResolveBbsApiBaseUrl();
    Console.WriteLine($"BBS API: {bbsApiBaseUrl}");

    using var bbsUserRepo = new HttpBbsUserRepository(bbsApiBaseUrl, RuntimeConfiguration.ResolveBbsApiKey());
    int syncedUsers = BbsUserSync.SyncFromPlayers(bbsUserRepo, playerRepo, overwriteExistingPasswords: forcePasswordSync);
    Console.WriteLine($"Synced {syncedUsers} users into the BBS service{(forcePasswordSync ? " (passwords overwritten from Players)." : ".")}");
    return;
}

string? helpTopicsPath = MmudrebornWorkspaceFileResolver.ResolveOptionalFile("help_topics.json");
var database = new GameDatabase(postgresConnectionString, helpTopicsPath);
database.LoadAll();

Console.WriteLine("mmudreborn module bootstrap complete.");
Console.WriteLine("Run CWGamingServ to host telnet/BBS transport and door sessions.");

static bool HasArgument(string[] args, string value)
{
    return args.Any(arg => string.Equals(arg, value, StringComparison.OrdinalIgnoreCase));
}

static string FormatConnectionString(string connectionString)
{
    var builder = new NpgsqlConnectionStringBuilder(connectionString);
    if (!string.IsNullOrEmpty(builder.Password))
        builder.Password = "******";

    return builder.ToString();
}

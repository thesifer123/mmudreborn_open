using CWGaming.Shared;
using Xunit;

namespace mmudreborn.UnitTests;

public sealed class BbsIpAccessControlTests
{
    [Fact]
    public void Exact_ip_ban_blocks_only_that_address()
    {
        var control = new BbsIpAccessControl();
        Assert.True(control.TryBan("203.0.113.5", out string display, out _));
        Assert.Equal("203.0.113.5", display);

        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, control.TryBeginConnection("203.0.113.5"));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("203.0.113.6"));
    }

    [Fact]
    public void Cidr_ban_blocks_the_whole_range_and_canonicalizes_host_bits()
    {
        var control = new BbsIpAccessControl();
        // A sysop may type any address in the range; it canonicalizes to the network base.
        Assert.True(control.TryBan("203.0.113.37/24", out string display, out _));
        Assert.Equal("203.0.113.0/24", display);

        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, control.TryBeginConnection("203.0.113.1"));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, control.TryBeginConnection("203.0.113.254"));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("203.0.114.1"));
    }

    [Fact]
    public void Ipv4_mapped_ipv6_client_matches_an_ipv4_ban()
    {
        var control = new BbsIpAccessControl();
        Assert.True(control.TryBan("203.0.113.5", out _, out _));

        // A dual-stack socket may report the client as ::ffff:203.0.113.5.
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, control.TryBeginConnection("::ffff:203.0.113.5"));
    }

    [Fact]
    public void Unban_removes_the_block()
    {
        var control = new BbsIpAccessControl();
        control.TryBan("203.0.113.5", out _, out _);
        Assert.True(control.TryUnban("203.0.113.5"));

        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("203.0.113.5"));
        Assert.False(control.TryUnban("203.0.113.5")); // already gone
    }

    [Fact]
    public void Per_ip_connection_cap_rejects_beyond_the_limit_and_recovers_on_disconnect()
    {
        var control = new BbsIpAccessControl();
        control.SetMaxConnectionsPerIp(2);

        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("198.51.100.7"));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("198.51.100.7"));
        // Third simultaneous connection from the same IP is over the cap.
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.TooManyConnections, control.TryBeginConnection("198.51.100.7"));

        // A different IP is unaffected by another address's count.
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("198.51.100.8"));

        // Freeing a slot lets the next one in.
        control.EndConnection("198.51.100.7");
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("198.51.100.7"));
    }

    [Fact]
    public void Cap_of_zero_means_unlimited()
    {
        var control = new BbsIpAccessControl();
        control.SetMaxConnectionsPerIp(0);

        for (int i = 0; i < 25; i++)
            Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("192.0.2.10"));
    }

    [Fact]
    public void Banned_address_is_not_counted_against_the_cap()
    {
        var control = new BbsIpAccessControl();
        control.SetMaxConnectionsPerIp(1);
        control.TryBan("192.0.2.20", out _, out _);

        // Repeated banned attempts must not leak connection-count slots (no EndConnection is called for them).
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, control.TryBeginConnection("192.0.2.20"));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, control.TryBeginConnection("192.0.2.20"));
        Assert.DoesNotContain(control.GetActiveConnectionCounts(), c => c.Ip == "192.0.2.20");
    }

    [Fact]
    public void Unparseable_or_empty_ip_is_allowed_but_not_counted()
    {
        var control = new BbsIpAccessControl();
        control.SetMaxConnectionsPerIp(1);

        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection(null));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Allowed, control.TryBeginConnection("not-an-ip"));
        Assert.Empty(control.GetActiveConnectionCounts());
    }

    [Fact]
    public void Invalid_ban_entry_is_rejected_with_an_error()
    {
        var control = new BbsIpAccessControl();
        Assert.False(control.TryBan("banana", out _, out string error));
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(control.GetBans());
    }

    [Fact]
    public void Duplicate_ban_is_reported_and_not_added_twice()
    {
        var control = new BbsIpAccessControl();
        Assert.True(control.TryBan("203.0.113.5", out _, out _));
        Assert.False(control.TryBan("203.0.113.5", out _, out string error));
        Assert.Contains("already banned", error, System.StringComparison.OrdinalIgnoreCase);
        Assert.Single(control.GetBans());
    }

    [Fact]
    public void Bans_and_cap_persist_through_the_settings_store()
    {
        var store = new FakeSettingsStore();

        var first = new BbsIpAccessControl(store);
        first.TryBan("203.0.113.0/24", out _, out _);
        first.TryBan("198.51.100.9", out _, out _);
        first.SetMaxConnectionsPerIp(4);

        // A fresh instance over the same store reloads the persisted state.
        var reloaded = new BbsIpAccessControl(store);
        Assert.Equal(4, reloaded.MaxConnectionsPerIp);
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, reloaded.TryBeginConnection("203.0.113.50"));
        Assert.Equal(BbsIpAccessControl.ConnectionDecision.Banned, reloaded.TryBeginConnection("198.51.100.9"));
    }

    /// <summary>Minimal in-memory <see cref="IBbsUserRepository"/> exercising only the settings KV used here.</summary>
    private sealed class FakeSettingsStore : IBbsUserRepository
    {
        private readonly Dictionary<string, string> _settings = new(System.StringComparer.Ordinal);

        public string GetSettingText(string key, string defaultValue) => _settings.TryGetValue(key, out var v) ? v : defaultValue;
        public void SetSettingText(string key, string value) => _settings[key] = value;

        public bool SetPassword(string userName, string newPasswordHash) => false;
        public bool SetDisplayName(string userName, string displayName) => false;
        public bool SetSysopStatus(string userName, bool isSysop) => false;
        public bool UserExists(string userName) => false;
        public BbsUserAccount? LoadUser(string userName) => null;
        public BbsUserAccount? LoadUser(string userName, string password) => null;
        public void SaveUser(BbsUserAccount account) { }
        public IReadOnlyList<BbsUserAccount> GetUsers() => System.Array.Empty<BbsUserAccount>();
        public int SyncUsers(IReadOnlyCollection<BbsUserAccount> users, bool overwriteExistingPasswords = false) => 0;
    }
}

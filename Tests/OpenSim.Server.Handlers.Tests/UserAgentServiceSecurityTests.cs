using OpenMetaverse;
using OpenSim.Data;
using OpenSim.Services.HypergridService;
using OpenSim.Services.Interfaces;
using Xunit;

namespace OpenSim.Server.Handlers.Tests;

public class UserAgentServiceSecurityTests
{
    [Xunit.Theory]
    [Xunit.InlineData("http://grid.example:8002", "http://grid.example:8002/")]
    [Xunit.InlineData("http://grid.example", "http://grid.example:80/")]
    [Xunit.InlineData("https://grid.example", "https://grid.example:443/")]
    public void IsLocalGridURIRecognizesEquivalentGatekeeperUris(string localGridUri, string requestedGridUri)
    {
        Xunit.Assert.True(UserAgentService.IsLocalGridURI(localGridUri, requestedGridUri));
    }

    [Xunit.Fact]
    public void IsLocalGridURIDoesNotTreatDifferentGatekeepersAsLocal()
    {
        Xunit.Assert.False(UserAgentService.IsLocalGridURI("http://grid.example:8002/", "http://other.example:8002/"));
    }

    [Xunit.Fact]
    public void ForeignSessionHomeMatchesStoredForeignHome()
    {
        GridUserInfo existing = new()
        {
            UserID = "00000000-0000-0000-0000-000000000001;http://home.example:8002/;Test User"
        };

        Xunit.Assert.True(GatekeeperService.ForeignSessionHomeMatches(existing, "http://home.example:8002"));
    }

    [Xunit.Fact]
    public void ForeignSessionHomeRejectsDifferentClaimedHome()
    {
        GridUserInfo existing = new()
        {
            UserID = "00000000-0000-0000-0000-000000000001;http://home.example:8002/;Test User"
        };

        Xunit.Assert.False(GatekeeperService.ForeignSessionHomeMatches(existing, "http://attacker.example:8002/"));
    }

    [Xunit.Fact]
    public void FriendshipDeleteMatchesExactForeignFriendAndSecret()
    {
        UUID friendID = new("00000000-0000-0000-0000-000000000001");
        string storedFriend = friendID + ";http://home.example:8002/;Test User;secret123";

        Xunit.Assert.True(HGFriendsService.FriendshipDeleteMatches(storedFriend, friendID, "secret123"));
    }

    [Xunit.Fact]
    public void FriendshipDeleteRejectsEmptySecret()
    {
        UUID friendID = new("00000000-0000-0000-0000-000000000001");
        string storedFriend = friendID + ";http://home.example:8002/;Test User;secret123";

        Xunit.Assert.False(HGFriendsService.FriendshipDeleteMatches(storedFriend, friendID, string.Empty));
    }

    [Xunit.Fact]
    public void FriendshipDeleteRejectsWrongSecretOrFriend()
    {
        UUID friendID = new("00000000-0000-0000-0000-000000000001");
        UUID otherFriendID = new("00000000-0000-0000-0000-000000000002");
        string storedFriend = friendID + ";http://home.example:8002/;Test User;secret123";

        Xunit.Assert.False(HGFriendsService.FriendshipDeleteMatches(storedFriend, friendID, "123"));
        Xunit.Assert.False(HGFriendsService.FriendshipDeleteMatches(storedFriend, otherFriendID, "secret123"));
    }

    [Xunit.Fact]
    public void FriendshipDeleteRejectsBareLocalFriendship()
    {
        UUID friendID = new("00000000-0000-0000-0000-000000000001");

        Xunit.Assert.False(HGFriendsService.FriendshipDeleteMatches(friendID.ToString(), friendID, "secret123"));
    }

    [Xunit.Fact]
    public void HypergridEgressPolicyRejectsPrivateAndLoopbackAddresses()
    {
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("127.0.0.1")));
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("10.0.0.5")));
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("172.16.0.5")));
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("192.168.1.5")));
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("169.254.169.254")));
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("fc00::1")));
    }

    [Xunit.Fact]
    public void HypergridEgressPolicyAllowsPublicAddress()
    {
        Xunit.Assert.True(HypergridEgressPolicy.IsAllowedAddress(System.Net.IPAddress.Parse("8.8.8.8")));
    }

    // The address filter left no way to Hypergrid between grids on a private network (a LAN grid, two grids on
    // one host): [Hypergrid] AllowPrivateGrids names the ones allowed, by gatekeeper.
    [Xunit.Fact]
    public void HypergridEgressPolicyAllowsOnlyThePrivateGridsNamed()
    {
        Nini.Config.IniConfigSource config = new();
        config.AddConfig("Hypergrid").Set("AllowPrivateGrids", "http://172.17.0.1:9400/, LAN-Grid:8002 ;[fd00::5]:8002");
        HashSet<string> allowed = HypergridEgressPolicy.PrivateGridsAllowed(config);

        Xunit.Assert.True(HypergridEgressPolicy.IsAllowedTarget("http://172.17.0.1:9400/", null, allowed));
        Xunit.Assert.True(HypergridEgressPolicy.IsAllowedTarget("http://lan-grid:8002", null, allowed));
        Xunit.Assert.True(HypergridEgressPolicy.IsAllowedTarget("http://[fd00::5]:8002/", null, allowed));
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedTarget("http://172.17.0.1:9401/", null, allowed), "another port");
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedTarget("http://10.0.0.5:9400/", null, allowed), "another host");
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedTarget("ftp://172.17.0.1:9400/", null, allowed), "not http");
        Xunit.Assert.False(HypergridEgressPolicy.IsAllowedTarget("http://172.17.0.1:9400/", null, null), "none named");
        Xunit.Assert.Empty(HypergridEgressPolicy.PrivateGridsAllowed(new Nini.Config.IniConfigSource()));
    }

    [Xunit.Fact]
    public void HypergridEgressPolicyAllowsLocalGatewayCarveOut()
    {
        Xunit.Assert.True(HypergridEgressPolicy.IsAllowedTarget("http://127.0.0.1:8002/", "http://127.0.0.1:8002"));
    }

    [Xunit.Fact]
    public void TravelSessionMatchesKnownUserAndSession()
    {
        UUID userID = UUID.Random();
        UUID sessionID = UUID.Random();
        HGTravelingData travel = new()
        {
            SessionID = sessionID,
            UserID = userID
        };

        Xunit.Assert.True(UserAgentService.TravelSessionMatches(travel, userID, sessionID));
    }

    [Xunit.Fact]
    public void TravelSessionRejectsMismatchedUserOrSession()
    {
        UUID userID = UUID.Random();
        UUID sessionID = UUID.Random();
        HGTravelingData travel = new()
        {
            SessionID = sessionID,
            UserID = userID
        };

        Xunit.Assert.False(UserAgentService.TravelSessionMatches(travel, UUID.Random(), sessionID));
        Xunit.Assert.False(UserAgentService.TravelSessionMatches(travel, userID, UUID.Random()));
    }

    [Xunit.Fact]
    public void TravelSessionIgnoresUserIDInDataDictionary()
    {
        UUID userID = UUID.Random();
        UUID sessionID = UUID.Random();
        HGTravelingData travel = new()
        {
            SessionID = sessionID,
            UserID = UUID.Random(),
            Data = new Dictionary<string, string> { ["UserID"] = userID.ToString() }
        };

        Xunit.Assert.False(UserAgentService.TravelSessionMatches(travel, userID, sessionID));
        Xunit.Assert.True(UserAgentService.TravelSessionMatches(travel, travel.UserID, sessionID));
    }

    [Xunit.Fact]
    public void TravelSessionRejectsMissingRecordOrData()
    {
        UUID userID = UUID.Random();
        UUID sessionID = UUID.Random();

        Xunit.Assert.False(UserAgentService.TravelSessionMatches(null, userID, sessionID));
        Xunit.Assert.False(UserAgentService.TravelSessionMatches(
            new HGTravelingData { SessionID = sessionID, UserID = userID, Data = null }, userID, sessionID));
    }
}
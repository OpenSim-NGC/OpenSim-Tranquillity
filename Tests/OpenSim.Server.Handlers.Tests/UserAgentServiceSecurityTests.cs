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
            Data = new Dictionary<string, string> { ["UserID"] = userID.ToString() }
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
            Data = new Dictionary<string, string> { ["UserID"] = userID.ToString() }
        };

        Xunit.Assert.False(UserAgentService.TravelSessionMatches(travel, UUID.Random(), sessionID));
        Xunit.Assert.False(UserAgentService.TravelSessionMatches(travel, userID, UUID.Random()));
    }
}
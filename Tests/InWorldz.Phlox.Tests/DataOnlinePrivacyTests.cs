using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using Xunit;
using Xunit.Abstractions;
using FriendInfo = OpenSim.Services.Interfaces.FriendInfo;
using PresenceInfo = OpenSim.Services.Interfaces.PresenceInfo;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// DATA_ONLINE follows Halcyon (InWorldz.Phlox.Engine/LSLSystemAPI.cs GetAgentData): "1" for an
/// avatar in the script's region; otherwise "0" for an avatar who is offline; for one who is online,
/// "1" if it is the script's owner, a friend of the owner, or has not ticked "Only friends and
/// groups know I'm online" (the directory-visibility preference), and "0" if it has, or if the
/// preference cannot be read. Groups are not consulted, as in Halcyon.
/// </summary>
// No longer in "phlox-state": this class touches no process-wide state, so it runs in parallel.
public class DataOnlinePrivacyTests
{
    private readonly ITestOutputHelper _out;
    public DataOnlinePrivacyTests(ITestOutputHelper o) => _out = o;

    private static bool PumpUntil(SchedulerHarness h, Func<bool> done, TimeSpan limit)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < limit)
        {
            h.PumpFor(TimeSpan.FromMilliseconds(50));
            if (done()) return true;
        }
        return done();
    }

    /// <summary>The script's answer for DATA_ONLINE about <paramref name="who"/>.</summary>
    private string Ask(SchedulerHarness h, UUID who)
    {
        h.RezScript($"default {{ state_entry() {{ llRequestAgentData(\"{who}\", DATA_ONLINE); }} dataserver(key q, string d) {{ llSay(0, \"online=\" + d); }} }}");
        // 30 s, not 5 s: under a full parallel run the script had not yet answered once at 5 s.
        Assert.True(PumpUntil(h, () => h.Said.Any(s => s.StartsWith("online=")), TimeSpan.FromSeconds(30)), string.Join(" | ", h.Said));
        string line = h.Said.First(s => s.StartsWith("online="));
        _out.WriteLine(line);
        return line.Substring("online=".Length);
    }

    private sealed class World
    {
        public readonly StubPresence Presence;
        public readonly StubFriends Friends = new();
        public readonly StubProfiles Profiles = new();

        public World(SchedulerHarness h, bool friends = true, bool profiles = true)
        {
            Presence = StubPresence.Install(h);
            if (friends) h.Scene.RegisterModuleInterface<IFriendsModule>(Friends);
            if (profiles) h.Scene.RegisterModuleInterface<IProfileModule>(Profiles);
        }
    }

    [Fact]
    public void AnAvatarInTheRegionIsOnlineWhateverItsSetting()
    {
        using var h = new SchedulerHarness();
        var client = h.AddClient();   // before the stub presence service: adding an avatar needs the scene's own
        var w = new World(h);
        w.Profiles.Hidden.Add(client.AgentId);
        Assert.Equal("1", Ask(h, client.AgentId));
    }

    [Fact]
    public void AnAvatarWhoIsOfflineIsNotOnline()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Friends.Add(who, h.Prim.OwnerID);   // even a friend of the owner with the setting clear
        Assert.Equal("0", Ask(h, who));
    }

    [Fact]
    public void TheOwnerOnlineElsewhereIsOnlineWhateverItsSetting()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var owner = h.Prim.OwnerID;
        w.Presence.OnlineElsewhere.Add(owner);
        w.Profiles.Hidden.Add(owner);
        Assert.Equal("1", Ask(h, owner));
    }

    [Fact]
    public void AFriendOfTheOwnerOnlineElsewhereIsOnlineWithTheSettingTicked()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        w.Profiles.Hidden.Add(who);
        w.Friends.Add(who, h.Prim.OwnerID);
        Assert.Equal("1", Ask(h, who));
    }

    [Fact]
    public void AStrangerOnlineElsewhereIsOnlineWithTheSettingClear()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        Assert.Equal("1", Ask(h, who));
    }

    [Fact]
    public void AStrangerOnlineElsewhereIsNotOnlineWithTheSettingTicked()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        w.Profiles.Hidden.Add(who);
        Assert.Equal("0", Ask(h, who));
    }

    /// <summary>A friendship the other side has not accepted is not a friendship.</summary>
    [Fact]
    public void AnUnansweredFriendshipOfferDoesNotCount()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        w.Profiles.Hidden.Add(who);
        w.Friends.Add(who, h.Prim.OwnerID, theirFlags: -1);
        Assert.Equal("0", Ask(h, who));
    }

    // Halcyon: RetrieveUserPreferences returns null when no plugin can read them (every plugin's
    // exception is caught), and a null preference answers "0".

    [Fact]
    public void AStrangerIsNotOnlineWhenThereIsNoProfilesModule()
    {
        using var h = new SchedulerHarness();
        var w = new World(h, profiles: false);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        Assert.Equal("0", Ask(h, who));
    }

    [Fact]
    public void AStrangerIsNotOnlineWhenThePreferenceCannotBeRead()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        w.Profiles.Unreadable.Add(who);
        Assert.Equal("0", Ask(h, who));
    }

    [Fact]
    public void AStrangerIsNotOnlineWhenThePreferenceLookupThrows()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        w.Profiles.Throw = true;
        Assert.Equal("0", Ask(h, who));
    }

    // Halcyon: GetUserFriendList catches each plugin's exception and returns what it has, so a
    // failing friends lookup is "not a friend" and the preference decides.

    [Fact]
    public void AFailingFriendsLookupFallsThroughToThePreference()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var visible = UUID.Random();
        var hidden = UUID.Random();
        w.Presence.OnlineElsewhere.Add(visible);
        w.Presence.OnlineElsewhere.Add(hidden);
        w.Profiles.Hidden.Add(hidden);
        w.Friends.Throw = true;
        Assert.Equal("1", Ask(h, visible));
        h.ClearSaid(UUID.Zero);
        Assert.Equal("0", Ask(h, hidden));
    }

    [Fact]
    public void TheLookupsRunOffTheSchedulerThread()
    {
        using var h = new SchedulerHarness();
        var w = new World(h);
        var who = UUID.Random();
        w.Presence.OnlineElsewhere.Add(who);
        w.Profiles.Hidden.Add(who);
        int scheduler = Environment.CurrentManagedThreadId;   // the harness pumps the scheduler on this thread
        Assert.Equal("0", Ask(h, who));
        var calls = w.Presence.Threads.Concat(w.Friends.Threads).Concat(w.Profiles.Threads).ToList();
        _out.WriteLine($"scheduler thread {scheduler}; presence {string.Join(",", w.Presence.Threads)}; friends {string.Join(",", w.Friends.Threads)}; profiles {string.Join(",", w.Profiles.Threads)}");
        Assert.NotEmpty(w.Presence.Threads);
        Assert.NotEmpty(w.Friends.Threads);
        Assert.NotEmpty(w.Profiles.Threads);
        Assert.DoesNotContain(scheduler, calls);
    }

    // ---- stubs ----

    /// <summary>A presence service that reports chosen avatars as root agents in another region.</summary>
    public class StubPresence : DispatchProxy
    {
        public readonly HashSet<UUID> OnlineElsewhere = new();
        public readonly ConcurrentBag<int> Threads = new();

        public static StubPresence Install(SchedulerHarness h)
        {
            IPresenceService proxy = Create<IPresenceService, StubPresence>();
            typeof(Scene).GetField("m_PresenceService", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(h.Scene, proxy);
            return (StubPresence)(object)proxy;
        }

        protected override object Invoke(MethodInfo method, object[] args)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (method.Name == nameof(IPresenceService.GetAgents))
            {
                var ids = (string[])args[0];
                return ids.Where(s => UUID.TryParse(s, out UUID id) && OnlineElsewhere.Contains(id))
                    .Select(s => new PresenceInfo { UserID = s, RegionID = UUID.Random() }).ToArray();
            }
            if (method.Name == nameof(IPresenceService.GetAgent)) return null;
            return method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null;
        }
    }

    /// <summary>Friends as the friends service holds them: each user's list of (friend, their flags).</summary>
    public class StubFriends : IFriendsModule
    {
        private readonly Dictionary<UUID, List<(UUID friend, int theirFlags)>> m_lists = new();
        public readonly ConcurrentBag<int> Threads = new();
        public volatile bool Throw;

        /// <summary>A friendship between a and b; theirFlags -1 on a's row is an offer b has not answered.</summary>
        public void Add(UUID a, UUID b, int theirFlags = 1)
        {
            lock (m_lists)
            {
                if (!m_lists.TryGetValue(a, out var la)) m_lists[a] = la = new();
                la.Add((b, theirFlags));
                if (theirFlags == -1) return;
                if (!m_lists.TryGetValue(b, out var lb)) m_lists[b] = lb = new();
                lb.Add((a, 1));
            }
        }

        public bool IsFriendInService(UUID userID, UUID friendID)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (Throw) throw new InvalidOperationException("friends service down");
            lock (m_lists)
                return m_lists.TryGetValue(userID, out var l) && l.Any(f => f.friend == friendID && f.theirFlags != -1);
        }

        // Cache-only members: the avatars asked about are not on this simulator.
        public bool AreFriendsCached(UUID userID) => false;
        public FriendInfo[] GetFriendsFromCache(UUID userID) => Array.Empty<FriendInfo>();
        public void AddFriendship(IClientAPI client, UUID friendID) { }
        public void RemoveFriendship(IClientAPI client, UUID exFriendID) { }
        public int GetRightsGrantedByFriend(UUID userID, UUID friendID) => 0;
        public void GrantRights(IClientAPI remoteClient, UUID friendID, int perms) { }
        public void IsNowRoot(ScenePresence sp) { }
        public bool SendFriendsOnlineIfNeeded(IClientAPI client) => false;
        public bool IsFriendOnline(UUID userID, UUID friendID) => false;
        public void CacheFriendsOnline(UUID userID, List<UUID> friendsOnline, bool online) { }
        public void CacheFriendOnline(UUID userID, UUID friendOnline, bool online) { }
        public List<UUID> GetCachedFriendsOnline(UUID userID) => new();
        public bool IsFriend(UUID userID, UUID friendID) => false;
    }

    /// <summary>User preferences as the profiles service holds them; Visible is false when the box is ticked.</summary>
    public class StubProfiles : IProfileModule
    {
        public readonly HashSet<UUID> Hidden = new();
        public readonly HashSet<UUID> Unreadable = new();
        public readonly ConcurrentBag<int> Threads = new();
        public volatile bool Throw;

        public UserPreferences GetUserPreferences(UUID userID)
        {
            Threads.Add(Environment.CurrentManagedThreadId);
            if (Throw) throw new InvalidOperationException("profiles service down");
            if (Unreadable.Contains(userID)) return null;
            return new UserPreferences { UserId = userID, Visible = !Hidden.Contains(userID), EMail = "someone@example.com" };
        }

        public void RequestAvatarProperties(IClientAPI remoteClient, UUID avatarID) { }
    }
}

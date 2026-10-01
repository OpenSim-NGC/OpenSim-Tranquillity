/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Linq;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;
using OpenSim.Tests.Common;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Agent data and names. llGetUsername and llGetDisplayName answer for an avatar the region knows (root or child
/// agent), else "" (SL wiki llGetUsername: "present in or otherwise known to the sim ..., otherwise an empty string is
/// returned"), with the grid's display-name module where it has one (YEngine LSL_Api.llGetDisplayName).
/// iwGetAgentData's DATA_ONLINE follows llRequestAgentData's privacy rule; DATA_ACCOUNT_TYPE is the account's
/// UserTitle. Halcyon's unknown-agent and bad-key answers (LSLSystemAPI.GetAgentData, llRequestAgentData,
/// iwRequestAnimationData, llTextBox, llRequestUsername). The local-time functions agree with each other.
/// </summary>
// No test reaches a network service: the presence service is a stub. No process-wide state: the class runs in parallel.
public class AgentDataRowsTests
{
    private const int DATA_ONLINE = 1, DATA_BORN = 3, DATA_ACCOUNT_TYPE = 11001;

    private static UserAccount Account(Scene scene, string first, string last, string title = null)
    {
        UserAccount acct = UserAccountHelpers.CreateUserWithInventory(scene, first, last, UUID.Random(), "pw");
        if (title != null)
        {
            acct.UserTitle = title;
            scene.UserAccountService.StoreUserAccount(acct);
        }
        return acct;
    }

    // ---------------------------------------------------------------- llGetUsername / llGetDisplayName

    [Fact]
    public void AnAbsentAvatarHasNoName()
    {
        using var r = new ApiCallRig();
        UserAccount acct = Account(r.H.Scene, "Far", "Away");
        Assert.Equal("", r.Api.llGetUsername(acct.PrincipalID.ToString()));
        Assert.Equal("", r.Api.llGetDisplayName(acct.PrincipalID.ToString()));
    }

    [Fact]
    public void AChildAgentIsNamed()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        sp.IsChildAgent = true;
        Assert.Equal(sp.Name, r.Api.llGetUsername(sp.UUID.ToString()));
        Assert.Equal(sp.Name, r.Api.llGetDisplayName(sp.UUID.ToString()));
    }

    [Fact]
    public void TheDisplayNameModuleGivesTheDisplayName()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        r.H.Scene.RegisterModuleInterface<IDisplayNameModule>(Fake<IDisplayNameModule>.Create((m, a) =>
            (UUID)a[0] == sp.UUID ? "Shown Name" : (object)null));
        Assert.Equal("Shown Name", r.Api.llGetDisplayName(sp.UUID.ToString()));
        Assert.Equal(sp.Name, r.Api.llGetUsername(sp.UUID.ToString()));
    }

    [Fact]
    public void NamesOfAbsentAvatarsNeedNoServiceCall()
    {
        using var r = new ApiCallRig();
        string absent = UUID.Random().ToString();
        Assert.False(r.Api.NeedsService("llGetUsername", new object[] { absent }));
        Assert.False(r.Api.NeedsService("llGetDisplayName", new object[] { absent }));
    }

    // ---------------------------------------------------------------- iwGetAgentData DATA_ONLINE, DATA_ACCOUNT_TYPE

    [Fact]
    public void TheOwnerOnlineElsewhereIsOnlineToIwGetAgentData()
    {
        using var r = new ApiCallRig();
        var presence = DataOnlinePrivacyTests.StubPresence.Install(r.H);
        presence.OnlineElsewhere.Add(r.H.Prim.OwnerID);
        Assert.Equal("1", r.Api.iwGetAgentData(r.H.Prim.OwnerID.ToString(), DATA_ONLINE));
    }

    [Fact]
    public void DataOnlineForAnAbsentAvatarGoesToTheServiceLane()
    {
        using var r = new ApiCallRig();
        Assert.True(r.Api.NeedsService("iwGetAgentData", new object[] { UUID.Random().ToString(), DATA_ONLINE }));
    }

    [Fact]
    public void AccountTypeIsTheUserTitle()
    {
        using var r = new ApiCallRig();
        UserAccount acct = Account(r.H.Scene, "Titled", "Person", "Example Title");
        Assert.Equal("Example Title", r.Api.iwGetAgentData(acct.PrincipalID.ToString(), DATA_ACCOUNT_TYPE));
        Assert.True(r.Api.NeedsService("iwGetAgentData", new object[] { UUID.Random().ToString(), DATA_ACCOUNT_TYPE }));
    }

    [Fact]
    public void AccountTypeReachesTheDataserver()
    {
        using var h = new SchedulerHarness();
        UserAccount acct = Account(h.Scene, "Titled", "Person", "Example Title");
        h.RezScript("default { state_entry() { llRequestAgentData(\"" + acct.PrincipalID + "\", DATA_ACCOUNT_TYPE); }"
            + " dataserver(key q, string d) { llSay(0, \"type=\" + d); } }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("type=")), TimeSpan.FromSeconds(30)));
        Assert.Contains("type=Example Title", h.Said);
    }

    // ---------------------------------------------------------------- Halcyon's unknown-agent and bad-key answers

    [Fact]
    public void AnUnknownAgentWasBornAtTheEpoch()
    {
        using var r = new ApiCallRig();
        Assert.Equal("1970-01-01", r.Api.iwGetAgentData(UUID.Random().ToString(), DATA_BORN));
    }

    [Fact]
    public void AnUnknownAgentWasBornAtTheEpochInTheDataserverToo()
    {
        using var h = new SchedulerHarness();
        h.RezScript("default { state_entry() { llRequestAgentData(\"" + UUID.Random() + "\", DATA_BORN); }"
            + " dataserver(key q, string d) { llSay(0, \"born=\" + d); } }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("born=")), TimeSpan.FromSeconds(30)));
        Assert.Contains("born=1970-01-01", h.Said);
    }

    [Fact]
    public void ABadKeyStillGetsAQueryKeyAndAnEmptyAnswer()
    {
        using var h = new SchedulerHarness();
        h.RezScript("key q; default { state_entry() { q = llRequestAgentData(\"not a key\", DATA_NAME);"
            + " llSay(0, \"null=\" + (string)(q == NULL_KEY)); }"
            + " dataserver(key k, string d) { llSay(0, \"answer=\" + (string)(k == q) + \"[\" + d + \"]\"); } }");
        Assert.True(h.PumpUntil(() => h.Said.Any(s => s.StartsWith("answer=")), TimeSpan.FromSeconds(30)),
            string.Join(" | ", h.Said));
        Assert.Contains("null=0", h.Said);
        Assert.Contains("answer=1[]", h.Said);
    }

    [Fact]
    public void AMissingAnimationGivesAnEmptyStringAfterASecond()
    {
        using var r = new ApiCallRig();
        var (ms, ret) = r.Accounted(api => api.iwRequestAnimationData("missing"));
        Assert.Equal("", ret);
        Assert.Equal(1000, ms);
    }

    [Fact]
    public void ATextBoxToNullKeyStillSleeps()
    {
        using var r = new ApiCallRig();
        r.H.Scene.RegisterModuleInterface(RecordingDialogs.Create(out _));
        var (ms, _) = r.Accounted(api => { api.llTextBox(UUID.Zero.ToString(), "hi", 5); return null; });
        Assert.Equal(1000, ms);
    }

    [Fact]
    public void RequestUsernameTakesAHundredMilliseconds()
    {
        using var r = new ApiCallRig();
        ScenePresence sp = r.AddAvatar();
        var (ms, ret) = r.Accounted(api => { api.llRequestUsername(sp.UUID.ToString()); return null; });
        Assert.NotEqual(UUID.Zero.ToString(), ret);
        Assert.Equal(100, ms);
    }

    // ---------------------------------------------------------------- local time

    [Fact]
    public void TheLocalTimeFunctionsAgree()
    {
        using var r = new ApiCallRig();
        int offset = (int)TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).TotalSeconds;
        Assert.Equal(offset, r.Api.iwGetLocalTimeOffset());
        int utc = r.Api.llGetUnixTime();
        Assert.InRange(r.Api.iwGetLocalTime() - utc - offset, -2, 2);
        Assert.InRange(r.Api.llGetLocalTime() - r.Api.iwGetLocalTime(), -2, 2);
        // iwFormatTime's local branch prints the same wall clock iwGetLocalTime counts.
        int now = r.Api.llGetUnixTime();
        string local = r.Api.iwFormatTime(now, 0, "yyyy-MM-dd HH:mm:ss");
        string shifted = r.Api.iwFormatTime(now + r.Api.iwGetLocalTimeOffset(), 1, "yyyy-MM-dd HH:mm:ss");
        Assert.Equal(shifted, local);
    }
}

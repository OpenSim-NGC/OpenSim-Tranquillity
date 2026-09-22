using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.Handlers;
using OpenSim.Server.Handlers.Base;
using OpenSim.Services.Interfaces;

namespace OpenSim.Server.Handlers.Tests;

public class UserProfilesSecurityTests
{
    [Xunit.Fact]
    public void PublicProfileReadRemainsOpenFromUntrustedCaller()
    {
        JsonRpcProfileHandlers handler = new(new StubUserProfilesService(), new ControlPlaneAccess(new IniConfigSource()));
        OSDMap request = JsonRpcRequestFrom("203.0.113.10", new OSDMap { ["creatorId"] = OSD.FromUUID(UUID.Random()) });
        JsonRpcResponse response = new();

        bool handled = handler.AvatarClassifiedsRequest(request, ref response);

        Xunit.Assert.True(handled);
        Xunit.Assert.Equal(0, response.Error.Code);
        Xunit.Assert.NotNull(response.Result);
    }

    [Xunit.Fact]
    public void SensitiveProfileWriteIsHiddenFromUntrustedCaller()
    {
        JsonRpcProfileHandlers handler = new(new StubUserProfilesService(), new ControlPlaneAccess(new IniConfigSource()));
        OSDMap request = JsonRpcRequestFrom("203.0.113.10", new OSDMap());
        JsonRpcResponse response = new();

        bool handled = handler.AvatarPropertiesUpdate(request, ref response);

        Xunit.Assert.False(handled);
        Xunit.Assert.Equal(ErrorCode.MethodNotFound, response.Error.Code);
    }

    [Xunit.Fact]
    public void SensitiveProfileWriteIsAllowedFromTrustedCaller()
    {
        IniConfigSource config = new();
        config.AddConfig("Security").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        JsonRpcProfileHandlers handler = new(new StubUserProfilesService(), new ControlPlaneAccess(config));
        OSDMap request = JsonRpcRequestFrom("203.0.113.10", new OSDMap());
        JsonRpcResponse response = new();

        bool handled = handler.AvatarPropertiesUpdate(request, ref response);

        Xunit.Assert.True(handled);
        Xunit.Assert.Equal(0, response.Error.Code);
    }

    [Xunit.Fact]
    public void SensitiveProfileWriteIsHiddenFromScriptCaller()
    {
        IniConfigSource config = new();
        config.AddConfig("Security").Set("ControlPlaneTrustedHosts", "203.0.113.10");
        JsonRpcProfileHandlers handler = new(new StubUserProfilesService(), new ControlPlaneAccess(config));
        OSDMap request = JsonRpcRequestFrom("203.0.113.10", new OSDMap());
        request[ControlPlaneAccess.JsonRpcLlHttpRequestKey] = OSD.FromBoolean(true);
        JsonRpcResponse response = new();

        bool handled = handler.AvatarPropertiesUpdate(request, ref response);

        Xunit.Assert.False(handled);
        Xunit.Assert.Equal(ErrorCode.MethodNotFound, response.Error.Code);
    }

    private static OSDMap JsonRpcRequestFrom(string address, OSDMap parameters)
    {
        return new OSDMap
        {
            ["params"] = parameters,
            [ControlPlaneAccess.JsonRpcRemoteAddressKey] = OSD.FromString(address),
            [ControlPlaneAccess.JsonRpcLlHttpRequestKey] = OSD.FromBoolean(false)
        };
    }

    private sealed class StubUserProfilesService : IUserProfilesService
    {
        public OSD AvatarClassifiedsRequest(UUID creatorId) => new OSDArray();
        public bool ClassifiedUpdate(UserClassifiedAdd ad, ref string result) => true;
        public bool ClassifiedInfoRequest(ref UserClassifiedAdd ad, ref string result) => true;
        public bool ClassifiedDelete(UUID recordId) => true;
        public OSD AvatarPicksRequest(UUID creatorId) => new OSDArray();
        public bool PickInfoRequest(ref UserProfilePick pick, ref string result) => true;
        public bool PicksUpdate(ref UserProfilePick pick, ref string result) => true;
        public bool PicksDelete(UUID pickId) => true;
        public bool AvatarNotesRequest(ref UserProfileNotes note) => true;
        public bool NotesUpdate(ref UserProfileNotes note, ref string result) => true;
        public bool AvatarPropertiesRequest(ref UserProfileProperties prop, ref string result) => true;
        public bool AvatarPropertiesUpdate(ref UserProfileProperties prop, ref string result) => true;
        public bool UserPreferencesRequest(ref UserPreferences pref, ref string result) => true;
        public bool UserPreferencesUpdate(ref UserPreferences pref, ref string result) => true;
        public bool AvatarInterestsUpdate(UserProfileProperties prop, ref string result) => true;
        public OSD AvatarImageAssetsRequest(UUID avatarId) => new OSDArray();
        public bool RequestUserAppData(ref UserAppData prop, ref string result) => true;
        public bool SetUserAppData(UserAppData prop, ref string result) => true;
    }
}
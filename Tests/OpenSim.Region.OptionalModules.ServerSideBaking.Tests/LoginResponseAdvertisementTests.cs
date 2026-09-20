using System.Collections;
using Microsoft.Extensions.Logging.Abstractions;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;
using OpenSim.Services.LLLoginService;
using Xunit;

namespace OpenSim.Region.OptionalModules.ServerSideBaking.Tests;

/// <summary>
/// S4 Part 2 — viewer contract V6. The login response advertises <c>agent_appearance_service</c> when the grid
/// has one and <b>omits the key entirely</b> when it does not.
///
/// <para>
/// The distinction matters because of what the viewer does with it: <c>llstartup.cpp:4047-4051</c> adopts the
/// value only when non-empty, and <c>LLVOAvatar::getImageURL</c> warns
/// "AgentAppearanceServiceURL not set - Baked texture requests will fail" and returns "" when it is unset
/// (<c>llvoavatar.cpp:5901-5906</c>). On a bit-0 region that means the avatar never textures — which is exactly
/// what Ebony produced when S3's flag was flipped before this service existed.
/// </para>
/// </summary>
public class LoginResponseAdvertisementTests
{
    private const string Key = "agent_appearance_service";

    static LoginResponseAdvertisementTests()
    {
        // LLLoginResponse.ToHashtable wraps its whole body in try/catch and returns the *failure* response on any
        // exception (LLLoginResponse.cs:483, :593-597). It logs inside that body, and with no logger factory
        // installed the log call throws, so every assertion here would silently be made against the failure
        // hashtable instead of the real one. Installing a null factory is what makes this test test anything.
        LoggerProvider.LoggerFactory = NullLoggerFactory.Instance;
    }

    /// <summary>
    /// A fresh response per encoding. <c>ToHashtable</c> and <c>ToOSDMap</c> both mutate the instance they are
    /// called on — they append to <c>loginFlags</c>, <c>globalTextures</c> and <c>uiConfig</c> — and both swallow
    /// any exception and return the *failure* response instead (LLLoginResponse.cs:593-597, :712-717). Calling
    /// them one after the other on one object therefore makes the second one fail silently, which is not
    /// something production ever does: the login service builds a response and serialises it once.
    /// </summary>
    /// <summary>
    /// A response the serialisers can actually render. The parameterless constructor leaves
    /// <c>inventoryLibRoot</c> null (LLLoginResponse.cs:141 — every other ArrayList is initialised, that one is
    /// not), and <c>ToOSDMap</c> passes it straight to <c>ArrayListToOSDArray</c> (:664), so the LLSD form of a
    /// default-constructed response always throws and silently degrades to the failure map. Production never hits
    /// it because the real constructor fills it in. Setting it here is the fixture, not a workaround for anything
    /// S4 introduced.
    /// </summary>
    private static LLLoginResponse Fresh(string url)
    {
        var r = new LLLoginResponse { InventoryLibRoot = new ArrayList() };
        if (url is not null) r.AgentAppearanceServiceURL = url;
        return r;
    }

    private static Hashtable Xml(string url)
    {
        var r = Fresh(url);
        var h = r.ToHashtable();
        Assert.True(h.ContainsKey("session_id"), "the response should have been built, not the failure form");
        return h;
    }

    private static OSDMap Llsd(string url)
    {
        var r = Fresh(url);
        var m = (OSDMap)r.ToOSDMap();
        Assert.True(m.ContainsKey("session_id"), "the response should have been built, not the failure form");
        return m;
    }

    private static (Hashtable Xml, OSDMap Llsd) Build(string url) => (Xml(url), Llsd(url));

    [Fact]
    public void WhenConfiguredTheResponseCarriesTheUrlInBothEncodings()
    {
        var (xml, llsd) = Build("http://legiongrid.ddns.net:8002/");

        Assert.True(xml.ContainsKey(Key));
        Assert.Equal("http://legiongrid.ddns.net:8002/", xml[Key]);
        Assert.True(llsd.ContainsKey(Key));
        Assert.Equal("http://legiongrid.ddns.net:8002/", llsd[Key].AsString());
    }

    [Fact]
    public void WhenNotConfiguredTheKeyIsAbsentEntirelyRatherThanEmpty()
    {
        var (xml, llsd) = Build(null);

        Assert.False(xml.ContainsKey(Key));
        Assert.False(llsd.ContainsKey(Key));
    }

    [Fact]
    public void AnEmptyOrNullValueIsAlsoAnAbsentKey()
    {
        foreach (var value in new[] { "", null })
        {
            var (xml, llsd) = Build(value);
            Assert.False(xml.ContainsKey(Key), $"value '{value}'");
            Assert.False(llsd.ContainsKey(Key), $"value '{value}'");
        }
    }

    /// <summary>
    /// The response is otherwise untouched: advertising this must not perturb anything a viewer already reads.
    /// Compared key set by key set, configured against not.
    /// </summary>
    [Fact]
    public void NothingElseInTheResponseChanges()
    {
        var (withXml, withLlsd) = Build("http://example.invalid:8002/");
        var (withoutXml, withoutLlsd) = Build(null);

        var added = withXml.Keys.Cast<string>().Except(withoutXml.Keys.Cast<string>()).ToArray();
        Assert.Equal(new[] { Key }, added);
        Assert.Empty(withoutXml.Keys.Cast<string>().Except(withXml.Keys.Cast<string>()));

        var addedLlsd = withLlsd.Keys.Except(withoutLlsd.Keys).ToArray();
        Assert.Equal(new[] { Key }, addedLlsd);
        Assert.Empty(withoutLlsd.Keys.Except(withLlsd.Keys));
    }

    /// <summary>
    /// The trailing slash is not cosmetic. The viewer builds the URL as
    /// <c>appearance_service_url + "texture/" + ...</c> with no separator (<c>llvoavatar.cpp:5912</c>), so a
    /// configured value without one yields <c>...:8002texture/&lt;agent&gt;/...</c> and every fetch fails. The
    /// login service normalises it exactly as it does <c>MapTileURL</c>; this pins the arithmetic the
    /// normalisation exists to protect.
    /// </summary>
    [Fact]
    public void TheViewerConcatenatesWithoutASeparatorSoTheValueMustEndInSlash()
    {
        const string agent = "a7d2ff2e-dc32-44d8-aa61-3d22070a4964";
        const string asset = "11111111-2222-3333-4444-555555555555";

        var good = "http://host:8002/" + "texture/" + agent + "/" + "head" + "/" + asset;
        Assert.Equal($"http://host:8002/texture/{agent}/head/{asset}", good);

        var bad = "http://host:8002" + "texture/" + agent + "/" + "head" + "/" + asset;
        Assert.Equal($"http://host:8002texture/{agent}/head/{asset}", bad);
        Assert.DoesNotContain("/texture/", bad);
    }
}

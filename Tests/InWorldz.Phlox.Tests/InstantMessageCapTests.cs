using System;
using System.Linq;
using System.Text;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using Xunit;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// llInstantMessage's message length, as the SL wiki states it (https://wiki.secondlife.com/wiki/LlInstantMessage):
/// "Messages longer than 1023 bytes will be truncated to 1023 bytes. This can convey 1023 ASCII characters, or fewer
/// if non-ASCII characters are present." The cap counts bytes of UTF-8; a character the cut would split is dropped
/// whole, so the message stays valid text.
/// </summary>
// No test reaches a network service: the IM transfer module is an in-memory recorder.
// Test grouping: no process-wide state, so the class runs in parallel.
public class InstantMessageCapTests
{
    private static readonly UUID To = new UUID("5a5a5a5a-0000-4000-8000-00000000a1a1");

    /// <summary>Run one llInstantMessage of <paramref name="message"/> and return what reached the transfer module.</summary>
    private static string Sent(string message)
    {
        using var h = new SchedulerHarness();
        h.Scene.RegisterModuleInterface<IMessageTransferModule>(RecordingIms.Create(out var ims));
        h.RezScript("default { state_entry() { llInstantMessage(\"" + To + "\", \"" + message + "\"); } }");
        Assert.True(h.PumpUntil(() => !ims.Sent.IsEmpty), "no instant message was sent");
        Assert.True(ims.Sent.TryPeek(out GridInstantMessage im));
        Assert.Equal(To.Guid, im.toAgentID);
        return im.message;
    }

    /// <summary>A two-byte character in UTF-8 (U+00E9, e with an acute accent).</summary>
    private static readonly string E = ((char)0xE9).ToString();

    private static int Bytes(string s) => Encoding.UTF8.GetByteCount(s);

    [Fact]
    public void AMessageOfExactly1023BytesIsSentWhole()
    {
        string msg = new string('a', 1023);
        Assert.Equal(msg, Sent(msg));
    }

    [Fact]
    public void AMessageOneByteOverTheCapIsCutTo1023Bytes()
    {
        string msg = new string('a', 1024);
        string got = Sent(msg);
        Assert.Equal(1023, Bytes(got));
        Assert.Equal(msg.Substring(0, 1023), got);
    }

    [Fact]
    public void AMultibyteCharacterTheCutWouldSplitIsDroppedWhole()
    {
        // 1022 one-byte characters and one two-byte character: 1024 bytes, 1023 characters. The cap counts bytes,
        // so the message is over it; cutting at 1023 bytes would split the last character, which is dropped.
        string msg = new string('a', 1022) + E;
        string got = Sent(msg);
        Assert.Equal(new string('a', 1022), got);
        Assert.Equal(1022, Bytes(got));
    }

    [Fact]
    public void MultibyteCharactersThatFitTheCapAreSentWhole()
    {
        // 511 two-byte characters and one one-byte character: exactly 1023 bytes in 512 characters.
        string msg = string.Concat(Enumerable.Repeat(E, 511)) + "a";
        Assert.Equal(1023, Bytes(msg));
        Assert.Equal(msg, Sent(msg));
    }

    [Fact]
    public void AMessageFarOverTheCapInMultibyteCharactersIsCutBelowIt()
    {
        // 600 two-byte characters (1200 bytes): 511 whole characters fit (1022 bytes); the 512th would need 1024.
        string msg = string.Concat(Enumerable.Repeat(E, 600));
        string got = Sent(msg);
        Assert.Equal(string.Concat(Enumerable.Repeat(E, 511)), got);
    }
}

using System.IO;
using ProtoBuf;
using Xunit;
using Xunit.Abstractions;

namespace InWorldz.Phlox.Tests;

/// <summary>
/// Upgrade gate. DetectVariables gained ProtoMembers 27-29 (Damage, DamageType, OriginalDamage)
/// and it IS on the persisted state row: every queued or running event is saved as a SerializedPostedEvent
/// whose DetectVars (tag 3) are DetectVariables. Every row an existing region holds was written before tag 27;
/// this is the same proof tag 22 got - a record shaped exactly as the old code wrote it, deserialised as
/// the current type, with the old fields intact and the new ones at their defaults.
/// </summary>
public class DetectVariablesRowTests
{
    private readonly ITestOutputHelper _out;
    public DetectVariablesRowTests(ITestOutputHelper o) => _out = o;

    [ProtoContract]
    private class PreTag27DetectVariables
    {
        [ProtoMember(3)] public string Key;
        [ProtoMember(5)] public string Name;
        [ProtoMember(6)] public string Owner;
        [ProtoMember(9)] public int Type;
    }

    [ProtoContract]
    private class PreTag27PostedEvent
    {
        [ProtoMember(1, IsRequired = true)] public InWorldz.Phlox.Types.SupportedEventList.Events EventType;
        [ProtoMember(3)] public PreTag27DetectVariables[] DetectVars;
        [ProtoMember(4)] public int TransitionToState;
    }

    [Fact]
    public void ADetectRecordWrittenBeforeTagTwentySevenLoadsWithZeroDamage()
    {
        var old = new PreTag27PostedEvent
        {
            EventType = InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START,
            DetectVars = new[] { new PreTag27DetectVariables { Key = "k", Name = "toucher", Owner = "o", Type = 1 } },
            TransitionToState = -1,
        };

        using var ms = new MemoryStream();
        Serializer.Serialize(ms, old);
        ms.Position = 0;
        var loaded = Serializer.Deserialize<InWorldz.Phlox.Serialization.SerializedPostedEvent>(ms);

        Assert.Equal(InWorldz.Phlox.Types.SupportedEventList.Events.TOUCH_START, loaded.EventType);
        var d = Assert.Single(loaded.DetectVars);
        _out.WriteLine($"Key={d.Key} Name={d.Name} Type={d.Type} Damage={d.Damage} DamageType={d.DamageType} Original={d.OriginalDamage} Adjust={(d.AdjustDamage == null ? "null" : "set")}");
        Assert.Equal("k", d.Key);
        Assert.Equal("toucher", d.Name);
        Assert.Equal(1, d.Type);
        Assert.Equal(0f, d.Damage);
        Assert.Equal(0, d.DamageType);
        Assert.Equal(0f, d.OriginalDamage);
        Assert.Null(d.AdjustDamage);
    }

    [Fact]
    public void TheThreeDamageFieldsRoundTripAndTheHookDoesNot()
    {
        var evt = new InWorldz.Phlox.Serialization.SerializedPostedEvent
        {
            EventType = InWorldz.Phlox.Types.SupportedEventList.Events.ON_DAMAGE,
            DetectVars = new[] { new InWorldz.Phlox.VM.DetectVariables { Key = "src", Damage = 10f, DamageType = 5, OriginalDamage = 20f, AdjustDamage = v => { } } },
        };
        using var ms = new MemoryStream();
        Serializer.Serialize(ms, evt);
        ms.Position = 0;
        var back = Serializer.Deserialize<InWorldz.Phlox.Serialization.SerializedPostedEvent>(ms);
        var d = Assert.Single(back.DetectVars);
        Assert.Equal((10f, 5, 20f), (d.Damage, d.DamageType, d.OriginalDamage));
        Assert.Null(d.AdjustDamage);
    }
}

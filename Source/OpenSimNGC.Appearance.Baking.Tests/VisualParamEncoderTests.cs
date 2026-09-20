using Xunit;

namespace OpenSimNGC.Appearance.Baking.Tests;

/// <summary>
/// The VisualParams block must be the one a viewer sends: 253 parameters (group 0 and 3) in id order,
/// float32-truncated bytes, worn wearable values on top of the sim's stored bytes for unworn types.
/// Ported from the web-viewer gateway (session 14) in S0b.
/// </summary>
public class VisualParamEncoderTests
{
    private static AvatarLad Lad => AvatarLad.Embedded;

    [Fact]
    public void the_send_list_is_the_253_transmitted_parameters_in_id_order()
    {
        var list = VisualParamEncoder.SendList(Lad);
        Assert.Equal(253, list.Count);
        Assert.Equal(249, list.Count(p => p.Group == 0));
        Assert.Equal(new[] { 163, 868, 869, 877 }, list.Where(p => p.Group == 3).Select(p => p.Id).ToArray());
        Assert.True(list.Zip(list.Skip(1)).All(pair => pair.First.Id < pair.Second.Id));
        Assert.Equal(31, VisualParamEncoder.IndexOf(Lad, 80));     // `male`, what the client picks the body by
        Assert.Equal(252, VisualParamEncoder.IndexOf(Lad, 11001)); // hover, the last one
    }

    [Fact]
    public void bytes_are_float32_truncated_like_the_viewer()
    {
        Assert.Equal(74, VisualParamEncoder.F32ToU8(0f, -0.5f, 1.2f));   // 0.5/1.7*255 = 74.999 in float32: 74, not 75
        Assert.Equal(255, VisualParamEncoder.F32ToU8(1f, 0f, 1f));
        Assert.Equal(0, VisualParamEncoder.F32ToU8(-3f, 0f, 1f));         // clamped
        Assert.Equal(51, VisualParamEncoder.F32ToU8(0.2f, 0f, 1f));
        Assert.Equal(204, VisualParamEncoder.F32ToU8(0.8f, 0f, 1f));
    }

    [Fact]
    public void worn_wearables_override_carried_bytes_which_override_defaults()
    {
        var lad = Lad;
        var carried = new byte[253];
        for (var i = 0; i < carried.Length; i++) carried[i] = (byte)(i % 256);
        var idx800 = VisualParamEncoder.IndexOf(lad, 800);   // shirt sleeve length
        var idx608 = VisualParamEncoder.IndexOf(lad, 608);   // jacket bottom length: not worn here
        var idx33 = VisualParamEncoder.IndexOf(lad, 33);     // shape height
        var worn = new List<(WearableKind, IReadOnlyDictionary<int, float>)>
        {
            (WearableKind.Shape, new Dictionary<int, float> { [33] = 1f }),
            (WearableKind.Shirt, new Dictionary<int, float> { [800] = 0.5f }),
            (WearableKind.Shirt, new Dictionary<int, float> { [800] = 1f }),   // topmost shirt wins
        };
        var r = VisualParamEncoder.Encode(lad, worn, carried);
        Assert.Equal(253, r.Bytes.Length);
        Assert.Equal(255, r.Bytes[idx800]);
        Assert.Equal(VisualParamEncoder.F32ToU8(1f, lad.Params[33].Min, lad.Params[33].Max), r.Bytes[idx33]);
        Assert.Equal(carried[idx608], r.Bytes[idx608]);
        Assert.Equal(2, r.FromWearables);
        Assert.Equal(251, r.Carried);
        Assert.Equal(0, r.Defaults);

        var noSim = VisualParamEncoder.Encode(lad, worn, null);
        Assert.Equal(VisualParamEncoder.F32ToU8(lad.Params[608].Default, lad.Params[608].Min, lad.Params[608].Max), noSim.Bytes[idx608]);
        Assert.Equal(251, noSim.Defaults);

        var wrongLength = VisualParamEncoder.Encode(lad, worn, new byte[218]);   // a stale blob is not carried
        Assert.Equal(251, wrongLength.Defaults);
    }
}

using OpenMetaverse;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// One worn wearable, as the compositor needs it: the asset it came from, its
/// wearable type, and the raw <c>LLWearable</c> text (the asset body) that names
/// the textures and parameter values for that wearable.
/// </summary>
/// <param name="AssetId">Asset id of the wearable asset.</param>
/// <param name="WearableType">
/// Wearable type index as used on the wire and in <c>avatar_lad.xml</c>
/// (0 = shape, 1 = skin, 2 = hair, 3 = eyes, 4 = shirt, 5 = pants, ...; see <see cref="WearableKind"/>).
/// </param>
/// <param name="RawText">Full text of the wearable asset, unparsed.</param>
public sealed record WearableInput(UUID AssetId, int WearableType, string RawText);

/// <summary>
/// One source texture referenced by a wearable, already fetched from the asset
/// service and still in its JPEG 2000 encoding.
/// </summary>
/// <param name="TextureId">Texture asset id.</param>
/// <param name="J2kBytes">JPEG 2000 codestream or JP2 file bytes.</param>
public sealed record TextureInput(UUID TextureId, byte[] J2kBytes);

/// <summary>
/// Everything one bake run needs. The request is complete and self-contained:
/// the compositor performs no I/O and no asset fetches of its own.
/// </summary>
/// <param name="Wearables">The wearables currently worn, in wear order (a later wearable of the same type is on top).</param>
/// <param name="VisualParams">
/// Visual parameter values keyed by parameter id, in the parameter's native range from
/// <c>avatar_lad.xml</c> (not the 0..255 wire encoding). A worn wearable's own stored value always
/// wins for the parameters its type owns; these fill in only what no worn wearable stores, and take
/// part in <see cref="BakeHash"/>. May be empty.
/// </param>
/// <param name="Textures">
/// Source textures keyed by texture id. Any texture a wearable references that
/// is absent from this map is reported in <see cref="FidelityReport.MissingTextures"/>
/// and its layer is skipped.
/// </param>
/// <param name="BakeSize">Output edge size in pixels for every channel (ADR-008: 1024 by default).</param>
public sealed record BakeRequest(
    IReadOnlyList<WearableInput> Wearables,
    IReadOnlyDictionary<int, float> VisualParams,
    IReadOnlyDictionary<UUID, TextureInput> Textures,
    int BakeSize)
{
    /// <summary>
    /// The channels to composite, or null (the default) for every channel the outfit needs. A caller that has
    /// recognised a channel's inputs as unchanged and is reusing its stored bake (ADR-004) leaves that channel
    /// out, and then nothing is decoded or composited for it. Naming a channel the outfit does not need does not
    /// add it: the set is intersected with what the wearables actually feed.
    /// <para>
    /// It is a request-shaping field only. It is deliberately **not** part of <see cref="BakeHash"/>: the bake of
    /// a channel must not depend on which of its siblings were asked for in the same call.
    /// </para>
    /// </summary>
    public IReadOnlyCollection<BakeChannel>? Channels { get; init; }

    /// <summary>
    /// Optional sink for the phase split (Ledger Q-10). Purely an output: nothing in it reaches a pixel, a hash or
    /// a byte of the bake, and a null one (the default) costs a null check per phase. The library fills in decode,
    /// composite and encode; the caller fills in the two I/O phases around them.
    /// </summary>
    public BakeTimings? Timings { get; init; }
}

/// <summary>
/// What the compositor could not reproduce faithfully for one output channel
/// (ADR-005: best-effort with a structured report; refusal only for corrupt input).
/// </summary>
/// <param name="UnsupportedLayers">
/// <c>avatar_lad.xml</c> layers of this channel that were skipped for a reason other than "nothing worn":
/// a bundled resource or mask file missing, or an unknown local texture. Each entry is <c>layer: detail</c>.
/// </param>
/// <param name="MissingTextures">Texture ids a wearable referenced in this channel's slots that were not supplied in the request.</param>
/// <param name="Notes">One line per layer of the channel (<c>layer status: detail</c>) — the coverage evidence, for logs; never parsed.</param>
/// <param name="Refusals">
/// The fidelity gate's reasons for the whole outfit (the same list on every channel): wearable types the
/// compositor does not composite, texture slots no requested channel draws, duplicate body parts, missing
/// bundled resources. Empty means the outfit is one the compositor reproduces faithfully. The web-viewer
/// gateway refuses to send an appearance when this is non-empty; the simulator decides per ADR-005.
/// </param>
public sealed record FidelityReport(
    IReadOnlyList<string> UnsupportedLayers,
    IReadOnlyList<UUID> MissingTextures,
    IReadOnlyList<string> Notes,
    IReadOnlyList<string> Refusals)
{
    /// <summary>True when the gate found nothing to refuse and every referenced texture was supplied.</summary>
    public bool IsFaithful => Refusals.Count == 0 && MissingTextures.Count == 0 && UnsupportedLayers.Count == 0;
}

/// <summary>
/// One finished bake.
/// </summary>
/// <param name="Channel">Which output channel this is.</param>
/// <param name="J2kBytes">The composited texture, single-tile JPEG 2000 codestream, ready to store as a texture asset.</param>
/// <param name="InputHash">
/// Deterministic hash of the inputs that produced this bake, as computed by
/// <see cref="BakeHash.Compute(BakeChannel, BakeRequest)"/>. Stored alongside the asset so an unchanged
/// input set can be recognised without re-baking (ADR-004 <c>BakeHash:&lt;channel&gt;</c>).
/// </param>
/// <param name="Fidelity">What was and was not reproduced.</param>
public sealed record BakeResult(
    BakeChannel Channel,
    byte[] J2kBytes,
    string InputHash,
    FidelityReport Fidelity)
{
    /// <summary>
    /// True when **no layer of this channel drew anything** — every colour layer was skipped, so nothing ever
    /// reached the canvas. Set from the compositor's own per-layer decisions, never by inspecting pixels, and it
    /// is per channel: an outfit can have one undrawn channel and ten drawn ones.
    /// <para>
    /// **Drawn-but-transparent is not undrawn.** A layer that drew a fully transparent texture has drawn: Truly
    /// Bazar's bald hair draws a 4x4 transparent hair texture, and its all-transparent bake is the correct bake
    /// and must be stored. So is the case where an alpha wearable hides a whole region (IMG_INVISIBLE). Only the
    /// "every layer skipped" case sets this.
    /// </para>
    /// <para>
    /// It matters because such a bake is not blank: the layer set's alpha starts opaque
    /// (LLTexLayerSet::render clears to opaque black) and only a mask layer would have carved it, so an undrawn
    /// channel encodes as a solid near-black image. A caller that stored it would paint that over the avatar —
    /// the defect S1d found on an assetless skirt slot. Callers should not store or apply such a bake.
    /// </para>
    /// </summary>
    public bool NothingDrawn { get; init; }
}

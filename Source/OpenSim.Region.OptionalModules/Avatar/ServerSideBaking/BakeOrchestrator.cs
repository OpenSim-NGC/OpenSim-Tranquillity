using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using OpenSimNGC.Appearance.Baking;

namespace OpenSim.Region.OptionalModules.Avatar.ServerSideBaking;

/// <summary>
/// The scene-free part of the pipeline (Design Brief §4.2 steps 2, 4-6): resolve the agent's wearables and
/// textures from the asset service into a <see cref="BakeRequest"/>, run the backend, store the bakes as assets
/// and write the baked faces. Takes only the wearable set, the visual params, an asset service, a backend and
/// the <see cref="AvatarAppearance"/> to write into, so it is unit-tested with fakes and no ScenePresence.
/// Sending and the appearance save (steps 7) stay in <see cref="ServerSideBakingModule"/>.
///
/// <para>
/// S2 adds the ADR-004 reuse path: the per-channel input hash is computed from the wearables alone, before any
/// texture is fetched, so a channel whose inputs have not changed and whose stored asset still resolves costs
/// nothing at all — no asset fetch, no J2K decode, no composite, no encode, no store. Its face is still written
/// and the appearance is still sent, because the reason for the bake may be that a viewer has never seen it.
/// </para>
/// </summary>
public static class BakeOrchestrator
{
    /// <summary>Why a wearable or texture could not be used and which channels it takes down.</summary>
    public sealed record InputFailure(BakeChannel Channel, string Reason);

    /// <summary>The resolved inputs: the request the backend gets, plus the channels already lost to unusable inputs.</summary>
    public sealed record ResolvedInputs(BakeRequest Request, IReadOnlyList<InputFailure> Failures, IReadOnlyList<string> Notes)
    {
        public bool IsFailed(BakeChannel ch) { foreach (var f in Failures) if (f.Channel == ch) return true; return false; }
        public string FailureReason(BakeChannel ch) => string.Join("; ", Failures.Where(f => f.Channel == ch).Select(f => f.Reason));
    }

    /// <summary>
    /// The wearable half of <see cref="Resolve"/>: everything that can be known before a single texture is
    /// fetched. It is enough to compute every channel's <see cref="BakeHash"/>, which is what lets the reuse
    /// check run before the expensive half.
    /// </summary>
    public sealed record ResolvedWearables(
        IReadOnlyList<WearableInput> Wearables,
        IReadOnlyList<ParsedWearable> Parsed,
        IReadOnlyDictionary<int, float> VisualParams,
        IReadOnlyList<InputFailure> Failures,
        IReadOnlyList<string> Notes);

    private static readonly BakeChannel[] AllChannels = Enum.GetValues<BakeChannel>();
    private static readonly Dictionary<UUID, TextureInput> NoTextures = new();

    /// <summary>The TextureEntry face a channel is written to: <c>AvatarAppearance.BAKE_INDICES</c> in BakeChannel order.</summary>
    public static int FaceOf(BakeChannel ch) => AvatarAppearance.BAKE_INDICES[(int)ch];

    /// <summary>The asset name a stored bake carries (ADR-004): <c>bake:&lt;agent&gt;:&lt;channel&gt;</c>.</summary>
    public static string AssetNameFor(UUID agentId, BakeChannel ch) => $"bake:{agentId}:{ch.ToString().ToLowerInvariant()}";

    // ------------------------------------------------------------------ step 2 + 4: inputs

    /// <summary>
    /// Fetch every worn wearable asset (types 5/13) and parse it. A wearable that cannot be fetched or parsed
    /// fails the channels its type feeds (the shape feeds them all). Nothing else is refused (ADR-005). No
    /// texture is fetched here.
    /// </summary>
    public static ResolvedWearables ResolveWearables(AvatarWearable[] wearables, byte[] visualParams, IAssetService assets, TexLayerCompositor compositor, BakeTimings timings = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(compositor);
        var inputs = new List<WearableInput>();
        var parsed = new List<ParsedWearable>();
        var failures = new List<InputFailure>();
        var notes = new List<string>();

        if (wearables is not null)
        {
            for (var type = 0; type < wearables.Length; type++)
            {
                var slot = wearables[type];
                if (slot is null) continue;
                for (var j = 0; j < slot.Count; j++)
                {
                    var assetId = slot[j].AssetID;
                    if (assetId.IsZero())
                    {
                        // The slot is worn, there is just no asset behind it (typically a default system wearable
                        // item). That is still a worn wearable: the viewer counts wearables, not textures
                        // (LLTexLayerTemplate::updateWearableCache, lltexlayer.cpp:1615-1638), so it contributes its
                        // layers' morph masks with the avatar's own parameter values. Passing it on as an empty
                        // WearableInput is what the library expects (S1c, MORPH-MASK-PASS.md §2.4); dropping it here
                        // was Ledger Q-12. It carries no textures, so nothing is fetched for it.
                        inputs.Add(new WearableInput(UUID.Zero, type, ""));
                        parsed.Add(new ParsedWearable((WearableKind)type, "", new Dictionary<int, float>(), new Dictionary<TextureSlot, UUID>()));
                        notes.Add($"wearable type {(WearableKind)type}:{j} is worn with no asset; kept as a worn instance with no textures");
                        continue;
                    }
                    var t0 = BakeTimings.Now;
                    var asset = assets.Get(assetId.ToString());
                    timings?.AddAssetFetch(t0, asset?.Data?.Length ?? 0);
                    if (asset?.Data is not { Length: > 0 })
                    {
                        Fail(failures, ChannelsFedBy((WearableKind)type, compositor), $"wearable type {(WearableKind)type} asset {assetId} not found");
                        continue;
                    }
                    var text = Encoding.UTF8.GetString(asset.Data);
                    ParsedWearable pw;
                    try { pw = WearableParser.Parse(text); }
                    catch (FormatException ex)
                    {
                        Fail(failures, ChannelsFedBy((WearableKind)type, compositor), $"wearable type {(WearableKind)type} asset {assetId} unparseable: {ex.Message}");
                        continue;
                    }
                    inputs.Add(new WearableInput(assetId, type, text));
                    parsed.Add(pw with { Kind = (WearableKind)type });
                }
            }
        }

        // the presence's VisualParams, decoded through the parameter table as an overlay (the wearables' own values win)
        var overlay = new Dictionary<int, float>();
        if (visualParams is not null)
        {
            var list = VisualParamEncoder.SendList(compositor.Lad);
            if (visualParams.Length == list.Count)
            {
                for (var i = 0; i < list.Count; i++)
                    overlay[list[i].Id] = list[i].Min + visualParams[i] / 255f * (list[i].Max - list[i].Min);
            }
            else notes.Add($"VisualParams has {visualParams.Length} bytes, the parameter table {list.Count}; not overlaid");
        }

        return new ResolvedWearables(inputs, parsed, overlay, failures, notes);
    }

    /// <summary>
    /// Fetch, once each, every texture the given channels' layer sets can draw. A texture that cannot be fetched
    /// fails the channels among <paramref name="channels"/> whose layer sets draw its slot — never a channel that
    /// was not asked for, since such a channel is being reused and its face is not being rewritten.
    /// </summary>
    public static (IReadOnlyDictionary<UUID, TextureInput> Textures, IReadOnlyList<InputFailure> Failures) ResolveTextures(
        IReadOnlyList<ParsedWearable> parsed, IReadOnlyCollection<BakeChannel> channels, IAssetService assets, TexLayerCompositor compositor, BakeTimings timings = null)
    {
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(compositor);
        var textures = new Dictionary<UUID, TextureInput>();
        var failures = new List<InputFailure>();
        if (channels.Count == 0) return (textures, failures);

        var drawn = channels.SelectMany(compositor.SlotsOf).ToHashSet();
        foreach (var pw in parsed)
        {
            foreach (var (texSlot, id) in pw.Textures)
            {
                if (!drawn.Contains(texSlot)) continue;
                if (id.IsZero() || id == BakeConstants.DefaultAvatarTexture || textures.ContainsKey(id)) continue;
                var t0 = BakeTimings.Now;
                var asset = assets.Get(id.ToString());
                timings?.AddAssetFetch(t0, asset?.Data?.Length ?? 0);
                if (asset?.Data is not { Length: > 0 })
                {
                    Fail(failures, ChannelsDrawing(texSlot, compositor).Where(channels.Contains), $"texture {id} ({texSlot}) not found");
                    continue;
                }
                textures[id] = new TextureInput(id, asset.Data);
            }
        }
        return (textures, failures);
    }

    /// <summary>
    /// Wearables and every texture they reference, in one step: the S1 entry point, kept for callers that want a
    /// full bake with no reuse. <see cref="Run"/> uses the two halves separately so the reuse check can run
    /// between them.
    /// </summary>
    public static ResolvedInputs Resolve(AvatarWearable[] wearables, byte[] visualParams, IAssetService assets, TexLayerCompositor compositor, int bakeSize)
    {
        var w = ResolveWearables(wearables, visualParams, assets, compositor);
        var (textures, texFailures) = ResolveTextures(w.Parsed, AllChannels, assets, compositor);
        var failures = new List<InputFailure>(w.Failures);
        failures.AddRange(texFailures);
        return new ResolvedInputs(new BakeRequest(w.Wearables, w.VisualParams, textures, bakeSize), failures, w.Notes);
    }

    private static void Fail(List<InputFailure> failures, IEnumerable<BakeChannel> channels, string reason)
    {
        foreach (var ch in channels) failures.Add(new InputFailure(ch, reason));
    }

    /// <summary>Channels whose layer sets draw a slot owned by the wearable type; the shape (parameters only) feeds every channel.</summary>
    public static IEnumerable<BakeChannel> ChannelsFedBy(WearableKind kind, TexLayerCompositor compositor)
    {
        if (kind == WearableKind.Shape) return AllChannels;
        return AllChannels.Where(ch => compositor.SlotsOf(ch).Any(s => TexLayerCompositor.WearableOf(s) == kind));
    }

    /// <summary>Channels whose layer sets draw the given texture slot.</summary>
    public static IEnumerable<BakeChannel> ChannelsDrawing(TextureSlot slot, TexLayerCompositor compositor)
        => AllChannels.Where(ch => compositor.SlotsOf(ch).Contains(slot));

    // ------------------------------------------------------------------ step 3: the reuse decision (ADR-004)

    /// <summary>What the reuse check decided for one run.</summary>
    /// <param name="Reused">Channels whose stored bake is being kept, with the asset and hash to write back.</param>
    /// <param name="ToBake">Channels that must be composited.</param>
    /// <param name="Hashes">The freshly computed input hash of every channel the outfit needs.</param>
    /// <param name="Notes">One line per channel that could have been reused but was not, and why.</param>
    public sealed record ReuseDecision(
        IReadOnlyDictionary<BakeChannel, StoredBake> Reused,
        IReadOnlyList<BakeChannel> ToBake,
        IReadOnlyDictionary<BakeChannel, string> Hashes,
        IReadOnlyList<string> Notes);

    /// <summary>
    /// Decide, per channel, whether the stored bake can be kept (ADR-004). A channel is reused when
    /// <list type="number">
    ///   <item>the index records a bake and a hash for it, and</item>
    ///   <item>the stored index was written at the bake size now in force, and</item>
    ///   <item>the hash of the current inputs equals the stored hash, and</item>
    ///   <item>the stored asset still resolves in the asset service.</item>
    /// </list>
    /// (2) is belt and braces: <see cref="BakeHash"/> already folds the size into the hash, so a
    /// <c>[Appearance] BakeSize</c> change invalidates every channel through (3) as well. (4) is the rule that a
    /// stored hash whose asset has vanished — deleted by an operator, lost in an asset-service migration — must
    /// never be trusted: the face would point at nothing and the avatar would go untextured for good.
    /// </summary>
    public static ReuseDecision DecideReuse(IReadOnlyList<BakeChannel> needed, BakeRequest hashRequest, BakeIndex index,
        IAssetService assets, TexLayerCompositor compositor, int bakeSize)
    {
        ArgumentNullException.ThrowIfNull(assets);
        var hashes = new Dictionary<BakeChannel, string>(needed.Count);
        foreach (var ch in needed) hashes[ch] = BakeHash.Compute(ch, hashRequest, compositor);

        var notes = new List<string>();
        var candidates = new List<BakeChannel>();
        foreach (var ch in needed)
        {
            if (!index.TryGet(ch, out var stored)) { notes.Add($"{ch}: no stored bake"); continue; }
            if (index.Size != bakeSize) { notes.Add($"{ch}: stored at size {index.Size}, now {bakeSize}"); continue; }
            if (!string.Equals(stored.Hash, hashes[ch], StringComparison.Ordinal)) { notes.Add($"{ch}: inputs changed"); continue; }
            candidates.Add(ch);
        }

        var reused = new Dictionary<BakeChannel, StoredBake>();
        if (candidates.Count > 0)
        {
            var ids = candidates.Select(ch => index.Bakes[ch].AssetId.ToString()).ToArray();
            var exists = Exists(assets, ids);
            for (var i = 0; i < candidates.Count; i++)
            {
                var ch = candidates[i];
                if (exists[i]) reused[ch] = index.Bakes[ch];
                else notes.Add($"{ch}: stored asset {ids[i]} has vanished from the asset service");
            }
        }

        var toBake = needed.Where(ch => !reused.ContainsKey(ch)).ToList();
        return new ReuseDecision(reused, toBake, hashes, notes);
    }

    /// <summary>
    /// Does each asset still resolve? One <see cref="IAssetService.AssetsExist"/> call where the service answers
    /// it (RegionAssetConnectorModule returns null for a mixed local/HG batch), otherwise one metadata fetch each.
    /// Anything that cannot be answered counts as absent, which costs a re-bake and never a broken face.
    /// </summary>
    private static bool[] Exists(IAssetService assets, string[] ids)
    {
        bool[] exists = null;
        try { exists = assets.AssetsExist(ids); }
        catch (Exception) { }
        if (exists is not null && exists.Length == ids.Length) return exists;

        exists = new bool[ids.Length];
        for (var i = 0; i < ids.Length; i++)
        {
            try { exists[i] = assets.GetMetadata(ids[i]) is not null; }
            catch (Exception) { exists[i] = false; }
        }
        return exists;
    }

    // ------------------------------------------------------------------ step 6: store + faces + supersede

    /// <summary>
    /// Store each bake as a texture asset (ADR-004 marker: name <c>bake:&lt;agent&gt;:&lt;channel&gt;</c>, description = input
    /// hash, not temporary, not local, creator = agent) and write its UUID to the channel's baked face. Channels in
    /// <paramref name="reused"/> keep their stored asset and have their face written to it without any store.
    /// Channels in <paramref name="inputs"/>'s failures and channels the backend produced nothing for keep their
    /// current face.
    ///
    /// <para>
    /// Supersede (ADR-004): once a channel's new asset is confirmed stored <i>and</i> its face has been moved to
    /// it, the asset the channel held before is deleted. Never before the store — a store that fails must leave
    /// the old bake serving — and never an asset any baked face still points at.
    /// </para>
    /// </summary>
    public static IReadOnlyList<ChannelOutcome> StoreAndApply(IReadOnlyList<BakeResult> results, ResolvedInputs inputs, UUID agentId,
        IAssetService assets, AvatarAppearance appearance,
        IReadOnlyDictionary<BakeChannel, StoredBake> reused = null, IReadOnlyDictionary<BakeChannel, StoredBake> previous = null,
        List<UUID> superseded = null, BakeTimings timings = null)
    {
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(assets);
        ArgumentNullException.ThrowIfNull(appearance);
        var byChannel = results.ToDictionary(r => r.Channel);
        var outcomes = new List<ChannelOutcome>(AllChannels.Length);
        var empty = new FidelityReport(Array.Empty<string>(), Array.Empty<UUID>(), Array.Empty<string>(), Array.Empty<string>());
        foreach (var ch in AllChannels)
        {
            // Reuse first: a reused channel was never fetched for, so it cannot have acquired a texture failure,
            // and its stored bake is by definition the bake its unchanged inputs produce.
            if (reused is not null && reused.TryGetValue(ch, out var keep))
            {
                appearance.Texture.CreateFace((uint)FaceOf(ch)).TextureID = keep.AssetId;
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Reused, keep.AssetId, keep.Hash, "inputs unchanged", empty));
                continue;
            }
            byChannel.TryGetValue(ch, out var result);
            if (inputs.IsFailed(ch))
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, result?.InputHash ?? "", inputs.FailureReason(ch), result?.Fidelity ?? empty));
                continue;
            }
            if (result is null)
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Skipped, UUID.Zero, "", "nothing worn for this channel", empty));
                continue;
            }
            if (result.J2kBytes is not { Length: > 0 })
            {
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, result.InputHash, "backend returned no bytes", result.Fidelity));
                continue;
            }
            if (result.NothingDrawn)
            {
                // Every layer of this channel was skipped, so the bake is whatever the canvas was cleared to —
                // opaque, not blank (S1d measured 96.5% opaque near-black on an assetless skirt slot). Storing it
                // and writing the face would paint that over the avatar, replacing a viewer bake that may be
                // perfectly good. The face keeps what it has. Note this is a fact about the layer decisions, not
                // the pixels: a channel that drew a fully transparent texture (a bald hair) is stored normally.
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Skipped, UUID.Zero, result.InputHash, "nothing drawn for this channel", result.Fidelity));
                continue;
            }
            var asset = new AssetBase(UUID.Random(), AssetNameFor(agentId, ch), (sbyte)AssetType.Texture, agentId.ToString())
            {
                Data = result.J2kBytes,
                Description = result.InputHash,
                Temporary = false,
                Local = false,
            };
            var tStore = BakeTimings.Now;
            var storedId = assets.Store(asset);
            timings?.AddAssetStore(tStore);
            if (string.IsNullOrEmpty(storedId) || !UUID.TryParse(storedId, out var id) || id.IsZero())
            {
                // the store failed: the channel's previous bake, if any, is still the one its face points at and
                // is emphatically not superseded.
                outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, result.InputHash, "asset service refused the bake", result.Fidelity));
                continue;
            }
            appearance.Texture.CreateFace((uint)FaceOf(ch)).TextureID = id;
            outcomes.Add(new ChannelOutcome(ch, ChannelStatus.Baked, id, result.InputHash, "", result.Fidelity));
            Supersede(assets, appearance, previous, ch, id, superseded, timings);
        }
        return outcomes;
    }

    /// <summary>
    /// Delete the asset the channel held before this run, now that the new one is stored and the face has moved.
    /// Refuses to delete an asset that any baked face still points at — including the one just written, and
    /// including a face of some other channel that was left alone this run.
    /// </summary>
    private static void Supersede(IAssetService assets, AvatarAppearance appearance, IReadOnlyDictionary<BakeChannel, StoredBake> previous,
        BakeChannel ch, UUID newId, List<UUID> superseded, BakeTimings timings = null)
    {
        if (previous is null || !previous.TryGetValue(ch, out var old)) return;
        if (old.AssetId.IsZero() || old.AssetId == newId) return;
        foreach (var other in AllChannels)
        {
            var face = appearance.Texture.FaceTextures[FaceOf(other)];
            if (face is not null && face.TextureID == old.AssetId) return;
        }
        try
        {
            var t0 = BakeTimings.Now;
            var gone = assets.Delete(old.AssetId.ToString());
            timings?.AddAssetStore(t0, deleted: true);
            if (gone) superseded?.Add(old.AssetId);
        }
        catch (Exception) { }
    }

    // ------------------------------------------------------------------ the whole scene-free run

    /// <summary>A full bake with no persistence: every channel is composited and nothing is written to the avatar service.</summary>
    public static BakeOutcome Run(UUID agentId, BakeReason reason, AvatarWearable[] wearables, byte[] visualParams, AvatarAppearance appearance,
        IAssetService assets, IBakeBackend backend, TexLayerCompositor compositor, int bakeSize, CancellationToken ct)
        => Run(agentId, reason, wearables, visualParams, appearance, assets, null, backend, compositor, bakeSize, 0, ct);

    /// <summary>
    /// Resolve, decide what can be reused, bake the rest, store, write faces, supersede, and record the bake index
    /// in the avatar service. Sending is the caller's.
    /// </summary>
    /// <param name="avatars">The avatar service holding the ADR-004 index, or null for a run that neither reads nor writes it.</param>
    /// <param name="cofVersion">The Current Outfit folder's version at bake time; stored, not compared (the hash is the stronger test).</param>
    public static BakeOutcome Run(UUID agentId, BakeReason reason, AvatarWearable[] wearables, byte[] visualParams, AvatarAppearance appearance,
        IAssetService assets, IAvatarService avatars, IBakeBackend backend, TexLayerCompositor compositor, int bakeSize, int cofVersion,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var timings = new BakeTimings();
        var w = ResolveWearables(wearables, visualParams, assets, compositor, timings);
        ct.ThrowIfCancellationRequested();

        // the channels this outfit needs at all, and their input hashes — computed from the wearables alone, so
        // the reuse decision happens before a single texture is fetched
        var needed = SkiaBakeBackend.ChannelsFor(w.Parsed);
        var hashRequest = new BakeRequest(w.Wearables, w.VisualParams, NoTextures, bakeSize);
        var index = BakeIndex.Read(avatars, agentId);
        var reuse = DecideReuse(needed, hashRequest, index, assets, compositor, bakeSize);
        ct.ThrowIfCancellationRequested();

        var (textures, texFailures) = ResolveTextures(w.Parsed, reuse.ToBake, assets, compositor, timings);
        var failures = new List<InputFailure>(w.Failures);
        failures.AddRange(texFailures);
        var notes = new List<string>(w.Notes);
        notes.AddRange(reuse.Notes);
        var inputs = new ResolvedInputs(
            new BakeRequest(w.Wearables, w.VisualParams, textures, bakeSize) { Channels = reuse.ToBake, Timings = timings },
            failures, notes);
        ct.ThrowIfCancellationRequested();

        IReadOnlyList<BakeResult> results;
        try
        {
            results = reuse.ToBake.Count == 0
                ? Array.Empty<BakeResult>()
                : backend.BakeAsync(inputs.Request, ct).GetAwaiter().GetResult();
        }
        catch (OperationCanceledException) { throw; }
        catch (ArgumentException ex)
        {
            // ADR-005: corrupt input is the one refusal; every channel keeps its existing face and the index is
            // left exactly as it was.
            var all = AllChannels.Select(ch => new ChannelOutcome(ch, ChannelStatus.Failed, UUID.Zero, "", $"backend refused the inputs: {ex.Message}",
                new FidelityReport(Array.Empty<string>(), Array.Empty<UUID>(), Array.Empty<string>(), Array.Empty<string>()))).ToList();
            return new BakeOutcome(agentId, reason, all, sw.ElapsedMilliseconds) { Timings = timings };
        }

        var superseded = new List<UUID>();
        var outcomes = StoreAndApply(results, inputs, agentId, assets, appearance, reuse.Reused, index.Bakes, superseded, timings);

        // the index: one row pair per channel that now has a live bake, plus the three scalars
        var live = outcomes
            .Where(o => o.Status is ChannelStatus.Baked or ChannelStatus.Reused && !o.AssetId.IsZero())
            .Select(o => new KeyValuePair<BakeChannel, StoredBake>(o.Channel, new StoredBake(o.AssetId, o.InputHash)))
            .ToList();
        var indexWritten = live.Count > 0 && BakeIndex.Write(avatars, agentId, live, cofVersion, bakeSize, DateTime.UtcNow);

        return new BakeOutcome(agentId, reason, outcomes, sw.ElapsedMilliseconds)
        {
            Superseded = superseded,
            IndexWritten = indexWritten,
            Notes = notes,
            Timings = timings,
        };
    }
}

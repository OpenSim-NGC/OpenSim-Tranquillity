using System.Diagnostics;

namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// Where a bake's time goes (Ledger Q-10). A caller that wants the breakdown creates one, hands it to the run
/// through <see cref="BakeRequest.Timings"/>, and reads it afterwards; a caller that does not pass one costs two
/// null checks per phase.
///
/// <para>
/// Five phases, which between them account for a bake end to end:
/// <list type="bullet">
///   <item><see cref="AssetFetch"/> — reading the wearable and texture assets out of the asset service. Measured by
///     the orchestrator, not the library: the library performs no I/O.</item>
///   <item><see cref="Decode"/> — turning the fetched JPEG 2000 texture bytes into planes.</item>
///   <item><see cref="Composite"/> — the layer-set render, per channel.</item>
///   <item><see cref="Encode"/> — the five-component JPEG 2000 encode, per channel.</item>
///   <item><see cref="AssetStore"/> — writing the finished bakes back (and the supersede deletes).</item>
/// </list>
/// Everything else — parsing wearables, hashing, reading the bake index, writing the faces — is the remainder, and
/// on any measurement so far it is noise.
/// </para>
///
/// <para>
/// It is a sink, never an input: nothing here reaches a pixel, a hash or a byte of output. Counters are updated
/// with <see cref="Interlocked"/> so a backend that composites channels in parallel stays correct.
/// </para>
/// </summary>
public sealed class BakeTimings
{
    private long _fetchTicks, _decodeTicks, _compositeTicks, _encodeTicks, _storeTicks;
    private long _assetsFetched, _fetchedBytes, _texturesDecoded, _decodedPixels;
    private long _channelsComposited, _channelsEncoded, _assetsStored, _storedBytes, _assetsDeleted;

    /// <summary>A timestamp to pass to <see cref="Since"/>. Cheap; no allocation.</summary>
    public static long Now => Stopwatch.GetTimestamp();

    /// <summary>Elapsed time since a <see cref="Now"/> timestamp.</summary>
    public static TimeSpan Since(long start) => Stopwatch.GetElapsedTime(start);

    public void AddAssetFetch(long start, long bytes)
    {
        Interlocked.Add(ref _fetchTicks, Since(start).Ticks);
        Interlocked.Increment(ref _assetsFetched);
        Interlocked.Add(ref _fetchedBytes, bytes);
    }

    public void AddDecode(long start, long pixels)
    {
        Interlocked.Add(ref _decodeTicks, Since(start).Ticks);
        Interlocked.Increment(ref _texturesDecoded);
        Interlocked.Add(ref _decodedPixels, pixels);
    }

    public void AddComposite(long start)
    {
        Interlocked.Add(ref _compositeTicks, Since(start).Ticks);
        Interlocked.Increment(ref _channelsComposited);
    }

    public void AddEncode(long start, long bytes)
    {
        Interlocked.Add(ref _encodeTicks, Since(start).Ticks);
        Interlocked.Increment(ref _channelsEncoded);
        Interlocked.Add(ref _storedBytes, bytes);
    }

    public void AddAssetStore(long start, bool deleted = false)
    {
        Interlocked.Add(ref _storeTicks, Since(start).Ticks);
        if (deleted) Interlocked.Increment(ref _assetsDeleted);
        else Interlocked.Increment(ref _assetsStored);
    }

    public TimeSpan AssetFetch => TimeSpan.FromTicks(Interlocked.Read(ref _fetchTicks));
    public TimeSpan Decode => TimeSpan.FromTicks(Interlocked.Read(ref _decodeTicks));
    public TimeSpan Composite => TimeSpan.FromTicks(Interlocked.Read(ref _compositeTicks));
    public TimeSpan Encode => TimeSpan.FromTicks(Interlocked.Read(ref _encodeTicks));
    public TimeSpan AssetStore => TimeSpan.FromTicks(Interlocked.Read(ref _storeTicks));

    /// <summary>The five phases added together. Always less than the run's wall clock; the difference is the remainder.</summary>
    public TimeSpan Accounted => AssetFetch + Decode + Composite + Encode + AssetStore;

    public int AssetsFetched => (int)Interlocked.Read(ref _assetsFetched);
    public long FetchedBytes => Interlocked.Read(ref _fetchedBytes);
    public int TexturesDecoded => (int)Interlocked.Read(ref _texturesDecoded);
    public long DecodedPixels => Interlocked.Read(ref _decodedPixels);
    public int ChannelsComposited => (int)Interlocked.Read(ref _channelsComposited);
    public int ChannelsEncoded => (int)Interlocked.Read(ref _channelsEncoded);
    public int AssetsStored => (int)Interlocked.Read(ref _assetsStored);
    public int AssetsDeleted => (int)Interlocked.Read(ref _assetsDeleted);
    public long StoredBytes => Interlocked.Read(ref _storedBytes);

    /// <summary>The one-line split, milliseconds: <c>fetch=… decode=… composite=… encode=… store=…</c>.</summary>
    public string Summary =>
        $"fetch={AssetFetch.TotalMilliseconds:F0} ({AssetsFetched} assets, {FetchedBytes / 1024} KiB), "
        + $"decode={Decode.TotalMilliseconds:F0} ({TexturesDecoded} textures), "
        + $"composite={Composite.TotalMilliseconds:F0} ({ChannelsComposited} channels), "
        + $"encode={Encode.TotalMilliseconds:F0} ({ChannelsEncoded} channels, {StoredBytes / 1024} KiB), "
        + $"store={AssetStore.TotalMilliseconds:F0} ({AssetsStored} stored, {AssetsDeleted} superseded)";
}

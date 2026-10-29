namespace OpenSimNGC.Appearance.Baking;

/// <summary>
/// A bake compositor. Implementations take a complete <see cref="BakeRequest"/>
/// and return one <see cref="BakeResult"/> per channel that the worn wearables
/// touch. Implementations must be pure with respect to their inputs: same request,
/// same bytes, same hashes.
/// </summary>
public interface IBakeBackend
{
    /// <summary>
    /// Composite every affected channel for the given request.
    /// </summary>
    /// <param name="r">The complete bake input.</param>
    /// <param name="ct">Cancellation token; a cancelled bake returns nothing and stores nothing.</param>
    /// <returns>
    /// One result per channel actually produced. Channels with no contributing
    /// layer (for example <see cref="BakeChannel.Skirt"/> with no skirt worn) are omitted.
    /// </returns>
    /// <exception cref="OperationCanceledException">If <paramref name="ct"/> is cancelled.</exception>
    /// <exception cref="ArgumentException">If the request is malformed (corrupt wearable text or undecodable texture bytes).</exception>
    Task<IReadOnlyList<BakeResult>> BakeAsync(BakeRequest r, CancellationToken ct);
}

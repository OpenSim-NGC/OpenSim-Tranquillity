using System;
using System.Threading;
using OpenMetaverse;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// AIS-SEC-3. Serialises mutations that target the same <c>(agent, folder)</c>, process-wide.
///
/// <para><b>The defect this exists for.</b> <see cref="AisSlam.Run"/> snapshots a folder's links, creates the
/// wanted set, then deletes the snapshot. Create-before-delete is the right failure bias — the folder never holds
/// fewer links than it started with (A3) — but nothing ordered two mutations on one folder. Two slams that both
/// snapshot the old links, both create their own set and both delete only what they saw leave the folder holding
/// the <b>union</b> of two outfits. A purge racing a slam is the same shape from the other side: it decides what
/// to delete, the slam replaces all of it, and the purge's closing re-read then blames the slam's new links for a
/// partial purge it never caused.
///
/// <para>The viewer produces this on its own — an outfit change issued while a previous one is still in flight, or
/// two sessions — and one agent can hold caps from more than one region <i>in this process</i>, so it is not only
/// a cross-process concern.</para></para>
///
/// <para><b>Striped, not per-key.</b> A fixed array of semaphores allocates nothing per request, has nothing to
/// reference-count and nothing to leak, where a <c>ConcurrentDictionary&lt;key, SemaphoreSlim&gt;</c> must be
/// swept or it grows for the life of the process. The cost is that two unrelated keys can share a stripe and
/// serialise against each other for the length of one mutation. That is never <i>wrong</i>, only occasionally
/// slower, and with <see cref="Stripes"/> entries it is rare. <see cref="StripeOf"/> is exposed so a test can
/// assert that the keys it uses do not collide, rather than depend on luck.</para>
///
/// <para><b>Static, and that is the point.</b> Every agent gets a fresh <see cref="AisHandler"/> per cap
/// registration, and an agent present in two regions of one simulator holds two of them over the same inventory.
/// A per-handler lock would order nothing between those.</para>
///
/// <para><b>What this is not.</b> It is the single-process answer. It cannot order a mutation in this simulator
/// against one in another simulator or in Robust, because there is no shared lock and
/// <c>IInventoryService</c> offers no transaction and no batch write (tree state T5, Ledger A-R2/A-Q10). Phase 2,
/// which hosts these routes on Robust, needs a real inventory transaction — a service-side operation that
/// replaces a folder's links in one call — and until that exists the cross-process window stands. Saying so here
/// because a lock is exactly the kind of thing that gets mistaken for a full fix.</para>
/// </summary>
public static class AisFolderLocks
{
    /// <summary>
    /// How long a mutation waits for the folder before giving up. Generous next to a slam (a handful of inventory
    /// round trips) and short enough that a wedged request does not hold an HTTP thread for a minute.
    /// </summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    /// <summary>Stripe count. A power of two, far above the concurrent inventory mutations one simulator sees.</summary>
    public const int Stripes = 256;

    private static readonly SemaphoreSlim[] s_locks = Build();

    private static SemaphoreSlim[] Build()
    {
        var locks = new SemaphoreSlim[Stripes];
        for (int i = 0; i < locks.Length; i++) locks[i] = new SemaphoreSlim(1, 1);
        return locks;
    }

    /// <summary>
    /// Which stripe a key lands on. Public so the concurrency tests can assert their keys do not collide; see the
    /// remarks on the class.
    ///
    /// <para><b>The combined hash is avalanched before it is truncated, and that is not decoration.</b> Measured
    /// on this tree's <c>UUID</c>: <c>GetHashCode()</c> has a low byte of <c>0x80</c> for every UUID of the form
    /// <c>xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx</c> - the variant nibble lands there - so four structured ids that
    /// differ everywhere a human looks gave the identical low 8 bits, and taking those bits as a bucket index put
    /// <b>every</b> such key on stripe 0. Random UUIDs spread fine, which is exactly what makes it a trap: it
    /// would have looked correct in production and collapsed to a single global lock for any sequential or
    /// hand-built id family, of which this tree has several. The finalising mix below moves every input bit into
    /// the low ones, so the truncation is sound whatever the ids look like.</para>
    /// </summary>
    public static int StripeOf(UUID agentId, UUID folderId)
    {
        unchecked
        {
            uint h = (uint)((agentId.GetHashCode() * 397) ^ folderId.GetHashCode());
            h ^= h >> 16;
            h *= 0x7feb352d;
            h ^= h >> 15;
            h *= 0x846ca68b;
            h ^= h >> 16;
            return (int)(h & (Stripes - 1));   // Stripes is a power of two, so the mask is exact
        }
    }

    /// <summary>
    /// Takes the folder. Returns false on timeout, and the caller must then answer and <b>not</b> proceed — a
    /// mutation that ran unlocked because the lock was busy would be the defect with extra steps.
    /// </summary>
    public static bool TryEnter(UUID agentId, UUID folderId)
        => s_locks[StripeOf(agentId, folderId)].Wait(Timeout);

    public static void Exit(UUID agentId, UUID folderId)
        => s_locks[StripeOf(agentId, folderId)].Release();
}

using System.Collections.Generic;
using OpenMetaverse;

namespace OpenSim.Region.ClientStack.LindenCaps.AIS;

/// <summary>
/// The operations the LL viewer drives against the InventoryAPIv3 / LibraryAPIv3 caps, one per row of
/// Docs/feature/ais-v3/AIS-V3-SPEC.md §1a (viewer llaisapi.h COMMAND_TYPE plus the string-identifier fetch).
/// </summary>
public enum AisOperation
{
    /// <summary>The verb + path pair is not one the spec defines.</summary>
    Unknown,
    /// <summary>POST /category/{parent}?tid= — create categories / items / links under a parent.</summary>
    CreateInventory,
    /// <summary>PUT /category/{id}/links?tid= — replace the folder's links.</summary>
    SlamFolder,
    /// <summary>DELETE /category/{id}.</summary>
    RemoveCategory,
    /// <summary>DELETE /item/{id}.</summary>
    RemoveItem,
    /// <summary>DELETE /category/{id}/children.</summary>
    PurgeDescendents,
    /// <summary>PATCH /category/{id}.</summary>
    UpdateCategory,
    /// <summary>PATCH /item/{id}.</summary>
    UpdateItem,
    /// <summary>GET /item/{id}.</summary>
    FetchItem,
    /// <summary>GET /category/{id}/children?depth=N (no children= list).</summary>
    FetchCategoryChildren,
    /// <summary>GET /category/{id}/categories?depth=N.</summary>
    FetchCategoryCategories,
    /// <summary>GET /category/{id}/children?depth=N&amp;children=a,b,c.</summary>
    FetchCategorySubset,
    /// <summary>GET /category/current/links — the Current Outfit folder's links.</summary>
    FetchCOF,
    /// <summary>GET /category/{id}/links.</summary>
    FetchCategoryLinks,
    /// <summary>GET /orphans.</summary>
    FetchOrphans,
    /// <summary>COPY /category/{source}?tid=[,depth=0] with a destination (library cap).</summary>
    CopyCategory,
}

/// <summary>Classification of the operations, kept beside the enum so a new one is hard to forget.</summary>
public static class AisOperations
{
    /// <summary>
    /// True for the operations that change inventory — everything the viewer applies a delta envelope from. The
    /// fetches are excluded deliberately: their bodies are whole inventory listings, and logging those would bury
    /// the mutations that matter.
    /// </summary>
    public static bool IsMutation(AisOperation operation) => operation switch
    {
        AisOperation.CreateInventory or
        AisOperation.SlamFolder or
        AisOperation.RemoveCategory or
        AisOperation.RemoveItem or
        AisOperation.PurgeDescendents or
        AisOperation.UpdateCategory or
        AisOperation.UpdateItem or
        AisOperation.CopyCategory => true,
        _ => false,
    };
}

/// <summary>
/// One parsed request: what the viewer asked for, with the ids and query values the spec defines. Never holds
/// anything scene-bound (Ledger P-2).
/// </summary>
public sealed record AisRoute(
    AisOperation Operation,
    string Verb,
    string Path,
    /// <summary>The category or item id from the path; UUID.Zero for aliases and id-less routes.</summary>
    UUID Id,
    /// <summary>The raw path segment the id came from ("current" for the COF alias), or "".</summary>
    string Identifier,
    /// <summary>True when the id segment is an alias such as "current" rather than a UUID.</summary>
    bool IsAlias,
    /// <summary>tid query value, or UUID.Zero.</summary>
    UUID Tid,
    /// <summary>depth query value; -1 when absent.</summary>
    int Depth,
    /// <summary>children= ids for FetchCategorySubset; empty otherwise.</summary>
    IReadOnlyList<UUID> Children,
    /// <summary>Every query key and its raw value, for anything the spec does not name (e.g. simulate).</summary>
    IReadOnlyDictionary<string, string> Query)
{
    public static readonly AisRoute None = new(AisOperation.Unknown, "", "", UUID.Zero, "", false, UUID.Zero, -1, System.Array.Empty<UUID>(), new Dictionary<string, string>());
}

/// <summary>
/// Parses verb + path + query into an <see cref="AisRoute"/>. The path is relative to the cap URL (everything after
/// the cap's own path segment); the parser is tolerant of a leading slash and of the cap prefix being present when
/// <paramref name="capPath"/> is supplied. Pure; unit-tested against every URL shape in AIS-V3-SPEC.md §1a.
/// </summary>
public static class AisRouter
{
    public const string CurrentOutfitAlias = "current";

    public static AisRoute Parse(string verb, string pathAndQuery, string capPath = "")
    {
        if (string.IsNullOrEmpty(verb) || pathAndQuery is null) return AisRoute.None;
        verb = verb.ToUpperInvariant();

        var q = pathAndQuery.IndexOf('?');
        var path = q >= 0 ? pathAndQuery[..q] : pathAndQuery;
        var queryText = q >= 0 ? pathAndQuery[(q + 1)..] : "";

        if (capPath.Length > 0 && path.StartsWith(capPath, System.StringComparison.OrdinalIgnoreCase))
            path = path[capPath.Length..];
        path = path.Trim('/');

        var query = ParseQuery(queryText);
        var segments = path.Length == 0 ? System.Array.Empty<string>() : path.Split('/', System.StringSplitOptions.RemoveEmptyEntries);

        // tid: the viewer's COPY appends ",depth=0" to the tid value itself (llaisapi.cpp:278)
        var tid = UUID.Zero;
        var depth = -1;
        if (query.TryGetValue("tid", out var tidText))
        {
            var comma = tidText.IndexOf(',');
            if (comma >= 0)
            {
                var extra = tidText[(comma + 1)..];
                tidText = tidText[..comma];
                var eq = extra.IndexOf('=');
                if (eq > 0) query[extra[..eq]] = extra[(eq + 1)..];
            }
            UUID.TryParse(tidText, out tid);
        }
        if (query.TryGetValue("depth", out var depthText) && int.TryParse(depthText, out var d)) depth = d;
        var children = new List<UUID>();
        if (query.TryGetValue("children", out var childrenText))
            foreach (var part in childrenText.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries))
                if (UUID.TryParse(part, out var cid)) children.Add(cid);

        AisRoute Route(AisOperation op, string identifier, UUID id, bool alias)
            => new(op, verb, "/" + path, id, identifier, alias, tid, depth, children, query);

        if (segments.Length == 1 && segments[0].Equals("orphans", System.StringComparison.OrdinalIgnoreCase))
            return verb == "GET" ? Route(AisOperation.FetchOrphans, "", UUID.Zero, false) : Route(AisOperation.Unknown, "", UUID.Zero, false);

        if (segments.Length is < 2 or > 3) return Route(AisOperation.Unknown, "", UUID.Zero, false);

        var collection = segments[0].ToLowerInvariant();
        var identifier = segments[1];
        var isAlias = !UUID.TryParse(identifier, out var objectId);
        if (isAlias && !identifier.Equals(CurrentOutfitAlias, System.StringComparison.OrdinalIgnoreCase))
            return Route(AisOperation.Unknown, identifier, UUID.Zero, true);
        if (isAlias) objectId = UUID.Zero;
        var sub = segments.Length == 3 ? segments[2].ToLowerInvariant() : "";

        AisOperation op = AisOperation.Unknown;
        if (collection == "item" && sub.Length == 0)
        {
            op = verb switch { "GET" => AisOperation.FetchItem, "DELETE" => AisOperation.RemoveItem, "PATCH" => AisOperation.UpdateItem, _ => AisOperation.Unknown };
        }
        else if (collection == "category")
        {
            op = (verb, sub) switch
            {
                ("POST", "") => AisOperation.CreateInventory,
                ("DELETE", "") => AisOperation.RemoveCategory,
                ("PATCH", "") => AisOperation.UpdateCategory,
                ("COPY", "") => AisOperation.CopyCategory,
                ("PUT", "links") => AisOperation.SlamFolder,
                ("DELETE", "children") => AisOperation.PurgeDescendents,
                ("GET", "children") => children.Count > 0 ? AisOperation.FetchCategorySubset : AisOperation.FetchCategoryChildren,
                ("GET", "categories") => AisOperation.FetchCategoryCategories,
                ("GET", "links") => isAlias ? AisOperation.FetchCOF : AisOperation.FetchCategoryLinks,
                _ => AisOperation.Unknown,
            };
        }
        return Route(op, identifier, objectId, isAlias);
    }

    private static Dictionary<string, string> ParseQuery(string queryText)
    {
        var dict = new Dictionary<string, string>(System.StringComparer.OrdinalIgnoreCase);
        if (queryText.Length == 0) return dict;
        foreach (var pair in queryText.Split('&', System.StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var key = eq >= 0 ? pair[..eq] : pair;
            var value = eq >= 0 ? pair[(eq + 1)..] : "";
            dict[System.Uri.UnescapeDataString(key)] = System.Uri.UnescapeDataString(value);
        }
        return dict;
    }
}

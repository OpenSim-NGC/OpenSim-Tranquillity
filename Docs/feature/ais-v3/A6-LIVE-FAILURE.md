# A6 — the first live run failed, and why

**Date:** 2026-09-04. **Region:** Ebony. **Viewer:** Firestorm 7.2.5. **Avatar:** Truly Bazar.
**Symptom:** every inventory folder empty and stayed empty; "worn folder could not be found so clothing could not
be downloaded"; avatar a cloud. The region log shows the startup line once and then **no AIS lines at all**.

## The short version

**The caps were advertised correctly. Every request to them then 404'd inside the HTTP server, before the handler
was ever entered.** AIS is the first capability in this tree whose URLs carry sub-paths, and it was registered in
the dictionary that only matches a path *exactly*.

## Why "no AIS lines" proved nothing

The request path had **no logging whatsoever** — not at registration, not at handler entry, not on error. So
"zero AIS lines" was equally consistent with "no request arrived" and with "every request arrived and was
rejected before reaching us". It could not distinguish them, which is why item (d) of the brief exists and why
the fix adds both log points.

The in-world symptom does distinguish them, and it points the other way from the brief's leading hypothesis. **If
the caps had been missing from the seed response, nothing would have broken**: `AISAPI::isAvailable()` would have
returned false and Firestorm would have used the legacy `FetchInventoryDescendents2` path exactly as it did the
day before. Inventory came up empty *because* the viewer got the caps, switched everything to AIS, and then got
nothing back.

## (a) Where AIS registers, and the side-by-side that matters

Registration itself is correct and identical to a cap that works.

| | `FetchInventory2Module` (works) | `AISv3Module` (failed) |
|---|---|---|
| Subscribes | `s.EventManager.OnRegisterCaps += RegisterCaps` in `RegionLoaded` (`FetchInventory2Module.cs:112`) | same, in `RegionLoaded` (`AISv3Module.cs`) |
| Registers | `caps.RegisterSimpleHandler("FetchInventory2", new SimpleOSDMapHandler("POST", "/" + UUID.Random(), …))` (`:141-149`) | `caps.RegisterSimpleHandler(CapName, new AisHandler("/" + UUID.Random(), …))` |
| Reaches the seed? | yes | **yes — this was never the problem** |
| **URL shape the viewer uses** | the cap URL **exactly**: `POST <capurl>` | the cap URL **plus a sub-path**: `GET <capurl>/category/{id}/children`, `<capurl>/item/{id}`, `<capurl>/orphans` |

That last row is the whole bug.

Both go `Caps.RegisterSimpleHandler` (`Source/OpenSim.Capabilities/Caps.cs:196-200`) →
`CapsHandlers.AddSimpleHandler` (`CapsHandlers.cs:94-100`) → `m_httpListener.AddSimpleStreamHandler(handler)`.
That call takes a second parameter which neither `Caps` nor `CapsHandlers` exposes:

```csharp
public void AddSimpleStreamHandler(ISimpleStreamHandler handler, bool varPath = false)   // BaseHttpServer.cs:358-364
{
    if (varPath)
        m_simpleStreamVarPath.TryAdd(handler.Path, handler);
    else
        m_simpleStreamHandlers.TryAdd(handler.Path, handler);
}
```

It defaults to **false**, so AIS landed in `m_simpleStreamHandlers`. And that dictionary is matched **exactly**:

```csharp
private bool TryGetSimpleStreamHandler(string uripath, out ISimpleStreamHandler handler)   // BaseHttpServer.cs:1109-1123
{
    if (m_simpleStreamHandlers.TryGetValue(uripath, out handler))      // exact match only
        return true;

    // look only for keyword before second slash ( /keyword/someparameter/... )
    handler = null;
    if (uripath.Length < 3) return false;
    int indx = uripath.IndexOf('/', 2);
    if (indx < 0 || indx == uripath.Length - 1) return false;
    return m_simpleStreamVarPath.TryGetValue(uripath[..indx], out handler);   // sub-paths live HERE
}
```

The dispatcher matches on `Util.TrimEndSlash(request.UriPath)` (`BaseHttpServer.cs:702-704`), i.e. the path
without the query string. For a cap registered at `/<uuid>`:

- `POST /<uuid>` — FetchInventory2's shape — hits the exact-match branch. Works.
- `GET /<uuid>/category/<id>/children` — AIS's shape — misses the exact match, falls to the var-path branch,
  where `uripath.IndexOf('/', 2)` lands on the slash after the 36-character UUID and looks up `/<uuid>` in
  `m_simpleStreamVarPath` — **which is empty for us**. Returns false. The server answers 404 and the handler is
  never entered.

So the var-path branch is exactly the mechanism AIS needs, and the key it would look up is exactly the path AIS
registered. Only the dictionary was wrong.

**Precedent:** every handler in this tree that serves sub-paths already passes `varPath: true` —
`GatekeeperServerConnector.cs:70`, `UserAgentServerConnector.cs:107`, `NeighbourServiceInConnector.cs:61`,
`SimulationServiceInConnector.cs:50-51`, `XBakesHandler.cs:62`. None of them is a **cap**, which is why
`Caps.RegisterSimpleHandler` never needed the parameter until now. AIS is the first sub-path cap in the tree.

## (b) Timing — not the cause

`RegionLoaded` ran: the startup line printed once, and it is emitted *after* `OnRegisterCaps += Handler`. The
subscription is taken on the same `Scene` object the log line names (`scene.EventManager`, with the scene captured
in the closure that `RemoveRegion` later unsubscribes). Truly logged in 18 minutes later, so there is no race.
`TriggerOnRegisterCaps` (`EventManager.cs:2119-2138`) invokes each delegate in a try/catch and logs
`[EVENT MANAGER]: Delegate for TriggerOnRegisterCaps failed` on a throw — no such line appeared either.

## (c) Cap names — not the cause

`AISv3Module.CapName = "InventoryAPIv3"` and `LibraryCapName = "LibraryAPIv3"`, matching `llaisapi.cpp:48-49`
character for character, and pinned by a test. `SeedCapRequest` adds every requested name to `validCaps` with no
whitelist (`BunchOfCaps.cs:340-376`), and `GetCapsDetailsLLSDxml` emits a URL for any name present in either
handler dictionary (`CapsHandlers.cs`), so a registered name is advertised.

## (d) Why nothing could be seen

Two log points were missing and are added by the fix:

- **at registration**, DEBUG, naming the agent and the URL produced — so a live run shows registration per agent
  rather than only the once-per-region startup line;
- **at handler entry**, DEBUG, naming the verb, path and resolved operation — so a request that arrives and fails
  is distinguishable from one that never arrives.

Had either existed, this would have been a one-minute diagnosis instead of a code read.

## What this says about the test suite

114 tests passed while this was completely broken. They drive `AisHandler.Handle(request, response)` directly, so
they exercise routing, envelopes and every operation — and never touch how the handler is bound to a URL. The bug
lived entirely in the two lines between `RegisterCaps` and the HTTP server, which no test observed.

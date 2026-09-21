# AIS-AUDIT-1 — the malformed-LLSD blind spot, tree-wide

**Date:** 2026-09-12 · **Tree:** `feature/ais-v3` at `73c8933253` · **Read-only sweep; no code changed.**

**Bottom line: the blind spot is real, it is worse than A21 recorded, and no site in this tree is currently
exploitable through it.** 86 parse sites examined, 40 of them client-facing. **Zero DESTRUCTIVE.** One latent
defect that is one edit away from destructive is recorded in §5, and one correction to A21/A23's own wording
in §6.

---

## 1. The parser's behaviour, measured

**A21 said "LibreMetaverse". That is wrong** — see §6. The OSD types in this tree come from
**`UtopiaSkye.OpenMetaverse.StructuredData` 1.1.7** (`Directory.Build.props:13-15`). Everything below was
measured against that package, in a throwaway console probe outside the repo, not taken from A21.

### 1a. `DeserializeLLSDXml` — A21 confirmed, and broader than recorded

| input | result |
|---|---|
| `<llsd><array><map>` (truncated) | `OSD` **Type=Unknown** |
| `<?xml version="1.0"?><outfit><wear/></outfit>` (well-formed, not LLSD) | `OSD` **Type=Unknown** |
| `""` (empty) | `OSD` **Type=Unknown** |
| `not xml at all` | `OSD` **Type=Unknown** |
| `<llsd><map><key>name</key>` (truncated map) | `OSD` **Type=Unknown** |
| `<llsd />` (valid but empty document) | `OSD` **Type=Unknown** |
| `<llsd><map /></llsd>` | `OSDMap` Type=Map Count=0 |
| `<llsd><array /></llsd>` | `OSDArray` Type=Array Count=0 |

Never throws, never returns null, for any malformed input. A *valid* empty map or array is correctly typed, so
**`Type == Unknown` is the only discriminator** — which is exactly what `AisHandler.ReadBody` now tests.

Note the last-but-two row: a bare `<llsd/>` is syntactically fine and still yields Unknown, so "Unknown"
conflates *malformed* with *empty document*. Both should be refused on a mutating route, so that conflation is
harmless here — but it is worth knowing before anyone tries to distinguish them.

### 1b. The siblings differ, and that is the part A21 did not cover

**This is not one blind spot. It is four different ones.**

| entry | truncated | not-LLSD | empty string | garbage text |
|---|---|---|---|---|
| `DeserializeLLSDXml` | **Unknown** | **Unknown** | **Unknown** | **Unknown** |
| `Deserialize` (auto-detect) | **Unknown** | **Unknown** | **Unknown** | throws `JsonException` |
| `DeserializeLLSDNotation` | **Unknown** | throws `OSDException` | **Unknown** | throws `OSDException` |
| `DeserializeJson` | throws `JsonException` | throws `JsonException` | **Unknown** | throws `JsonException` |
| `DeserializeLLSDBinary` | throws `OSDException` | throws `OSDException` | throws `OSDException` | throws `OSDException` |

Consequences worth stating plainly:

- **`Deserialize` (auto) is as dangerous as XML** and is used on 17 sites. A try/catch around it catches only
  the garbage-text case.
- **`DeserializeLLSDNotation` and `DeserializeJson` are partially covered** by a try/catch — each still returns
  Unknown for at least one input, and for both that input includes the **empty string**, i.e. *no body at all*.
- **`DeserializeLLSDBinary` is the only entry a try/catch fully covers.**
- For every text entry, **an absent body is indistinguishable from a malformed one**. That is the specific
  conflation that made AIS-SEC-2 destructive.

### 1c. What a degenerate OSD does downstream

| expression | result |
|---|---|
| `(OSDMap)degenerate` — hard cast | **throws `InvalidCastException`** |
| `(OSDArray)degenerate` — hard cast | **throws `InvalidCastException`** |
| `degenerate as OSDMap` — soft cast | **null** |
| `degenerate.AsString()` | `""` |
| `degenerate.AsInteger()` | `0` |
| `degenerate.AsBoolean()` | `false` |
| `degenerate.AsUUID()` | `00000000-0000-0000-0000-000000000000` |
| `degenerate.AsReal()` | `0` |

And, for comparison, `new OSDMap()["missing"].Type` is **also `Unknown`** — the same sentinel is used for "key
not present". That is why the type is easy to overlook: it is the universal "nothing here" value, not an error
marker.

**This is the structural finding that makes the whole sweep tractable.** A hard cast *fails loudly*. A soft cast
yields null, which fails loudly on first use. Only **holding a bare `OSD` and calling accessors** proceeds
silently, with empty strings and zero UUIDs — and only that path can write wrong data without anyone noticing.

---

## 2. The anti-pattern that actually caused AIS-SEC-2 — and it exists nowhere else

The destructive ingredient in AIS was not the bare parse. It was
**`catch { return new OSDMap(); }`**: a failure converted into a *plausible, empty, correctly-typed* value,
which then passed every downstream check and was read as "the client asked for nothing".

**Searched the whole of `Source/` and `Addons/` for that shape — a catch block substituting a default
`OSD`/`OSDMap`/`OSDArray`. Zero occurrences.** The only match in the tree is the doc comment in
`AisHandler.cs:419` describing the code that was removed.

That is the single most reassuring result of this audit, and it is why there are no DESTRUCTIVE findings.

---

## 3. Inventory

86 non-comment `OSDParser.Deserialize*` call sites in `Source/` + `Addons/`, by entry point:

| entry | sites |
|---|---|
| `DeserializeLLSDXml` | 40 |
| `DeserializeJson` | 18 |
| `Deserialize` (auto) | 17 |
| `DeserializeLLSDBinary` | 9 |
| `DeserializeLLSDNotation` | 3 |

Split by trust boundary:

- **40 client-facing** — parse an HTTP request stream, a response body from an external service, or a body
  string. Analysed in §4.
- **46 out of scope** — parse data the server itself wrote: assets (`asset.Data`), config, the round-trip in
  `SimulatorFeaturesModule.cs:267`, mesh/material blobs already validated on upload, `DAMap`, and
  `PrimitiveBaseShape`. Flagged, not analysed, per the brief.

**One nuance in that split, stated rather than hidden.** Several of the 46 parse **script-supplied** strings —
`OSSL_Api` (7 sites), `Phlox.ScriptEngine/LSLSystemAPI` (3), `JsonStore` (2). That is untrusted input from a
resident's script, a different trust boundary from an HTTP body but not a safe one. I checked the
fork-authored ones anyway because they use the mixed-blind-spot entries, and they are clean:

- `LSLSystemAPI.cs:13996` — `as OSDMap` followed by `parsed != null`. SAFE.
- `LSLSystemAPI.cs:16206` — `if (osd is OSDMap map)`. SAFE.
- `JsonStore.cs:131` — guards `string.IsNullOrEmpty(value)` *before* parsing, which pre-empts the one case
  `DeserializeJson` returns Unknown for. SAFE, and apparently deliberate.
- `JsonStore.cs:268` — try/catch, with a comment acknowledging the parser may crash on bad input. Covered,
  since Json throws for everything except the empty string handled above.

A full sweep of the OSSL script surface is **not** in this session's scope and is recommended as its own audit.

---

## 4. Classification of the 40 client-facing sites

Definitions used. The brief's DESTRUCTIVE definition was truncated in transmission; it is taken here as the
complement of HARMLESS — *unchecked, and a degenerate OSD reaches a write, so data can be lost or altered*.

| class | count | meaning |
|---|---|---|
| **SAFE** | 15 | type-checked (`OSDType`, `is OSDMap`, or soft cast + null check) before use |
| **HARMLESS** | 25 | unchecked, but a degenerate OSD throws before any write, and the route answers an error |
| **DESTRUCTIVE** | **0** | — |

### 4a. SAFE — the good pattern, already widespread

| site | guard |
|---|---|
| `BaseHttpServer.cs:1499` | `if (llsdRequest is not OSDMap) return;` — **the framework's default LLSD dispatcher**, so this covers every handler behind it |
| `WebStatsModule.cs:473` | `if (message.Type != OSDType.Map) return ...`, and again on the nested `agent` map — the exact pattern AIS adopted, pre-existing |
| `EnvironmentModule.cs:581` | `if (req is OSDMap map)` |
| `FreeSwitchVoiceModule.cs:320` | `if (tmp is OSDMap map)` |
| `VivoxVoiceModule.cs:457` | `if (tmp is OSDMap map)` |
| `WebRtcVoiceRegionModule.cs:896` | `if (tmp is OSDMap map) return map;` else falls through to a logged failure |
| `AisHandler.cs:452-454` | `parsed is null \|\| parsed.Type == OSDType.Unknown` — AIS-SEC-2's own fix |
| `WebUtil.cs:501,598` · `Util.cs:2710,2726` | `responseOSD.Type == OSDType.Map` |
| `JanusAdminClient.cs:162` · `JanusPeerCtlBatchSink.cs:368` · `JanusMessages.cs:217` | `is OSDMap` pattern / soft cast + null check |

### 4b. HARMLESS — 25 hard-cast sites

All of the form `(OSDMap)OSDParser.DeserializeLLSDXml(httpRequest.InputStream)` or
`(OSDArray)OSDParser.Deserialize(...)`. On a degenerate OSD the cast throws `InvalidCastException` (§1c),
**before** the handler reaches any backend call. Either a local `catch` answers an error, or the exception
reaches `BaseHttpServer`, which answers 500. **No write occurs with degenerate data in either case**, and —
critically — none of them catches the exception and then continues with a substituted default (§2).

Includes the write-capable routes, which are the ones that would matter if this were wrong:
`BunchOfCaps.cs:326,701,1163,1935,2067` (upload/caps), `MoapModule.cs:239,466` (media-on-a-prim),
`MaterialsModule.cs:478,558,883` (materials), `LandManagementModule.cs:2049` (parcel),
`GodsModule.cs:117`, `AgentPreferencesModule.cs:121`, `DisplayNameModule.cs:145`,
`ServerSideBakingModule.cs:325`, `ExperienceModule.cs:244`, `EstateChangeInfo.cs:155`,
`FetchInvDescHandler.cs:73` and `FetchLibDescHandler.cs:74` (read-only), `SimpleOSDMapHandler.cs:99`,
`FetchInventory2Handler.cs:58`, `Utils.cs:105` / `Simulation/Utils.cs:89`, `LLLoginHandlers.cs:236`,
`BaseHttpServer.cs:1429`, `GroupsModule.cs:464,514`.

**"Harmless" here means no data is harmed, not that the behaviour is ideal.** A malformed body on these routes
produces an `InvalidCastException` in the log and a bare 500, where AIS now produces a Warning naming the route
and byte count plus a 400. That is a quality gap, not a security one, and it is a large mechanical change
across 25 sites — deliberately **not** proposed here.

---

## 5. The one latent defect: `ViewerEnvironment.FromWLOSD` checks the wrong variable

`Source/OpenSim.Framework/ViewerEnvironment.cs:91-101`:

```csharp
public void FromWLOSD(OSD osd)
{
    OSDArray array = osd as OSDArray;   // a degenerate OSD -> null
    if (osd != null)                    // <-- checks osd, NOT array
    {
        Cycle = new DayCycle();
        Cycle.FromWLOSD(array);         // passes null
    }
    InvalidateCaches();
}
```

A degenerate `OSD` is **non-null**, so the guard passes and `null` is handed to
`DayCycle.FromWLOSD(OSDArray array)`.

Reached from `EnvironmentModule.SetEnvironmentSettings` (`:757`), the legacy WindLight setter, which parses with
the auto-detect entry — one of the four with the blind spot — and holds the result as a bare `OSD` with **no
type check**:

```csharp
ViewerEnvironment VEnv = new();
OSD env = OSDParser.Deserialize(request.InputStream);
VEnv.FromWLOSD(env);
StoreOnRegion(VEnv);        // <-- the write
```

**Why it is HARMLESS today:** `DayCycle.FromWLOSD` (`ViewerDaycycle.cs:65`) dereferences `array.Count` at
`:71`, before it reads anything else, so it throws `NullReferenceException` **before** `StoreOnRegion(VEnv)` is
reached. The
surrounding `catch` logs an error and answers `success: false` with a `fail_reason`. Nothing is written.

**Why it is worth recording anyway:** it is harmless *by accident of evaluation order*, not by design. Make
`DayCycle.FromWLOSD` null-tolerant — a reasonable-looking hardening change — and `Cycle` becomes an empty
`DayCycle`, `StoreOnRegion` is reached, and **the region's environment is overwritten with a blank one** by a
truncated request. That is the AIS-SEC-2 failure mode exactly: a malformed body read as "the client asked for
nothing".

Mitigating factors: the route is gated by `CanIssueEstateCommand`, so it needs an estate manager; and it is the
legacy WL path, superseded by the checked `:581` handler for modern viewers. **Not fixed in this session** —
read-only — but it is the one place where the blind spot is still load-bearing.

---

## 6. Correction to A21 and A23: the package is not LibreMetaverse

Ledger row **A21** says *"the shipped parser"* and the AIS-SEC-2 commit body says the same; row **A23** says
*"Measured on this tree's `UUID`"* and both the row and the handoff attribute it to LibreMetaverse. **The OSD
and UUID types in this tree come from `UtopiaSkye.OpenMetaverse.*` 1.1.7**, referenced in
`Directory.Build.props:13-17`. LibreMetaverse 3.1.4 *is* in the NuGet cache, but for a different project in
this workspace, not this tree.

**The measurements in A21 and A23 stand** — both were taken through the tree's own test project, which
references the real package — so only the attribution was wrong. Corrected here rather than silently, because
anyone chasing the `UUID.GetHashCode` low-byte finding of A23 would otherwise go and read the wrong source.

---

## 7. Recommendations, in priority order

1. **`ViewerEnvironment.FromWLOSD` (§5): change `if (osd != null)` to `if (array != null)`.** One line, removes
   the only load-bearing instance of the blind spot, and removes the trap that a future null-tolerance edit
   would spring. Highest value per unit of risk in this document.
2. **Record the §1b table where the next author will find it.** The "four different blind spots" result is the
   part most likely to be re-derived from scratch; `Deserialize` (auto) being as unsafe as XML is the
   counter-intuitive half.
3. **Audit the OSSL / script-supplied parse surface** (§3) as its own session. Different trust boundary, 12+
   sites, out of scope here.
4. **Do not mass-convert the 25 hard-cast sites.** They are safe against data loss. Converting them to typed
   checks with proper 400s is a quality improvement worth doing route-by-route when each is touched for another
   reason, not as a sweep — a 25-site mechanical change to the cap surface carries more risk than the logging
   it would improve.

---

## 8. Method, so this can be repeated or disputed

- Parser behaviour: a throwaway .NET 10 console project in the session scratchpad referencing
  `UtopiaSkye.OpenMetaverse.StructuredData` / `.Types` 1.1.7, exercising all five entries against six malformed
  and two valid inputs, plus the cast and accessor behaviour of §1c. **The repository was not modified at any
  point in this session**, and `git status` was clean before and after.
- Inventory: `grep -rn 'OSDParser\.Deserialize' Source/ Addons/ --include=*.cs`, excluding `bin/`, `obj/`,
  comment lines and tests.
- Trust split: sites reading `InputStream` / a request or response body treated as client-facing; sites reading
  `asset.Data`, config, or data this server serialized treated as out of scope.
- Anti-pattern search: catch blocks substituting a default `OSD`/`OSDMap`/`OSDArray`.
- Every classification in §4 was read at the call site; none was inferred from the grep line alone.

# Recon Report — Trusted Hypergrid

**Status:** FROZEN as fact-finding. Supersedes Part 1 of `trusted-hypergrid-analysis.md`.
**Scope:** what the code does today. No design, no fixes, no opinions on what it should be.

**Tree:** `OpenSim-NGC/OpenSim-Tranquillity`, branch `develop`
**Commit:** `a115734ff3e47be37cfa9fbc5b606ff8b099f535` — "Feature/xunit tests (#197)", 16 Aug 2026 22:30 -0400
**Target framework:** `net10.0` (`Directory.Build.props:3`)
**Method:** shallow clone, direct inspection. All file:line references are to this commit.

---

## R0. Correction to the prior analysis

The earlier analysis document was verified against **`master` at `526b424cd` (16 Dec 2025)**, not `develop`. The clone defaulted to `master` and this was not caught. Three of its conclusions are wrong as a result. This report replaces its Part 1. Everything below was re-verified against `develop`.

---

## R1. BinaryFormatter — fully removed. Prior finding F1 is CLOSED.

`grep -rn BinaryFormatter` across `.cs`, `.csproj`, `.props`, `.json` returns only explanatory comments. No live use.

- `Directory.Build.props` no longer contains `EnableUnsafeBinaryFormatterSerialization`.
- YEngine (`Source/OpenSim.Region.ScriptEngine.YEngine/XMRInstAbstract.cs:1474-1477, 1989-1995`) retains the legacy opcodes `SYSERIAL` / `THROWNEX` as **refused-on-read markers** — the blob is never deserialized; the script restarts clean. A third marker `SYSUNSUP` covers system types it will not serialize without BinaryFormatter.
- `FlotsamAssetCache` (`Source/OpenSim.Region.CoreModules/Asset/FlotsamAssetCache.cs:63, 162, 533`) uses a fixed-type XML serializer under a format-versioned subdirectory; legacy cache files are not read.
- `KeyframeMotion.cs` moved to `Source/OpenSim.Region.Framework/Scenes/` and no longer uses it. (Consistent with the `KFM1` format noted in prior sessions.)

**Consequence:** the proposed Phase 0 does not exist as work. The .NET 10 blocker is gone.

## R2. Tree layout has been restructured

Projects now live under `Source/<AssemblyName>/` — e.g. `Source/OpenSim.Services.HypergridService/`, `Source/OpenSim.Region.CoreModules/`, `Source/OpenSim.Server.Handlers/`. Phlox is in-tree at `Source/InWorldz.Phlox/`.

**Every file path in the prior analysis is invalid.** Config examples also moved: `Robust.HG.ini.example` is now at `Source/OpenSim.Server.GridServer/AppData/`.

## R3. Export-bit enforcement is PARTIALLY IMPLEMENTED by another contributor

`Source/OpenSim.Region.CoreModules/Framework/InventoryAccess/HGInventoryAccessModule.cs`

Commit `6180f40` — *"Fix HGInventoryAccessModule OutboundPermission default and honor per-item Export bit (#187)"*. In-code attribution at lines 385-391 credits **Laxton Consulting / IMA, @llaxton, 2026-07-26**.

Two changes:

- Line 97 — `m_OutboundPermission` default flipped from `true` to **`false`**.
- Lines 392-393 — the inventory-transfer path now requires both the grid-wide toggle and the item's own bit:
  ```
  bool itemExportAllowed = (item.CurrentPermissions & (uint)PermissionMask.Export) != 0;
  if (isForeignReceiver && receiverAssetServer != string.Empty && m_OutboundPermission && itemExportAllowed)
  ```

**A second party is actively working this exact surface.** This is a coordination fact, not a code fact, and it is the most consequential item in this report.

## R4. Export-bit enforcement — what remains open

- `HGInventoryAccessModule.cs:214` — the `PostAsset` path still reads `(m_OutboundPermission || (type == AssetType.Landmark))` with **no Export-bit check**. The landmark carve-out is correct and must be preserved.
- Lines 560, 574 — still consult only `m_OutboundPermission`.
- `Source/OpenSim.Services.HypergridService/HGAssetService.cs` — gates at lines 112, 152, 197 (`AllowedExport(asset.Type)`) and 170 (`AllowedImport(asset.Type)`) remain **asset-type-only and grid-wide**. `AssetPermissions` is a pair of `bool[]` indexed by `AssetType`; it has no per-item, per-owner or per-requester dimension.

## R5. Public HG endpoints remain unauthenticated by shipped default

`Source/OpenSim.Server.GridServer/AppData/Robust.HG.ini.example` — `AuthType = None`, uncommented, at **line 817** (`[HGInventoryService]`) and **line 838** (`[HGAssetService]`).

Unchanged from the prior finding: on the Hypergrid, knowledge of an asset UUID is the entire access-control mechanism for anything the type filter permits.

## R6. Grid identity and exception matching — unchanged

`Source/OpenSim.Services.HypergridService/GatekeeperService.cs`

- `IsException()` at lines 665-677: trailing-slash normalisation followed by `userURL.Equals(s)`. Exact string comparison against the self-asserted `HomeURI`.
- `Authenticate()` still calls back to whatever `HomeURI` the incoming circuit claims.
- `CheckAddress()` still correctly verifies the service token names this gatekeeper.

`AllowExcept` / `DisallowExcept` remain evadable by advertising an alias URI.

## R7. Presence/location oracle — unchanged

`Source/OpenSim.Server.Handlers/Hypergrid/UserAgentServerConnector.cs`

- Line 56 / 85 — `m_VerifyCallers` defaults to `false`.
- Line 86 — `AuthorizedCallers` defaults to `127.0.0.1`; the check is a raw IP string compare.

`locate_user`, `get_uui`, `get_uuid`, `get_server_urls`, `status_notification`, `get_online_friends` are reachable unauthenticated under shipped defaults.

## R8. ServiceAuth mechanisms — unchanged

`Source/OpenSim.Framework/ServiceAuth/` contains `BasicHttpAuthentication`, `CompoundAuthentication`, `DisallowLlHttpRequest`, `IServiceAuth`, `ServiceAuth`. No HMAC, no signed-token, no mTLS. A new `IServiceAuth` implementation remains unavoidable for signed inter-grid calls.

## R9. HG asset push robustness — unchanged

`Source/OpenSim.Region.CoreModules/Framework/InventoryAccess/HGAssetMapper.cs:223` `Post(...)` — serial loops at 246, 262, 275; bare `catch` at 254; exception swallow at 295. No retry, no backoff, no parallelism.

## R10. Phlox script state — OPEN GAP NOW CLOSED, and it is clean

The prior analysis could not check this. Now that Phlox is in-tree:

`Source/InWorldz.Phlox/Util/Preloader.cs:12-23` pre-compiles **protobuf-net** contracts for `SerializedLSLList`, `SerializedLSLPrimitive`, `SerializedPostedEvent`, `SerializedRuntimeState`, `SerializedScript`, `SerializedStackFrame`, `ActiveListen`, `DetectVariables`, `EventInfo`, `FunctionInfo`, `MemoryInfo`.

protobuf-net with explicitly declared contract types is a **fixed-schema** deserializer — the wire payload cannot name arbitrary types, so it is not a CWE-502 gadget surface. The one pattern that would break this, `DynamicType = true`, does not appear anywhere in `Source/InWorldz.Phlox/`.

**Phlox script-state serialization is not a deserialization risk.** No work required.

## R11. Repository conventions

- Documentation convention is `Docs/*.md` at repo root. A `Docs/SECURITY.md` already exists. Existing multi-document features (`HostedService*.md` — architecture, migration plan, sprint plan, regression checklist, backlog, audits) establish the precedent for a document set per feature.
- Plugin registration is per-addon: `Addons/<Name>/PluginRegistration.cs`. New region modules must be registered explicitly; there is no reflection-based discovery.

---

## Summary of status change

| Prior finding | Status on `develop` @ `a115734ff` |
|---|---|
| F1 BinaryFormatter RCE | **CLOSED** — removed, .NET 10 |
| F2 Unauthenticated HG asset endpoint | Open, unchanged |
| F3 Export bit unenforced | **Partially closed by #187**; `PostAsset` path and `HGAssetService` still open |
| F4 Self-asserted identity / string matching | Open, unchanged |
| F5 Presence oracle | Open, unchanged |
| F6 No transport security / one auth mechanism | Open, unchanged |
| F7 Serial asset push | Open, unchanged |
| F8 XML hardening adequate | Re-confirmed |
| Phlox script state (was: unknown) | **CLOSED** — protobuf-net fixed-schema, no `DynamicType` |

## Open questions this report cannot answer

1. **What is @llaxton's remaining scope?** #187 landed the inventory-transfer half of the Export-bit work. Whether Laxton Consulting intends to continue into `PostAsset` and `HGAssetService`, or considers it finished, determines whether that work is ours to do.
2. **Does the Legion tree diverge in any HG path?** Not inspected. Relevant because of the vendored LibOMV 1.2.13 vs the NuGet `OpenMetaverse` package upstream now pulls in at the top level (commit `07006d9`).
3. **Balpien's privacy requirements**, particularly regarding R7.

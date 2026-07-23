# Tranquillity Experience Port — Conformance Ledger

**Discipline mirrors Legion's `experience-parity-ledger.md`.** Every row is PORTED (with evidence: commit + Legion reference) or DEFERRED-BY-SLICE (with the slice it belongs to and why). A row is not "done" until PORTED or explicitly DEFERRED.

**Legion reference:** `/d/legion-grid-source` @ tag `port-source-2026-07-22` (Experience COMPLETE/SL-compliant).
**Target:** `/d/tranquillity-develop` @ `develop`. **Strategy:** RECONCILE via `PhloxExperienceAdapter` (NGC storage authoritative, never modified; translation lives in the Phlox/adapter layer).

**Provenance standard (applies to every T-slice row):** each slice is **ported and code-path-verified against the complete Legion source; NOT executed on this tree** (Option-C discipline — no run against John's SQLite/MySQL). Live verification is John's, per slice.

---

## Slice T1 — script-surface conformance (SS-1..9) — ✅ PORTED (2026-07-23)

Behavior-only. Touched **`Phlox.ScriptEngine/LSLSystemAPI.cs`** + **`Phlox.ScriptEngine/PhloxExperienceAdapter.cs`** (DTO field). **No storage/wire/schema change.** Deploy: `Phlox.ScriptEngine.dll` only.

| SS | Item | Legion (correct) | Tranquillity (was) | T1 change | State |
|----|------|------------------|--------------------|-----------|-------|
| **SS-1** | `llGetExperienceDetails` layout | `[name, owner key, experience id, state(int), state msg, group key]` | `[name, owner, description, group, maturity, ""]` — wrong data at every index (High) | Correct SL layout; `state` from `PROPERTY_DISABLED` bit → `0`/`8`; msg via `llGetExperienceErrorMessage`. Added `Properties` to adapter DTO (mapped from NGC `info.properties`). | **PORTED** |
| **SS-2** | `llCreateKeyValue` key cap | 1011 (`MAX_KEY_LENGTH`) | **255** | `key.Length > 1011` (`MAX_EXPERIENCE_KEY_LENGTH`) | **PORTED** |
| **SS-3** | `llUpdateKeyValue` key cap | 1011 | **255** | `key.Length > 1011` | **PORTED** |
| **SS-4** | KV missing-key → code 14 | delete/read missing → `0,14` (KEY_NOT_FOUND) async CSV | `…SL` wrappers returned free-text (`"0,key not found"`); no `llDeleteKeyValueSL` | `…SL` wrappers now emit **numeric XP_ERROR**: read-miss → `0,14`; create-fail → `0,13`; update-CAS-fail → `0,15`. **Dedicated `llDeleteKeyValueSL` DEFERRED** (see below). | **PORTED (partial)** |
| **SS-5** | error table rows 14-16 | SL wiki table | 14 "key already exists", 15/16 shifted | Corrected to SL: 14 "key doesn't exist", 15 "retry update", 16 "…content rating too high" | **PORTED** |
| **SS-6** | `llAgentInExperience` presence + block | root-presence + `HasExperiencePermission` (block-wins > admission > grant) | bare `IsAgentGranted` | Root-presence check + agent-block-wins + grant. **Region/parcel BLOCK + admission ladder DEFERRED → T5** (adapter lacks region-block/parcel data). | **PORTED (core)** |
| **SS-7** | `llRequestExperiencePermissions` land code 17 | no-exp→5, land/admission→17, not-present→4, agent-block→4 | no-exp→**17**, region-deny→**18**, not-present→**17** (all wrong) | Corrected: no-exp→**5**, admission→**17**, not-present→**4**, block→**4**. (Auto-grant body untouched — consent is T3.) | **PORTED** |
| **SS-8** | error row 17 text | "not allowed to run on this land" | "…request timed out" (wrong) | Corrected row 17 | **PORTED** |
| **SS-9** | error row 18 | "experience permissions request timed out" | **missing** | Added row 18 | **PORTED** |

**Evidence:** commit `<T1-HASH>` (this slice). Legion ref: `port-source-2026-07-22` `Phlox.ScriptEngine/LSLSystemAPI.cs` (`llGetExperienceDetails` :12945, `llAgentInExperience` :12928, `llRequestExperiencePermissions` :12708, `llGetExperienceErrorMessage` :12010, KV :11395) + parity-ledger Section 1.

### Deferred out of T1 (documented)
- **`llDeleteKeyValueSL` (SS-4 delete-specific SL surface):** would be a **new** registered LSL function (4-file: `Defaults.cs`+`ISystemAPI.cs`+`SyscallShim.cs`+impl, new `TableIndex` 674 in the positional dispatch array — an off-by-one there breaks *all* script dispatch). Not worth the risk for one wrapper in a behavior slice; the numeric `14` already surfaces via the fixed `llReadKeyValueSL`, and the int `llDeleteKeyValue` already signals not-found (-4). **Follow-up (small, deliberate).**
- **SS-6 full admission ladder (region/parcel BLOCK + region-allow/grid-wide/parcel-allow):** needs adapter/service methods NGC doesn't expose yet (region block list, parcel checks) → **T5 (trusted/enforcement)**.
- **KV async-dataserver model:** Tranquillity KV is synchronous (`int` + `…SL` string wrappers) vs SL/Legion's async request-key + `dataserver` CSV. Architecture, **not T1** (not in the audit's T1 scope).

---

## Slice T2 — KV quota 16 → 128 MiB + XP_ERROR_QUOTA_EXCEEDED (11) — ✅ PORTED (2026-07-23)

**Ported and code-path-verified against Legion; NOT executed on this tree.** Enforcement LOGIC in the Phlox layer (`Phlox.ScriptEngine/LSLSystemAPI.cs`); the one unavoidable NGC touch is the limit **constant** (`OpenSim.Services.ExperienceService/ExperienceService.cs` `MAX_QUOTA` 16→128 MiB) — NGC enforces internally, so its constant had to align or it would reject at 16 before the Phlox gate. Deploy: `Phlox.ScriptEngine.dll` + `OpenSim.Services.ExperienceService.dll`.

| Item | Legion (verified) | Tranquillity (was) | T2 change | State |
|------|-------------------|--------------------|-----------|-------|
| Limit | 128 MiB (`MAX_DATA_QUOTA`) | **16 MiB** (`MAX_QUOTA`) | Phlox `MAX_DATA_QUOTA = 128 MiB`; NGC `MAX_QUOTA` 16→128 (backstop) | **PORTED** |
| Enforcement | check-before-write in Phlox; code 11 | NGC check-before-write but "full" collapsed to `-2` → SL `0,13` | Phlox pre-write check; over → `-5` → `…SL` emits `0,11` | **PORTED** |
| Create basis | `used + KvBytes(key) + KvBytes(value)` (full pair) | (NGC `.Length` chars) | ported verbatim (UTF-8 bytes) | **PORTED** |
| Update basis | `ExceedsQuota`: `used − oldPair + newPair` (delta-aware) | (NGC delta, char-based) | ported verbatim (UTF-8 bytes, reads old value) | **PORTED** |
| Byte basis | key+value UTF-8 bytes (`KvBytes`) | NGC `.Length` (chars) | Phlox uses `Encoding.UTF8.GetByteCount` (matches MySQL `LENGTH`) | **PORTED** |
| `llDataSizeKeyValue` (used,total) | `"1,used,128MiB"` (async) | returns `int` used only | TOTAL const = 128 MiB, used for the gate; surfacing `(used,total)` to the script needs an `…SL`/async wrapper → **DEFERRED** with the KV async-model slice | **PARTIAL** |

**Evidence:** commit `<T2-HASH>`. Legion ref `port-source-2026-07-22` `Phlox.ScriptEngine/LSLSystemAPI.cs` `KvBytes`:11380, `ExceedsQuota`:11386, create-check:11408, update-check:11479, `llDataSizeKeyValue`:11592; `Services/Interfaces/ExperienceInfo.cs` `MAX_DATA_QUOTA`:69.

**Non-identical (flagged):** on the **SQLite standalone**, NGC `GetSize` uses SQLite `LENGTH()` = *character* count (not bytes), while the Phlox delta uses UTF-8 bytes — they agree for ASCII, diverge for multi-byte keys/values. Legion runs on MySQL (`LENGTH` = bytes) where both sides are byte-consistent; Tranquillity's grid (MySQL) matches Legion exactly, its SQLite standalone has this minor char-vs-byte nuance at the quota boundary. **KV async-dataserver model** (sync `int`+`…SL` strings vs SL async request-key+`dataserver` CSV) remains a later architecture slice — T2 is quota only.

---

## Slice T3 — real SL consent (Legion D1) — ✅ PORTED (2026-07-23)

**Ported and code-path-verified against Legion; NOT executed on this tree.** Replaces auto-grant with the ScriptQuestion consent dialog + await + per-cause codes + 300s timeout + trusted seam. All in the Phlox/adapter layer — **client stack and NGC storage untouched** (Tranquillity's `LLClientView.SendScriptQuestion(…,experience)` already builds the Experience block, guarded on `experience != Zero`; `OnScriptAnswer` already fires on `ScriptAnswerYes`). Deploy: `Phlox.ScriptEngine.dll` only.

**STEP-0 gate (definitive):** a throwaway .NET 8 reflection app confirmed `ScriptQuestionPacket.ExperienceBlock` (declaring type `ScriptQuestionPacket`) with `ExperienceID : OpenMetaverse.UUID`, the `ScriptQuestionPacket.Experience` holder field, and `ScriptAnswerYesPacket` — all present in OpenMetaverse 1.2.13. Consent packet path buildable.

| Item | Legion (verified) | Tranquillity (was) | T3 change | State |
|------|-------------------|--------------------|-----------|-------|
| Gate order | no-exp 5 → block 17 → admission 17 → presence 4 → **agent-block 4 (before granted)** → granted → trusted → dialog | auto-grant; granted checked *before* block | full gate, **agent-block before already-granted** | **PORTED** |
| Consent | ScriptQuestion+Experience dialog, await `ScriptAnswerYes` | **auto-grant, no dialog** | `SendScriptQuestion(…,PERMISSION_EXPERIENCE, experienceId)` + await | **PORTED** |
| Correlation | pending map keyed by ItemID; match by TaskID+ItemID via `OnScriptAnswer` | none | `m_pendingExpPerms` + `RegisterPendingExperiencePerm`/`ResolveExperiencePerm` (first-wins) | **PORTED** |
| Yes / No / disconnect | Yes→grant; No→4; disconnect→4 | n/a | ported verbatim | **PORTED** |
| Timeout | 300s → code 18 | none | `EXPERIENCE_PERM_TIMEOUT_MS=300000` → `_denied 18` | **PORTED** |
| Trusted silent-grant | region-trusted → grant, no dialog | n/a | seam: `adapter.GetTrustedExperiences` → NGC `GetEstateKeyExperiences()` (estate, not Legion's table) | **PORTED (seam)** |
| Landmine guard | Experience block only when real experience | already guarded in Tranq `LLClientView:12335` | verified; normal `llRequestPermissions` dialogs keep working | **VERIFIED** |

**Adapter translations (Legion granular → NGC coarse):** `GrantPermission`→`adapter.GrantPermission`→NGC `UpdateExperiencePermissions(Allowed)`; `IsAgentGranted`/`IsAgentBlocked`→`adapter` (module `GetExperiencePermission`); `GetTrustedExperiences`→**new adapter method**→`GetEstateKeyExperiences()`; `InvalidatePermission`→adapter no-op. **Tranquillity storage semantics used, Legion's NOT ported:** permissions = single `allow BIT` table (grant=Allowed); script↔experience = `TaskInventoryItem.ExperienceID`; trusted = estate `EstateKeyExperience`.

**Evidence:** commit `<T3-HASH>`. Legion ref `port-source-2026-07-22` `Phlox.ScriptEngine/LSLSystemAPI.cs`: gate `:12708`, `GrantExperienceAndNotify` `:12823`, `RegisterPendingExperiencePerm` `:12837`, `HandleExperienceScriptAnswer` `:12868`, `ResolveExperiencePerm` `:12890`, consts/`PendingExperiencePerm` `:1337`.

**Deferred:** **T4** = ExperiencePreferences Block-button *persistence* (T3 ships the block *check*, not the write). **T5** = trusted *enforcement* + region/parcel BLOCK-list admission ladder (T3 ships the trusted *grant* seam + estate-allow admission only). Non-identical: region/parcel BLOCK tier absent (NGC adapter lacks a region-block list) → T5.

---

## Slice T4 — Block-button persistence loop — ✅ PORTED (2026-07-23)

**Ported and code-path-verified against Legion; NOT executed on this tree.** Completes T3's consent Block button. **Zero schema change** — uses Tranquillity's single permissions table (`experience_permissions.allow BIT`: grant = allow true, **block = allow false**), NOT Legion's dedicated `experience_agent_blocked` table (the TRANQ-AHEAD item — never reverted). Deploy: `Phlox.ScriptEngine.dll` only.

**Key finding:** most of the loop already existed and only needed adapter cache-coherency correctness:
| Piece | Status |
|---|---|
| **Write** — ExperiencePreferences cap PUT `permission=Block` → `SetExperiencePermissions(allow=false)` → `UpdateExperiencePermissions(Blocked)` (persist) + cache + `UpdateScriptExperiencePerms` (revoke running perms) | **Already existed** (`LindenCaps/ExperienceModule.cs:214,231,261`) |
| **Cache load** — on login, `OnNewClient` loads `FetchExperiencePermissions` (grants + blocks) into the module cache | **Already existed** (`:104`) |
| **Enforce** — early-denial gate checks agent-block **before** already-granted → code 4, no dialog | **Done in T3** |
| **Adapter read robustness** — `IsAgentBlocked` was module-cache-only (`None` → "not blocked"); now falls back to the service on a cache miss (single-table `allow=false` entry = block) — closes the legion-semantics TODO | **T4 change** |
| **Adapter grant coherency** — `GrantPermission` was service-only (cache stale → granted experience re-prompted); now routes through the module setter (updates cache + service) | **T4 change** |

**Loop (verified vs Legion):** Block clicked → viewer PUTs ExperiencePreferences(Block) → `allow=false` persisted + cached (and `ScriptAnswerYes(0)` resolves the current request as denied 4) → next `llRequestExperiencePermissions` → gate hits `IsAgentBlocked` FIRST → **denied 4, no dialog**. Stale-grant: grant (allow=true, now cached) → block (allow=false, overwrites) → denied 4. Unblock (prefs Allow/Forget) → prompts again. **Consent persistence:** Yes → allow=true (Legion & Tranq persist); **No → transient, NOT persisted** (per-request — matches Legion: `ResolveExperiencePerm` posts denied 4 without writing); Block → allow=false persisted.

**Evidence:** commit `<T4-HASH>`. Legion ref `port-source-2026-07-22`: enforcement order `Phlox.ScriptEngine/LSLSystemAPI.cs:12774` (agent-block before granted), Block-via-ExperiencePreferences (parity-ledger CAP-EPREF / D1 recon). Tranquillity: `LindenCaps/ExperienceModule.cs` prefs cap + `Phlox.ScriptEngine/PhloxExperienceAdapter.cs` (T4 adapter fixes).

**Non-identical:** Legion persists agent-block in a dedicated table; Tranquillity uses the single `allow BIT` (equivalent, TRANQ-AHEAD, no migration). Region/parcel BLOCK-list enforcement remains T5.

---

## Slice T5 — trusted enforcement + admission ladder — ⚠️ PARTIAL (trusted PORTED; region block → resolved in T5b; parcel block = STOP) (2026-07-23)

**Ported and code-path-verified against Legion; NOT executed on this tree.** Phlox layer only; **no adapter change, no NGC change, no new tables.** Deploy: `Phlox.ScriptEngine.dll` only.

**PORTED — trusted enforcement + the admission tiers Tranquillity has a source for:**
| Item | Legion | Tranquillity source | T5 |
|---|---|---|---|
| Trusted → silent grant | `experience_trusted` table | **estate `KeyExperiences`** (NOT Legion's table) via `GetTrustedExperiences`→`GetEstateKeyExperiences()` | wired in T3, verified |
| Admission includes trusted | `IsExperienceAdmittedAt`: allowed OR trusted OR grid-wide OR parcel-allow | estate allow OR trusted | new `IsExperienceAdmitted` helper — fixes a trusted-but-not-allowed experience being wrongly denied 17 |
| Admission tier on `llAgentInExperience` (SS-6) | `HasExperiencePermission` admission | estate allow OR trusted | added (closes the *admission* half of SS-6's deferred tier) |
| Ordering: agent-block wins over trusted | agent-block (4) before trusted-grant | same | verified — block checked before the trusted silent-grant |

**🛑 STOP — region/parcel BLOCK-wins ladder + grid-wide + OTH-1 parcel scoping: NO NGC SOURCE.** Tranquillity has **no** region/estate blocked-experience store (`EstateSettings` has `AllowedExperiences` + `KeyExperiences` only — no `BlockedExperiences`; the RegionExperiences cap hardcodes `blocked` empty), **no** parcel-experience data (`ILandObject` has no `IsExperienceAllowed`/`IsExperienceBlocked`), and **no** grid-wide bit. Implementing these would require either a **new estate field/table** (region-block — breaks the no-schema-change property this port has held for T1-T4) or a **new land-layer subsystem** (parcel experience access entries + `ILandObject` methods). **Flagged for John's decision — not invented here.** SS-6's BLOCK-wins tier therefore remains deferred.

> **UPDATE (T5b, 2026-07-23):** the **region-block** half is now resolved — John's decision was taken to make the first schema change (estate `BlockedExperiences` mirroring Allowed/Key; EstateStore VERSION 38). See the T5b slice below. **Parcel-block, OTH-1 agent-parcel scoping, and the grid-wide bit remain deferred as a separate project.**

**Evidence:** commit `<T5-HASH>`. Legion ref `port-source-2026-07-22` `Phlox.ScriptEngine/LSLSystemAPI.cs`: `IsExperienceAdmittedAt`:12661, `IsExperienceBlockedInRegion`:12640, `IsExperienceBlockedOnParcelAt`:12699, OTH-1 agent-parcel `HasExperiencePermission`:12614. Tranquillity gap: `EstateSettings.cs:279-292` (allow+key, no block), `ILandObject.cs` (no experience methods), `IExperienceModule.cs:32-33`.

**Non-identical:** trusted from estate `KeyExperiences` (not Legion's `experience_trusted` table — TRANQ-AHEAD, no migration); the entire BLOCK-wins land tier is absent pending the storage decision above.

---

## Slice T5b — region-block list (estate-level), block-wins tier — ✅ PORTED (2026-07-23)

**Ported and code-path-verified against the complete Legion source; NOT executed on this tree.** Resolves the **region** half of the T5 STOP. **FIRST schema change of this port** — one estate list mirroring the existing Allowed/Key arrays (MySQL `estate_blocked_experiences`, EstateStore **VERSION 38**). **Parcel-block + OTH-1 + grid-wide remain a separate project.** Deploy: `OpenSim.Framework.dll`, `OpenSim.Data.MySQL.dll`, `OpenSim.Region.CoreModules.dll`, `OpenSim.Region.Framework.dll`, `OpenSim.Region.ClientStack.LindenCaps.dll`, `Phlox.ScriptEngine.dll` (+ MySQL migrates to VERSION 38 on startup).

**What persists (reported exactly, per the task):**
- **MySQL (grid):** a NEW table `estate_blocked_experiences (EstateID int, uuid char(36))`, mirror of `estate_allowed_experiences` / `estate_key_experiences`. `MySQLEstateData` load (both overloads) and save wire it through the existing `LoadUUIDList`/`SaveUUIDList` helpers. `EstateStore.migrations` **VERSION 38** creates it. **This is the only new persisted object.**
- **SQLite (standalone):** NOTHING new persists — Tranquillity's SQLite estate store already does not persist Allowed/Key experiences (in-memory for the session); BlockedExperiences behaves identically (session-only). No SQLite migration added (parity, not a regression).
- **In-memory:** `EstateSettings.BlockedExperiences` (`List<UUID>` + `UUID[]` property + `AddBlockedExperience`/`RemoveBlockedExperience`/`BlockedExperiencesCount`), mirroring the Allowed/Key members; cap reuses `Constants.EstateAccessLimits.AllowedExperiences`.

| Item | Legion | Tranquillity (T5b) | Verified |
|---|---|---|---|
| Region-block store | `experience_blocked` (region-scoped) | **estate `BlockedExperiences`** (new estate list/table — NOT Legion's table; TRANQ estate-scoped) | mirror of existing Allowed/Key path |
| Block-wins in `llRequestExperiencePermissions` | `IsExperienceBlockedOnObjectParcel OR IsExperienceBlockedInRegion` → denied **17**, checked FIRST (before admission) | region-only `IsExperienceBlockedInRegion` → denied **17** (`XP_ERROR_NOT_PERMITTED_LAND`), inserted BEFORE the T5 admission check | Legion `LSLSystemAPI.cs:12735-12743` (code 17, first) ✓ |
| Block-wins in `llAgentInExperience` (SS-6) | region/parcel block in `HasExperiencePermission` | region `IsExperienceBlockedInRegion` → return 0, before agent-block/admission/grant | Legion `HasExperiencePermission:12624-12625` ✓ |
| Adapter read | `expService.GetBlockedExperiences(regionId)` | `PhloxExperienceAdapter.GetBlockedExperiences` → `IExperienceModule.GetEstateBlockedExperiences()` → `EstateSettings.BlockedExperiences` | mirror of `GetTrustedExperiences` ✓ |
| Operator set path | Region/Estate ▸ Experiences ▸ Blocked | same panel — viewer already sends estate-access deltas **BLOCKED_ADD (64) / BLOCKED_REMOVE (128)**, previously **DISCARDED**; now handled in `EstateManagementModule.handleEstateExperienceDeltaRequest` (add→`AddBlockedExperience`, remove→`RemoveBlockedExperience`) | mirrors the allowed 16/32 branches ✓ |

**Ladder (verified vs Legion):** `llRequestExperiencePermissions` order is now no-exp(5) → **region-block(17)** → admission(17) → presence(4) → agent-block(4) → granted → trusted silent-grant → dialog. Legion's order is identical minus the parcel-block sub-check folded into its block tier (`IsExperienceBlockedOnObjectParcel || IsExperienceBlockedInRegion`). Tranquillity omits only the parcel term (no source). Denial code **17** confirmed at Legion `LSLSystemAPI.cs:12740`.

**Files:** `OpenSim.Framework/EstateSettings.cs` (blocked field/property + 3 helpers); `OpenSim.Data.MySQL/MySQLEstateData.cs` (load×2 + save) + `Resources/EstateStore.migrations` (VERSION 38); `OpenSim.Region.CoreModules/World/Estate/EstateManagementModule.cs` (delta branches 64/128); `OpenSim.Region.Framework/Interfaces/IExperienceModule.cs` (`GetEstateBlockedExperiences`); `OpenSim.Region.ClientStack.LindenCaps/ExperienceModule.cs` (impl); `Phlox.ScriptEngine/PhloxExperienceAdapter.cs` (`GetBlockedExperiences`) + `LSLSystemAPI.cs` (`IsExperienceBlockedInRegion` helper + block-wins in both gates). Build: full solution, `-p:NoWarn=NU1605`, 0 errors.

**Evidence:** commit `<T5b-HASH>`. Legion ref `port-source-2026-07-22`.

**Non-identical:** block list is estate-scoped (Tranquillity estate model) rather than Legion's region-scoped `experience_blocked` table — TRANQ semantics, first migration of this port (VERSION 38). Parcel-block, OTH-1 agent-parcel scoping, and the grid-wide bit remain absent — **separate project.**

---

## Slice A — cap surface (T6 + T-caps + CAP-RE-ERR) — ✅ PORTED (2026-07-23)

**Ported and code-path-verified against the complete Legion source; NOT executed on this tree.** Caps layer only (`OpenSim.Region.ClientStack.LindenCaps/ExperienceModule.cs`) — sits above the script/permission layers (T1-T5b), no grant/consent/admission behavior touched. **One assembly:** `OpenSim.Region.ClientStack.LindenCaps.dll`. Build: full solution, `-p:NoWarn=NU1605`, 0 errors, no new warnings. Legion reference (all in `CoreModules/Experience/ExperienceModule.cs`): the 14 caps are verified-conformant per `experience-parity-ledger.md` Slices 0-6.

| Item | Finding (Tranquillity before) | Legion reference | Fix | Verified: shape match |
|---|---|---|---|---|
| **A1 ExperienceQuery** | cap **not registered** | `HandleExperienceQuery`:929 — documented NO-OP: per-agent EEP stubbed → answer all queried experiences `true` | added `ExperienceQueryGetHandler` + registration; GET `?experiences=csv` → `{experiences:{<uuid>:true}}` | **Confirmed Tranq per-agent EEP is stubbed** (`LSLSystemAPI.cs:11905/11928` `llSetAgentEnvironment`/`llReplaceAgentEnvironment` log-only) — no-op is correct; not registering was the only gap |
| **A2 IsExperienceContributor** | cap **present + conformant** (owner ∪ `GP_EXPERIENCE_CREATOR`, `{status:bool}`) | `HandleIsExperienceContributor`:912 / `IsAgentExperienceContributor`:899 | **no surface change**; added Legion's null/zero guard to `IsExperienceAdmin`+`IsExperienceContributor` (were NRE-on-unknown-id → 500) | predicate + `{status:bool}` already matched; guard prevents unparseable 500 |
| **A3 FindExperienceByName** | literal `// todo: handle pages`; ignored page/page_size; no next/prev urls | `HandleFindExperienceByName`:584 — 1-based page clamp, page_size 30, `next_page_url`/`previous_page_url` on presence | in-handler paging over the full result set + emit the two page-url keys; quota→128 | keys `experience_keys`/`next_page_url`/`previous_page_url` match; **Tranq SQL has no LIMIT** (unbounded) so paging is exact — no storage change (Legion had to fix a 50-row SQL cap; we don't) |
| **A4 GetExperienceInfo** | quota literal 128 **but** Find/Update emitted `info.quota`=**16**; GetExperienceInfo dropped marketplace (empty `<string/>`) | `ExperienceToOSD`:375 quota **hardcoded 128**; `BuildExtendedMetadata`:409 always emits logo+marketplace | all three handlers now emit **128**; GetExperienceInfo emits real `info.marketplace` like Find/Update | quota consistent 128 across Find/Info/Update = Legion; marketplace/logo now present. **Maturity conversion NOT ported** — Tranq stores 13/21/42 natively, so Legion's `MaturityToSimAccess` would be wrong here (verified: Tranq `maturity` field is already SIM_ACCESS) |
| **A5 UpdateExperience** | admin-gated write present; but **ANY admin could change group_id**; non-admin echoed unchanged (already reject-shaped) | `HandleUpdateExperience`:975 — admin-gated; **group_id OWNER-ONLY**:1010; non-admin → reject-shaped echo:990 | group change now gated on `owner_id == agent` (non-owner admin keeps existing group); quota→128; documented the existing non-admin reject-echo | owner-only group rule matches Legion exactly; non-admin path already echoes unchanged info (Legion-equivalent) |
| **A6 RegionExperiences** | error shape already well-formed; **`blocked` hardcoded `<undef/>`** (T5b store not surfaced) | `BuildRegionExperiencesLLSD`:417 — `blocked` is a real array from `GetBlockedExperiences` | **error shape: confirmed viewer-safe, no change**; wired `blocked` from `GetEstateBlockedExperiences()` (T5b) — was always empty | allowed/blocked/trusted arrays + conditional default/disabled match Legion; T5b blocks now visible in the Region panel |

**Failure mode guarded (item 9):** a 200 the viewer can't parse (key-name mismatch / 500). Every handler emits hand-built LLSD with the exact keys Legion emits (and thus Firestorm reads); A2's null-guard removes the one NRE path. No key renamed; all additions are new keys the viewer reads conditionally.

**No regression to T1-T5b:** caps are read-only over the same estate/experience/permission data; grant/consent/admission ladders (T3-T5b) untouched. A6 surfaces the T5b block store but does not change enforcement.

**Evidence:** commit `<SLICEA-HASH>`. Legion ref `port-source-2026-07-22`.

**Non-identical / notes:** Tranq serves caps via `BaseStreamHandler` classes emitting hand-built LLSD strings (Legion serves inline via `WriteLLSD`) — response *shape* is the conformance contract, matched item-by-item. A2 contributor/admin predicates read live group powers via `ScenePresence.ControllingClient.GetGroupPowers` (caller is in-region) vs Legion's `IGroupsModule.GetMembershipData` — equivalent for the self-querying viewer. Maturity conversion deliberately omitted (native 13/21/42). Per-agent EEP remains stubbed (A1 no-op revisit-if-shipped).

---

## Slice B — acquire policy (T7 / DEC-3) + UNV-6/UNV-7 tail — ✅ PORTED (2026-07-23)

**Ported and code-path-verified against the complete Legion source; NOT executed on this tree.** The last behavioral slice. B1 is a code change (caps layer + one config key); B2/B3 are **verify-and-document** — the ladder already satisfies them from T3/T5/T5b. **One assembly:** `OpenSim.Region.ClientStack.LindenCaps.dll` (+ `OpenSim.ini.example`, not compiled). Build: full solution, `-p:NoWarn=NU1605`, 0 errors, no new warnings. Legion ref (`CoreModules/Experience/ExperienceModule.cs` unless noted).

**B1 — ACQUIRE (T7 / Legion DEC-3, the ONE deliberate deviation from SL).**

| Aspect | Legion | Tranquillity before | Tranquillity now (B1) |
|---|---|---|---|
| Config key | `[Experience] ExperienceCreators` = `EstateManagersAndRegionOwners`(default) \| `Anyone` \| `AdminsOnly` (`:45-48,96-97`) | none | **same key/values/default** — `m_AcquirePolicy` read in `Initialise`; documented in `OpenSim.ini.example` `[Experience]` |
| Policy check | `CanAcquireExperience`:778 — `Anyone`→true, `AdminsOnly`→`IsAdministrator`, default→`Permissions.CanIssueEstateCommand(agent,false)` | n/a | **identical** predicate using `m_scene.Permissions.IsAdministrator`/`CanIssueEstateCommand` (verified present, `Scene.Permissions.cs:836,880`) |
| AgentExperiences handler | GET owned + POST acquire, one cap, method-dispatched (`:737`) | **GET-only** (`AgentExperiencesGetHandler`); POST unhandled | converted to `RegisterSimpleHandler` GET/POST delegate `HandleAgentExperiences`; old GET-only class removed |
| GET semantics | `GetExperiencesByOwner(agent)` (owned) | `GetAgentExperiences(agent)` = `WHERE owner_id=agent` — **already owned** (despite the name) | unchanged owned-list; acquire diff works because the new row is owner=agent |
| Permitted POST | `CreateExperience(owner=agent, enabled+gridwide)` | — | `UpdateExperienceInfo(new ExperienceInfo{ public_id=random, owner_id=agent, name="", properties=Grid })` (`replace into` inserts; Grid bit + Disabled-clear = gridwide+enabled) |
| Not-permitted POST | logs, creates nothing, no `purchase` (`:759-763`) | — | **same** — defensive log, no create |
| `purchase` key | emitted **iff** permitted; PRESENCE enables the viewer Acquire button (`:771`, viewer `llfloaterexperiences.cpp:240`) | never emitted | emitted `<integer>0</integer>` **only when** `CanAcquireExperience` — non-permitted agent's button stays disabled |
| Empty-name / uniqueness | empty name skips Legion's **name**-uniqueness guard → fresh UUID, no collision (`:745-753`) | Tranquillity's guard is on the **KEY** (console create checks existing-by-key; no name guard) | fresh random `public_id` per acquire → no collision; empty name kept as SL behaviour (user names it in the profile) — **name trick not needed here, documented** |

**B2 — UNV-6 root-presence (VERIFY-ONLY, already ported T3/T5).** Legion (`Phlox.ScriptEngine/LSLSystemAPI.cs:12755`): *"SL's exact root-presence requirement undocumented (D-SLTEST); the target agent must have a ROOT presence in this region to be granted. Chosen conservative behavior — deny (code 4) if absent or child agent; can never over-grant."* Tranquillity **already matches** — `llRequestExperiencePermissions:12637-12646` denies code 4 when `sp == null || sp.IsChildAgent` (after admission, before agent-block); `llAgentInExperience` returns 0 for a non-root presence. **No code change.** Carried forward as **DEFERRED-BY-DECISION** (SL behavior unpublished; conservative choice that can't over-grant; verify against live SL if access appears).

**B3 — UNV-7 ladder tie-break (VERIFY-ONLY, already ported T5b).** Legion (`LSLSystemAPI.cs:12727`): *"SL's fine-grain tie-break order undocumented (D-SLTEST); the permission ladder is applied MOST-RESTRICTIVE-WINS in a fixed order — block (parcel/region) BEFORE admission BEFORE agent-block BEFORE grant. A deny at any tier wins, so no combination can over-grant."* Tranquillity's ladder is **identical** minus the parcel-block sub-term (no NGC source): no-exp(5) → region-block(17) → admission(17) → presence(4) → agent-block(4) → granted → trusted silent-grant → dialog. **No code change.** Carried forward as **DEFERRED-BY-DECISION** (unverifiable tie-break; Legion's ordering adopted; live-SL confirmation if access appears).

**Traces (code-path, not executed):**
- *Permitted acquire:* estate mgr POSTs → `CanAcquireExperience`=true → new experience persisted owner=agent → response `experience_ids` includes it + `purchase` present → viewer diffs the new id, opens the profile. ✓ matches Legion.
- *Non-permitted:* normal avatar → `CanAcquireExperience`=false → GET/POST returns owned ids, **no `purchase`** → button disabled; a forced POST creates nothing. ✓
- *`Anyone`:* any agent → true → acquire works. *`AdminsOnly`:* `IsAdministrator` only → non-admin estate mgr denied. ✓ each matches Legion's switch.

**No regression to T1-T5b/Slice A:** B1 touches only the AgentExperiences cap (GET semantics unchanged; POST is new). B2/B3 changed nothing. The consent/admission/block ladders (T3-T5b) are untouched and independently re-read here for the UNV verification.

**Evidence:** commit `<SLICEB-HASH>`. Legion ref `port-source-2026-07-22`.

**Non-identical / notes:** acquire persists via `UpdateExperienceInfo` (`replace into`) rather than a dedicated `CreateExperience` (Tranquillity has none; behavior is equivalent). Uniqueness is key-scoped not name-scoped, so the empty-name is SL-fidelity not a guard-bypass. Policy read once at module init (restart to change) — same as Legion.

---

## Remaining slices (from `experience-port-audit-v2.md` PART D)

| Slice | Scope | State |
|---|---|---|
| GATE-PORT | LibOMV ScriptQuestion Experience block present | ✅ PASSED (pre-work gate) |
| **T1** | script-surface conformance (SS-1..9) | ✅ **PORTED** (this slice) |
| **T2** | KV quota 16→128 MiB + code 11 | ✅ **PORTED** (this slice) |
| **T3** | consent (D1): ScriptQuestion+await, 300s/code 18, trusted-bypass | ✅ **PORTED** (this slice) |
| **T4** | ExperiencePreferences ↔ consent Block loop | ✅ **PORTED** (this slice) |
| **T5** | trusted enforcement + region/parcel block admission | ⚠️ **PARTIAL** — trusted PORTED; region block → **T5b**; parcel block = STOP |
| **T5b** | region-block list (estate-level), block-wins tier | ✅ **PORTED** — resolves the region half of T5's STOP (first schema change: EstateStore VERSION 38); parcel-block + OTH-1 + grid-wide = separate project |
| **Slice A** | cap surface: A1 ExperienceQuery no-op · A2 IsExperienceContributor parity (+guard) · A3 Find pagination · A4 GetExperienceInfo quota/marketplace · A5 UpdateExperience owner-only group · A6 RegionExperiences blocked/error-shape | ✅ **PORTED** (folds in T6 + T-caps + CAP-RE-ERR) |
| **Slice B** | acquire policy (T7 / DEC-3, grid-configurable) + UNV-6 root-presence + UNV-7 tie-break | ✅ **PORTED** — B1 acquire is new; B2/B3 verified already-satisfied (DEFERRED-BY-DECISION, SL-unverified) |
| T8 | SL-UNVERIFIED tail parity (remainder after CAP-RE-ERR / UNV-6 / UNV-7) | ↳ folded — UNV-6/UNV-7 done in Slice B; UNV-1/2/3/4 ride with the deferred KV rework |
| — | `llDeleteKeyValueSL` registration (SS-4 tail) | deferred → with the KV async rework project |

---

# FINAL STATE SUMMARY — Experience port (handoff artifact)

*Written at the close of Slice B, the last behavioral slice. This is the whole-state handoff: anyone picking this up (Mike, Royale, a future John) should be able to understand what was done, how it was verified, what deviates, and what remains — from this section alone.*

## 1. What was ported and verified — the behavioral parity claim

Tranquillity's Second Life **Experience** system was brought to **behavioral parity** with Legion — the verified-SL-conformant reference — across these slices, all committed to `develop`:

- **T1** — script-surface conformance (SS-1..9): the `llGetExperienceDetails` SL layout, the full `XP_ERROR_*` 0-18 table, key-length/property constants.
- **T2** — KV quota raised 16→128 MiB with `XP_ERROR_QUOTA_EXCEEDED` (code 11) enforcement.
- **T3** — real SL **consent**: `ScriptQuestion` + await, 300 s timeout (code 18), trusted-experience silent-grant seam.
- **T4** — the **ExperiencePreferences ↔ consent Block** persistence loop (Allow/Block/Forget round-trips; block wins over a stale grant).
- **T5** — trusted **enforcement** + the admission ladder (estate-allow OR trusted).
- **T5b** — the **region-block** (estate-level `BlockedExperiences`) block-wins tier; the port's **one schema change** (MySQL EstateStore VERSION 38).
- **Slice A** — the cap surface: ExperienceQuery no-op, IsExperienceContributor parity + null-guard, FindExperienceByName pagination, GetExperienceInfo quota/marketplace fixes, UpdateExperience owner-only group, RegionExperiences blocked-list + error-shape.
- **Slice B** — **acquire** (grid-configurable) + the UNV-6/UNV-7 tail.

The resulting permission ladder (both `llRequestExperiencePermissions` and `llAgentInExperience`) is Legion's, tier-for-tier: **no-exp(5) → region-block(17) → admission(17) → root-presence(4) → agent-block(4) → already-granted → trusted silent-grant → consent dialog**, with block/most-restrictive winning at every tie.

## 2. Verification standard

Every slice was **code-path-verified against the complete, SL-conformant Legion source at tag `port-source-2026-07-22`** — handler-by-handler, comparing response shapes, LLSD keys, error codes, and ladder ordering, with Legion `file:line` citations recorded per row above. **Nothing in this port was executed on this tree** (no live grid, no DB, no Docker — a standing safety constraint). The on-grid validation plan — the concrete click-through tests a human runs to confirm behavior — lives in **`experience-port-tests.md`** (per-slice, with the second-avatar and config-restart cases flagged). Builds were full-solution with `-p:NoWarn=NU1605`; each slice ended at 0 errors with no new warning delta.

## 3. The deliberate deviation carried from Legion

**Acquire policy (T7 / DEC-3).** Second Life gates experience *creation* on a paid **Premium** account. Legion deliberately replaced that with a **grid-configurable** policy, and Tranquillity carries the same deviation: `[Experience] ExperienceCreators` = `EstateManagersAndRegionOwners` (default) | `Anyone` | `AdminsOnly`. This is the single intentional departure from SL behavior — chosen, documented, and identical to Legion's knob so operators moving between the two grids see the same setting.

## 4. Where Tranquillity's implementation was KEPT over Legion's (TRANQ-AHEAD — deliberately preserved, not overlooked)

The reconcile principle was **NGC storage is authoritative; translation lives in the Phlox/adapter layer**. Where Tranquillity's own design was sound (or better), it was kept rather than replaced with Legion's storage:

- **Grid-service wire protocol** — Tranquillity's Robust connector topology (Local/Remote + ServerConnector, form-in/XML-out) is retained; Legion is region-local direct-MySQL. Behavior reconciled, transport untouched.
- **Inventory-field script association** — the script's persisted `TaskInventoryItem.ExperienceID` is the SL-canonical source; kept over Legion's in-memory `m_ScriptExperiences` map.
- **Single-table allow-BIT permissions** — one permissions table with `allow BIT` encodes grant (`true`) and block (`false`); no separate `experience_agent_blocked` table. Distinguishable and migration-free.
- **Estate-sourced trusted list** — "trusted" = estate `KeyExperiences`, not a dedicated `experience_trusted` table.

These are **TRANQ-AHEAD**: preserved on purpose. The adapter (`PhloxExperienceAdapter`) is the single seam where any future move toward Legion's exact storage would happen.

## 5. Deferred projects (with reasons)

Two coherent bodies of work were **deliberately not attempted** — each wants one focused pass, not a piecemeal graft:

**(i) KV async model + surface tail.** Tranquillity's key-value store is synchronous (returns `int`) where SL/Legion is asynchronous via the `dataserver` event. This couples together: the async `dataserver` return contract, the `llDataSizeKeyValue (used,total)` list surface, `llDataSizeKeyValueSL`/`llDeleteKeyValueSL` registration, and the UNV-1/2/3/4 KV semantics (key-exists-vs-empty, checked-flag compare-and-set, clamp defaults). These are one interdependent rework — doing them singly would churn the same call sites repeatedly. **Deferred as a unit.**

**(ii) Parcel block/allow + OTH-1 + the grid-wide bit.** Legion's ladder has a **parcel**-scoped block/allow tier and an OTH-1 agent-parcel scoping rule, plus a grid-wide experience bit. Tranquillity has **no NGC data source** for any of these — `ILandObject` has no experience methods, and there is no grid-wide flag. Implementing them needs a **new land-layer subsystem** (per-parcel experience access entries + `ILandObject` API), not a translation. The region-block half was closed in T5b; the parcel half is a separate project. **Deferred pending that subsystem.**

## 6. SL-UNVERIFIED items carried from Legion as documented choices

Where SL's exact behavior is unpublished, Legion made **conservative, can't-over-grant** choices and documented them (D-SLTEST). Tranquillity adopts the same choices, recorded here as **DEFERRED-BY-DECISION** — correct-by-construction, to be confirmed against live SL only if access appears:

- **UNV-6** — root-presence requirement: deny (code 4) an absent/child-agent target rather than risk granting a not-truly-present agent. *(Ported; matches Legion.)*
- **UNV-7** — permission-ladder tie-break: most-restrictive-wins in a fixed order (block → admission → presence → agent-block → grant). *(Ported; matches Legion.)*
- **UNV-1/2/3/4** — KV semantics (exists-vs-empty distinction, checked-flag compare-and-set, clamp defaults): ride with deferred project (i).

**Bottom line:** behavioral parity with Legion is complete for everything both grids can express with Tranquillity's current data model; the two deferred projects are the only gaps, both gated on new storage/subsystems rather than on translation, and both scoped above. The port never changed NGC storage except the single T5b estate migration (VERSION 38).

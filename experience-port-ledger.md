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

## Slice T5 — trusted enforcement + admission ladder — ⚠️ PARTIAL (trusted PORTED; region/parcel block = STOP) (2026-07-23)

**Ported and code-path-verified against Legion; NOT executed on this tree.** Phlox layer only; **no adapter change, no NGC change, no new tables.** Deploy: `Phlox.ScriptEngine.dll` only.

**PORTED — trusted enforcement + the admission tiers Tranquillity has a source for:**
| Item | Legion | Tranquillity source | T5 |
|---|---|---|---|
| Trusted → silent grant | `experience_trusted` table | **estate `KeyExperiences`** (NOT Legion's table) via `GetTrustedExperiences`→`GetEstateKeyExperiences()` | wired in T3, verified |
| Admission includes trusted | `IsExperienceAdmittedAt`: allowed OR trusted OR grid-wide OR parcel-allow | estate allow OR trusted | new `IsExperienceAdmitted` helper — fixes a trusted-but-not-allowed experience being wrongly denied 17 |
| Admission tier on `llAgentInExperience` (SS-6) | `HasExperiencePermission` admission | estate allow OR trusted | added (closes the *admission* half of SS-6's deferred tier) |
| Ordering: agent-block wins over trusted | agent-block (4) before trusted-grant | same | verified — block checked before the trusted silent-grant |

**🛑 STOP — region/parcel BLOCK-wins ladder + grid-wide + OTH-1 parcel scoping: NO NGC SOURCE.** Tranquillity has **no** region/estate blocked-experience store (`EstateSettings` has `AllowedExperiences` + `KeyExperiences` only — no `BlockedExperiences`; the RegionExperiences cap hardcodes `blocked` empty), **no** parcel-experience data (`ILandObject` has no `IsExperienceAllowed`/`IsExperienceBlocked`), and **no** grid-wide bit. Implementing these would require either a **new estate field/table** (region-block — breaks the no-schema-change property this port has held for T1-T4) or a **new land-layer subsystem** (parcel experience access entries + `ILandObject` methods). **Flagged for John's decision — not invented here.** SS-6's BLOCK-wins tier therefore remains deferred.

**Evidence:** commit `<T5-HASH>`. Legion ref `port-source-2026-07-22` `Phlox.ScriptEngine/LSLSystemAPI.cs`: `IsExperienceAdmittedAt`:12661, `IsExperienceBlockedInRegion`:12640, `IsExperienceBlockedOnParcelAt`:12699, OTH-1 agent-parcel `HasExperiencePermission`:12614. Tranquillity gap: `EstateSettings.cs:279-292` (allow+key, no block), `ILandObject.cs` (no experience methods), `IExperienceModule.cs:32-33`.

**Non-identical:** trusted from estate `KeyExperiences` (not Legion's `experience_trusted` table — TRANQ-AHEAD, no migration); the entire BLOCK-wins land tier is absent pending the storage decision above.

---

## Remaining slices (from `experience-port-audit-v2.md` PART D)

| Slice | Scope | State |
|---|---|---|
| GATE-PORT | LibOMV ScriptQuestion Experience block present | ✅ PASSED (pre-work gate) |
| **T1** | script-surface conformance (SS-1..9) | ✅ **PORTED** (this slice) |
| **T2** | KV quota 16→128 MiB + code 11 | ✅ **PORTED** (this slice) |
| **T3** | consent (D1): ScriptQuestion+await, 300s/code 18, trusted-bypass | ✅ **PORTED** (this slice) |
| **T4** | ExperiencePreferences ↔ consent Block loop | ✅ **PORTED** (this slice) |
| **T5** | trusted enforcement + region/parcel block admission | ⚠️ **PARTIAL** — trusted PORTED; region/parcel block = STOP (no NGC source, John decides) |
| T6 | ExperienceQuery no-op cap + IsExperienceContributor parity | pending |
| T7 | acquire policy (DEC-3) | pending |
| T8 | SL-UNVERIFIED tail parity | pending |
| — | `llDeleteKeyValueSL` registration (SS-4 tail) | pending (small follow-up) |

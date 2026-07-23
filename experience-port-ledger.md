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

## Remaining slices (from `experience-port-audit-v2.md` PART D)

| Slice | Scope | State |
|---|---|---|
| GATE-PORT | LibOMV ScriptQuestion Experience block present | ✅ PASSED (pre-work gate) |
| **T1** | script-surface conformance (SS-1..9) | ✅ **PORTED** (this slice) |
| **T2** | KV quota 16→128 MiB + code 11 | ✅ **PORTED** (this slice) |
| T3 | consent (D1): ScriptQuestion + await, 300s/code 18, trusted-bypass | pending |
| T4 | ExperiencePreferences ↔ consent Block loop | pending |
| T5 | trusted enforcement + region/parcel admission ladder (absorbs SS-6 remainder) | pending |
| T6 | ExperienceQuery no-op cap + IsExperienceContributor parity | pending |
| T7 | acquire policy (DEC-3) | pending |
| T8 | SL-UNVERIFIED tail parity | pending |
| — | `llDeleteKeyValueSL` registration (SS-4 tail) | pending (small follow-up) |

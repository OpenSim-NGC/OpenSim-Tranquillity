# Tranquillity Experience Port — Test Plan (needs on-grid validation)

**What this is.** The Experience system in this build was ported, slice by slice, from a **complete, SL-conformance-verified Legion implementation** (`port-source-2026-07-22`) into Tranquillity's Phlox/adapter layer (NGC storage kept authoritative). Every slice was **code-path-verified against the Legion source but NOT executed on the porting machine** (Option-C discipline). **These tests validate the port on a real grid** — please run them and report results (especially any FAIL).

**How to run.** Deploy the built DLLs (per each slice's deploy set), restart, then run the LSL snippets below in-world. Unless noted, tests need a script **compiled under an experience** (LSL editor → "Use Experience" → pick an experience you own). "Owner-say" output is what you read; the **FAIL** line says what a broken port looks like.

**Grid vs standalone.** Everything here works in **standalone (SQLite)** and **grid (MySQL)**. One edge (T2 multi-byte quota) differs by backend — noted there.

---

## T1 — script-surface conformance (deploy: `Phlox.ScriptEngine.dll`)

**T1.1 — error message table (no experience needed; pure function)**
```lsl
default { state_entry() {
    llOwnerSay(llGetExperienceErrorMessage(14)); // EXPECT: "key doesn't exist"
    llOwnerSay(llGetExperienceErrorMessage(17)); // EXPECT: "not allowed to run on this land"
    llOwnerSay(llGetExperienceErrorMessage(18)); // EXPECT: "experience permissions request timed out"
} }
```
**FAIL:** 14 says "key already exists", 17 says "…timed out", or 18 says "unknown error id" (the old shifted/missing table).

**T1.2 — `llGetExperienceDetails` layout + `llReadKeyValueSL` code (experience script)**
```lsl
default { state_entry() {
    list d = llGetExperienceDetails("");            // "" = this script's experience
    llOwnerSay("idx2=" + llList2String(d,2)         // EXPECT idx2 = the experience UUID
             + " idx3=" + llList2String(d,3)        // EXPECT idx3 = 0 (state)
             + " idx4=" + llList2String(d,4));       // EXPECT idx4 = "no error"
    llOwnerSay("readmiss=" + llReadKeyValueSL("no-such-key")); // EXPECT "0,14"
} }
```
**FAIL:** idx2 is the *description* text (old layout), idx3 is a UUID/group, or `readmiss` is `"0,key not found"`/`"0,13"`.

**T1.3 — SS-7 denial code (NON-experience script)**
Put this on a prim **without** "Use Experience":
```lsl
default { state_entry() { llRequestExperiencePermissions(llGetOwner(), ""); }
    experience_permissions_denied(key a, integer r) { llOwnerSay("reason=" + (string)r); } // EXPECT 5
}
```
**FAIL:** reason is 17 (the old wrong "no-experience" code).

---

## T2 — KV quota 128 MiB + code 11 (deploy: `Phlox.ScriptEngine.dll` + `OpenSim.Services.ExperienceService.dll`)

**T2.1 — over-quota write is rejected with code 11 and does NOT write**
Practical check without writing 128 MiB: confirm the SL surface returns `0,11` when the store is at the limit. If you can stage a near-full experience KV store, then:
```lsl
// with the store ~at 128 MiB:
llOwnerSay(llCreateKeyValueSL("k", llGenerateKey())); // EXPECT "0,11" (quota), and the key is NOT created
llOwnerSay(llReadKeyValueSL("k"));                    // EXPECT "0,14" (proves no write happened)
```
**FAIL:** create returns `"0,13"` (old collapsed code) or `"1,…"` (wrote past quota), or the read shows the value (write leaked through).

**T2.2 — under-quota write works normally**
```lsl
llOwnerSay(llCreateKeyValueSL("hello", "world")); // EXPECT "1,world"
```
**FAIL:** spurious `"0,11"` below the limit.

**T2.3 — delta-aware update** — updating an existing key to a *smaller/equal* value must NOT trip the quota even when near-full (it swaps the value, not adds a pair). Update to a larger value counts only the delta.
**FAIL:** an in-place update to the same size is rejected as over-quota.

**T2.4 — `llDataSizeKeyValue`** returns the bytes used; the enforced TOTAL is now **128 MiB** (134217728), not 16 MiB. (Surfacing `used,total` as a script list is deferred with the KV-async slice; the enforced limit is the check that matters.)

> **Standalone (SQLite) note:** the quota "used" size on SQLite is measured in **characters** (`LENGTH()`), while the projected check uses **UTF-8 bytes** — identical for ASCII keys/values, slightly divergent for multi-byte content right at the 128 MiB boundary. On **grid (MySQL)** both are byte-based and match Legion exactly. Test with ASCII to avoid the edge, or note it if you probe multi-byte at the boundary.

---

## T3 — real SL consent (deploy: `Phlox.ScriptEngine.dll`) — the big one

Replaces the old **auto-grant** with the real ScriptQuestion consent dialog.

**T3.1 — consent dialog appears (NOT auto-grant), Yes → granted**
```lsl
default {
    touch_start(integer n) { llRequestExperiencePermissions(llDetectedKey(0), ""); }
    experience_permissions(key a)          { llOwnerSay("GRANTED " + (string)a); }
    experience_permissions_denied(key a, integer r) { llOwnerSay("DENIED " + (string)r); }
}
```
Touch it as a **different avatar** who hasn't accepted the experience. **EXPECT:** the viewer shows the **experience consent dialog** (grid-wide experience participation prompt). Click **Yes** → `GRANTED`.
**FAIL (the old behavior):** no dialog appears and you get `GRANTED` immediately (auto-grant), or you get a normal script-permissions dialog with no experience framing.

**T3.2 — No → denied 4**
Same script; touch and click **No / Deny**. **EXPECT:** `DENIED 4`.
**FAIL:** `GRANTED`, or a different code.

**T3.3 — timeout → denied 18**
Touch, then **ignore the dialog ~5 minutes** (300s). **EXPECT:** `DENIED 18` (the request times out).
**FAIL:** it hangs forever (no timeout), or grants after the wait.

**T3.4 — already-granted → immediate, no dialog**
After T3.1 (granted), touch again. **EXPECT:** immediate `GRANTED`, no dialog.

**T3.5 — agent-block wins over a stale grant** (seam; full Block button is T4)
If the agent has blocked the experience, touching → **EXPECT:** `DENIED 4` (block is checked *before* the already-granted short-circuit — a blocked resident is never re-granted).

**T3.6 — trusted experience → silent grant, no dialog**
Add the experience to the estate's **Trusted/Key experiences** (estate Experiences tab). Touch as a fresh avatar. **EXPECT:** `GRANTED` with **no dialog** (trusted bypasses consent).
**FAIL:** a dialog still appears for a trusted experience.

**T3.7 — LANDMINE REGRESSION: normal (non-experience) permission dialogs still work**
On a prim **without** "Use Experience":
```lsl
default {
    touch_start(integer n) { llRequestPermissions(llDetectedKey(0), PERMISSION_TRIGGER_ANIMATION); }
    run_time_permissions(integer p) { llOwnerSay("perms=" + (string)p); }
}
```
Touch it. **EXPECT:** the **normal** run-time permission dialog appears and `run_time_permissions` fires. **FAIL:** no dialog / broken dialog (this is the bug Legion hit — a Zero-experience block attached to a normal ScriptQuestion; guarded here so it must keep working).

---

## T4 — Block-button persistence loop (deploy: `Phlox.ScriptEngine.dll`)

Completes T3: the consent dialog's **"Block Experience"** button now persists and takes effect. Use the T3.1 script (touch → `llRequestExperiencePermissions`, with `experience_permissions`/`experience_permissions_denied` handlers).

**T4.1 — Block → denied 4**
Touch as a fresh avatar → consent dialog → click **Block Experience**. **EXPECT:** `DENIED 4` (the current request is denied), and the block is persisted.
**FAIL:** `GRANTED`, or a code other than 4.

**T4.2 — persistence proof: re-touch → denied 4 immediately, NO dialog**
After T4.1, touch the SAME experience object again (same avatar). **EXPECT:** `DENIED 4` **with no dialog** — the persisted block is enforced at the gate before any prompt.
**FAIL:** the consent dialog appears again (block didn't persist / isn't enforced), or `GRANTED`.

**T4.3 — stale-grant case (block wins over a prior grant)**
Grant the experience first (touch → **Yes** → `GRANTED`). Then Block it (touch again → **Block**, or use Me▸Experiences ▸ the experience ▸ **Block**). Then touch once more. **EXPECT:** `DENIED 4` — the block is checked *before* the already-granted short-circuit, so a previously-granted experience that is then blocked denies.
**FAIL:** `GRANTED` (the stale grant wins — the block-before-grant ordering regressed).

**T4.4 — unblock → dialog returns**
Me▸Experiences ▸ the experience ▸ **Allow** (or **Forget**). Touch again. **EXPECT:** the consent dialog appears again (Allow → immediate `GRANTED`; Forget → prompt again).
**FAIL:** still `DENIED 4` after unblocking.

**Persistence across restart (grid & standalone):** the block lives in the single permissions table (`allow=false`) and is reloaded into the module cache on the agent's next login — so T4.2 also holds after a region restart (fresh login re-reads the persisted block).

**Consent outcomes recap:** **Yes** → `allow=true` persisted (and cached, so no re-prompt); **No** → transient deny 4 (per-request, NOT persisted — the next touch prompts again); **Block** → `allow=false` persisted (enforced on every later touch).

## T5 — trusted enforcement + admission (deploy: `Phlox.ScriptEngine.dll`)

T5 makes a **trusted** experience grant silently and fixes admission so a trusted-but-not-allowed experience isn't wrongly denied. **Region/parcel BLOCK enforcement is NOT in this slice** — Tranquillity has no source for it (see the note at the end); those tests are marked N/A pending John's decision. Use the T3.1 touch script.

**T5.1 — trusted experience → silent grant, NO dialog**
Add the experience to the estate's **Trusted / Key experiences** (Region/Estate ▸ Experiences ▸ Trusted). Touch as a fresh avatar who hasn't accepted it. **EXPECT:** immediate `GRANTED`, **no consent dialog**.
**FAIL:** a dialog appears, or `DENIED 17`.

**T5.2 — trusted-but-not-allowed still works (the admission fix)**
Trust the experience but do **not** add it to the estate Allowed list. Touch. **EXPECT:** `GRANTED` silently (trusted admits + grants).
**FAIL:** `DENIED 17` (the pre-T5 bug — admission denied before the trusted check).

**T5.3 — non-trusted → dialog (T3 regression check)**
An experience that is estate-**Allowed** but **not** Trusted. Touch. **EXPECT:** the consent dialog appears (T3 behavior intact) — Yes → `GRANTED`.
**FAIL:** silent grant with no dialog (trusted logic leaked to non-trusted), or `DENIED 17`.

**T5.4 — agent-block wins over trusted**
Block the experience (Me▸Experiences ▸ Block), then trust it in the estate, then touch. **EXPECT:** `DENIED 4` — the agent's personal block is checked *before* the trusted silent-grant, so block wins over trusted (Legion's order).
**FAIL:** `GRANTED` (trusted overrode the block — wrong order).

**T5.5 — `llAgentInExperience` admission** — from a granted script, `llAgentInExperience(agent)` returns 1 only if the experience is admitted here (estate allow OR trusted) and the agent is root-present and not blocked; returns 0 if the experience isn't allowed/trusted in this region.

**N/A this slice (the STOP):**
- **Region-blocked experience → denied even if allowed (block-wins):** N/A — Tranquillity has no region/estate blocked-experience store (estate has Allowed + Key only; the RegionExperiences cap hardcodes `blocked` empty).
- **Parcel-block / OTH-1 agent-parcel rule:** N/A — Tranquillity's `ILandObject` has no `IsExperienceAllowed`/`IsExperienceBlocked`; there is no per-parcel experience data.
These need new storage / a land-layer subsystem — **John's decision** (would break the no-schema-change property this port has held). See the T5 STOP in `experience-port-ledger.md`.

**Grid vs standalone:** identical (estate Allowed/Key experiences work the same on both).

## T5b — region-block list (estate-level), block-wins tier (deploy: `OpenSim.Framework.dll`, `OpenSim.Data.MySQL.dll`, `OpenSim.Region.CoreModules.dll`, `OpenSim.Region.Framework.dll`, `OpenSim.Region.ClientStack.LindenCaps.dll`, `Phlox.ScriptEngine.dll`; **MySQL schema: EstateStore VERSION 38**)

T5b adds the estate **BlockedExperiences** list (a third list beside Allowed + Key/Trusted) and wires it as the **block-wins** tier of the admission ladder — a region-blocked experience is denied `17` (`XP_ERROR_NOT_PERMITTED_LAND`) regardless of allow/trusted/prior-grant. Resolves the *region* half of the T5 STOP; **parcel-block + OTH-1 remain a separate project.** Use the T3.1 touch script. Region deploy requires the schema migration (grid: MySQL auto-migrates to VERSION 38 on startup; standalone SQLite does **not** persist estate experience lists — the block list is in-memory for the session, same as Allowed/Key today).

**How the operator sets it:** the viewer's Region/Estate ▸ Experiences panel **Blocked** list — the viewer already sends `ESTATE_ACCESS_BLOCKED_EXPERIENCE_ADD` (64) / `_REMOVE` (128) estate-access deltas, which Tranquillity previously **discarded**; T5b handles them (add/remove on `EstateSettings.BlockedExperiences`, persisted).

**T5b.1 — region-blocked experience → denied 17 (block wins over allow)**
Add the experience to the estate **Allowed** list (so admission would pass), then add it to the estate **Blocked** list. Touch. **EXPECT:** `DENIED 17` — block is checked before admission/trusted/grant.
**FAIL:** `GRANTED`, or the dialog appears (block not enforced / wrong precedence).

**T5b.2 — block wins over trusted**
Add the experience to the estate **Trusted/Key** list (would silent-grant), then **Block** it. Touch as a fresh avatar. **EXPECT:** `DENIED 17` — no silent grant.
**FAIL:** `GRANTED` silently (trusted overrode the region block).

**T5b.3 — block wins over a prior grant (stale grant)**
Grant the experience (touch → **Yes** → `GRANTED`). Then add it to the estate **Blocked** list. Touch again. **EXPECT:** `DENIED 17` — the region block is checked before the already-granted short-circuit.
**FAIL:** `GRANTED` (stale grant wins).

**T5b.4 — unblock → prior behavior returns**
Remove the experience from the estate **Blocked** list (viewer sends BLOCKED_REMOVE / 128). Touch again. **EXPECT:** the pre-block outcome returns — `GRANTED` silently if it's Trusted, or the consent dialog if merely Allowed.
**FAIL:** still `DENIED 17` after unblocking (the REMOVE delta wasn't handled / didn't persist).

**T5b.5 — `llAgentInExperience` region-block** — from a granted script, add the experience to the estate **Blocked** list; `llAgentInExperience(agent)` returns **0** even for a root-present, admitted, previously-granted agent (region block checked before admission and grant).
**FAIL:** returns `1` for a region-blocked experience.

**T5b.6 — persistence across restart (grid only)**
After T5b.1, restart the region. Touch again. **EXPECT:** still `DENIED 17` — the block persisted to `estate_blocked_experiences` (VERSION 38) and reloaded with the estate settings. **Standalone (SQLite):** N/A — estate experience lists are in-memory (Allowed/Key behave the same); the block is lost on restart.
**FAIL (grid):** `GRANTED`/dialog after restart (block didn't persist or didn't reload).

**Delta discard check (regression):** before T5b the BLOCKED_ADD/REMOVE deltas (64/128) were silently dropped by `handleEstateExperienceDeltaRequest`; confirm the panel's Blocked add/remove now actually changes behavior (T5b.1 vs T5b.4).

**Still N/A after T5b (the remaining STOP — separate project):**
- **Parcel-block / parcel-allow / OTH-1 agent-parcel scoping:** N/A — `ILandObject` still has no `IsExperienceAllowed`/`IsExperienceBlocked`; no per-parcel experience data. Deferred.
- **Grid-wide experience bit** (a grid-wide experience running everywhere unless blocked): N/A — no grid-wide flag source. Deferred.

**Grid vs standalone:** block **enforcement** identical; block **persistence** grid-only (MySQL VERSION 38); standalone SQLite keeps the block for the session only (parity with existing Allowed/Key behavior).

### What's deferred (not testable yet)
- **T4** — the ExperiencePreferences **Block button** persistence loop (the profile Allow/Block/Forget that writes the agent-block T3.5 reads). T3 ships the block *check*; T4 ships the write path.
- **T5** — trusted **enforcement** + region/parcel BLOCK-list admission ladder (T3 ships the trusted *grant* seam and the estate-allow admission; region-block enforcement and full admission are T5).
- **KV async model** — `llCreateKeyValue`/etc. are synchronous here (return int) with `…SL` CSV wrappers, vs SL's async `dataserver` return; the `llDataSizeKeyValue (used,total)` list surface and `llDeleteKeyValueSL` ride with that later slice.

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

### What's deferred (not testable yet)
- **T4** — the ExperiencePreferences **Block button** persistence loop (the profile Allow/Block/Forget that writes the agent-block T3.5 reads). T3 ships the block *check*; T4 ships the write path.
- **T5** — trusted **enforcement** + region/parcel BLOCK-list admission ladder (T3 ships the trusted *grant* seam and the estate-allow admission; region-block enforcement and full admission are T5).
- **KV async model** — `llCreateKeyValue`/etc. are synchronous here (return int) with `…SL` CSV wrappers, vs SL's async `dataserver` return; the `llDataSizeKeyValue (used,total)` list surface and `llDeleteKeyValueSL` ride with that later slice.

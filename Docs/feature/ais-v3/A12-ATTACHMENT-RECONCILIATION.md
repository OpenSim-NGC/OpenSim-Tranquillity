# A12 — reconciling COF changes with attachment state: design brief


> **CONFIRMED and ACTED ON, 2026-09-04 (A13).** §2's finding — the detach happened and the deferred appearance
> save was lost — is the cause, and it is fixed: `dc4e417bb3` flushes a pending save on `OnRemovePresence`,
> deployed in merge `bfb50070d8`. Checklist step 10 then passed on a clean run and is closed
> (`A13-STEP10-CLOSED.md`).
>
> **Still outstanding from §4 and §5**, unaffected by that fix: the viewer skips its removal arm entirely when
> `isFullyLoaded()` is false and never retries, and an offline agent has no viewer to reconcile at all — the case
> Phase 2's Robust hosting makes normal. Option **B3, login-time reconciliation**, remains the standing
> recommendation.


**Design only. No behaviour changed this session.**

> **The brief's premise does not survive the evidence, and the correction matters more than the design.**
>
> The brief states: *"AIS removed the COF link; nothing detached the object."* **The object was detached.** What
> was lost is the *appearance record write* that should have recorded the detach — dropped by a five-second
> deferred save that raced the logout. See §2.
>
> That failure has **nothing to do with AIS**. It reproduces on the legacy path, it reproduces on wear as well as
> take-off, and it has been in the tree since long before this branch. AIS made it visible because AIS made
> take-off fast and quiet.
>
> There is still a genuine AIS-shaped gap (§3) — it is just not what broke step 10.

---

## 1. Part 1(a) — how a detach normally happens, and every store it writes

The viewer sends UDP, and the region does the rest:

| # | Step | Where |
|---|---|---|
| 1 | `DetachAttachmentIntoInv` / `ObjectDetach` packet arrives | `LLClientView.cs:8434`, `:8436` → `HandleDetachAttachmentIntoInv :9267`, `HandleObjectDetach :9289` |
| 2 | Raised as `OnDetachAttachmentIntoInv` / `OnObjectDetach` | `LLClientView.cs:89`, `:91` |
| 3 | Subscribed by the attachments module | `AttachmentsModule.cs:975-976` |
| 4 | Resolved to a `SceneObjectGroup` and dispatched | `Client_OnObjectDetach :1480-1491`, `Client_OnDetachAttachmentIntoInv :1493-1510` (matches on `group.FromItemID`) |
| 5 | The detach proper | `DetachSingleAttachmentToInv :892-952` |

**The four stores a detach writes**, all inside `DetachSingleAttachmentToInv`:

| Store | Call | Persisted by |
|---|---|---|
| **A. In-memory appearance** | `sp.Appearance.DetachAttachment(so.FromItemID)` (`:945`) | nothing on its own — memory only |
| **B. The `Avatars` table (`_ap_*` rows)** | `m_scene.AvatarFactory.QueueAppearanceSave(sp.UUID)` (`:947`) | **deferred**; see §2 |
| **C. The ScenePresence's attachment list** | `sp.RemoveAttachment(so)` (`:949`) | memory only |
| **D. The scene object + its inventory asset** | `UpdateDetachedObject(sp, so, scriptedState)` (`:950` → `:1217-1247`) | `m_scene.DeleteSceneObject` (`:1232`) then `UpdateKnownItem` (`:1246`) |

Store **B** is the one that matters here, and it is the only one that is not written synchronously.

## 2. Part 1(b) — what the viewer expects, and what actually failed

### The viewer reconciles attachments itself. It is not asking the server to.

Taking off an attachment does **not** send a detach directly:

```cpp
// LLAppearanceMgr::removeItemsFromAvatar — llappearancemgr.cpp:4204-4232
LLPointer<LLInventoryCallback> cb = new LLUpdateAppearanceOnDestroy(true, true, post_update_func);  // :4214
...
if (item && item->getType() == LLAssetType::AT_OBJECT)
    LL_DEBUGS("Avatar") << "ATT removing attachment " ... ;      // :4220-4223  — logs only
...
removeCOFItemLinks(linked_item_id, cb);                          // :4228
```

`removeCOFItemLinks` (`:3239-3266`) deletes the COF link — `remove_inventory_item(..., true)` for `AT_OBJECT`
(`:3253`), the immediate variant. **That is the `DELETE /item` we saw.** No detach message is sent here.

The detach comes from the **callback**. When the link deletes complete, `LLUpdateAppearanceOnDestroy` runs
`updateAppearanceFromCOF`, which diffs the COF against what is actually worn and acts:

```cpp
// llappearancemgr.cpp:2631-2673
LLAgentWearables::findAttachmentsAddRemoveInfo(obj_items, objects_to_remove, objects_to_retain, items_to_add);
...
// (don't remove attachments until avatar is fully loaded - reduces random attaching/detaching/reattaching at log-on)
if (gAgentAvatarp->isFullyLoaded())                              // :2654
{
    LLAgentWearables::userRemoveMultipleAttachments(objects_to_remove);   // :2656  <- the detach
}
...
LLAgentWearables::userAttachMultipleAttachments(items_to_add);            // :2673  <- the attach
```

So the contract is: **COF is the source of truth, and the viewer — not the server — reconciles the objects to
it.** The server's job is only to accept the link change and then to handle the ordinary detach/attach messages
the viewer sends afterwards.

The opposite direction confirms the same model. When the *server* detaches something, the viewer tidies COF
itself — `unregisterAttachment` (`:4459-4479`) calls `onDetachCompleted` and then `removeCOFItemLinks(item_id)`
(`:4471`). And `getIsProtectedCOFItem` (`:4502-4531`) refuses raw deletion of a COF link — *"force users to
choose 'Detach' or 'Take Off'"* — precisely so the removal goes through `removeItemFromAvatar`, which is the
function above.

**Answer to the question the brief poses:** it is **neither** a missing server behaviour nor a viewer message we
failed to handle. The viewer sent the detach, we handled it, and we then lost the write.

### What actually failed, from the log

| time | event | source |
|---|---|---|
| 14:09:35,236 | `DELETE /item/dfcc1be2-…` → RemoveItem | AIS |
| 14:09:35,267 | `RemoveItem -> 200 _removed_items=[dfcc1be2-…] _updated_category_versions={71c3c184-…:501}` | AIS — **correct** |
| ~14:09:35,3xx | viewer's `updateAppearanceFromCOF` → `userRemoveMultipleAttachments` → UDP detach | viewer |
| 14:09:35,408 | `[ATTACHMENTS MODULE]: Updating asset for attachment c91d9878-…, attachpoint 18` | `UpdateKnownItem` (`AttachmentsModule.cs:1031`) — **the detach's own object save** |
| ~14:09:40 | **logout** (`[JANUS PLUGIN] Detach. Detached` 14:09:40,012) | region |
| 14:09:40,0–,2 | `Updating asset for attachment` for attachpoints **11, 40, 7, 8** | `DeRezAttachments :533-561` |

**Attachpoint 18 is absent from the logout batch.** The dress was no longer attached in the scene — because it
had already been detached five seconds earlier. Yet the database still holds `_ap_18 = 9bec8993-…`.

The reason:

```csharp
public void QueueAppearanceSave(UUID agentid)                    // AvatarFactoryModule.cs:334-342
{
    long timestamp = DateTime.Now.Ticks + Convert.ToInt64(m_savetime * 1000 * 10000);   // m_savetime = 5 (:51)
    m_savequeue[agentid] = timestamp;
    m_updateTimer.Start();
}

private void SaveAppearance(List<UUID> ids)                      // :811-830
{
    foreach (UUID id in ids)
    {
        ScenePresence sp = m_scene.GetScenePresence(id);
        if (sp == null)
            continue;                                            // :818-819  <- the write is DROPPED, silently
        SetAppearanceAssets(id, sp.Appearance);
        m_scene.AvatarService.SetAppearance(id, sp.Appearance);   // :828  <- the ONLY persist for a live avatar
    }
}
```

The detach queued a save for ~14:09:40.4. The avatar logged out at ~14:09:40.0. When the timer fired,
`GetScenePresence` returned null, the loop `continue`d, and **the only write that would have cleared `_ap_18`
never happened**.

**Nothing flushes it at logout.** `DeRezAttachments` (`:533-561`) saves each attachment *object* through
`UpdateDetachedObject`, and never touches `sp.Appearance` or `AvatarService`. `AvatarFactoryModule.cs:828` is the
only appearance persist for a live avatar in the whole tree — the other `SetAppearance` callers are
`RemoteAdminPlugin` and account creation.

**So: a five-second deferred write, dropped without a log line, with no flush on the path that ends the session.**

### Why it looked like an AIS problem

Under AIS, take-off is one small HTTP call and the user is free to log out immediately. The legacy path had the
same race, but a UDP take-off is usually followed by more UDP traffic and a slower user. It is a pre-existing
defect that AIS exposed, in the same way A7's `folders[0]` was a pre-existing fragility.

## 3. Part 1(c) — SL parity

SL has no separate attachment store to fall out of step: the COF *is* the record, and the simulator's attachment
state is derived from it. That is why the viewer is written to reconcile against COF and why LL never needed a
"COF changed → detach" server behaviour.

**Parity therefore does not require us to reconcile at mutation time.** It requires that our derived stores —
`Avatars._ap_*`, `ScenePresence`, the scene object — never disagree with COF once the dust settles. Today they
can, in two ways:

1. the write that records agreement is lost (§2); and
2. nothing reconciles when no viewer is there to do it (§4).

## 4. Part 1(d) — the same gap on wear, on slam, and when nobody is reconciling

**Wear is symmetric.** `AttachmentsModule.cs:1389` sets `sp.Appearance.SetAttachment(...)` and `:1396` queues the
same deferred save. Attach something and log out within five seconds and the record is lost the same way — the
avatar comes back *without* the attachment, the mirror of this bug.

**Slam is the same code path, multiplied.** `PUT /category/current/links` replaces every link, so
`updateAppearanceFromCOF` computes a large `objects_to_remove` / `items_to_add` and issues many detaches and
attaches, each queueing a save that collapses to one timestamp. One logout inside the window loses the lot.

**Two holes the viewer cannot cover at all:**

- **`isFullyLoaded()` is false** (`llappearancemgr.cpp:2654`). The removal arm is skipped entirely and **never
  retried** — the COF link is gone, the object stays attached, and no message is ever sent. This is a real
  second failure mode with the same visible symptom, and no amount of fixing the save will address it.
- **The agent is not logged in.** There is no viewer to reconcile. Today AIS only serves a region-hosted,
  logged-in agent, so this is latent — but **Phase 2 hosts the handler on Robust, where an offline agent is the
  normal case, not the exception**. A COF mutation arriving for an offline agent must still leave the stored
  appearance consistent, or the avatar rezzes wrong on next login and "repairs" itself back to the old outfit.

---

## 5. Part 2 — options

**These are two problems and they want different answers. Do not let the second pay for the first.**

### Problem A — the lost appearance write (the actual cause of step 10)

| Option | Cost | Forecloses |
|---|---|---|
| **A1. Flush the save queue when the presence closes.** Drain `m_savequeue` for that agent on `OnRemovePresence` / client logout, before the ScenePresence is torn down. | Small and local to `AvatarFactoryModule`. One event subscription plus a synchronous save. | Nothing. |
| **A2. Persist synchronously on detach/attach** instead of queueing. | A DB write per attachment operation; a slam becomes N writes. Was presumably why the queue exists. | The batching the queue buys. |
| **A3. Make `SaveAppearance` not need the presence** — capture the `AvatarAppearance` at queue time rather than dereferencing `sp` at fire time. | Small, but changes save semantics: it would persist a snapshot rather than the latest state. | Coalescing later changes into one write. |
| **A4. Log the drop and do nothing else.** | Trivial. | Nothing — but it fixes nothing either. |

**Recommendation: A1, plus the WARN from A4.** It closes the race at the exact point the race exists, keeps the
batching, and needs no new architecture. A3 is a reasonable belt-and-braces addition later.

**What breaks if A1 is wrong:** a flush on logout writes appearance one extra time per session. If the in-memory
appearance were somehow *worse* than the stored one, we would persist the worse one — so the flush must write
only when the queue actually holds an entry for that agent, i.e. only when something really did change.

### Problem B — reconciling COF when no viewer will

This is the one P-2 constrains. **P-2 says the AIS handler may take only an agent id, an `IAisInventoryBackend`
and the request.** Every option below preserves that; they differ in where the knowledge lives.

**First, what the tree already has: nothing.** There is no listener for COF changes anywhere.
`grep FolderType.CurrentOutfit` across `OpenSim.Region.CoreModules` returns two hits, both in
`AvatarFactoryModule` (`:1097`, `:1112`), and both are *writes* — the "Failed Wearable Replacement" path creating
a link. Nothing subscribes to, or notices, a COF change. There is no existing seam to reuse.

| Option | Cost | Forecloses |
|---|---|---|
| **B1. Event/queue the region subscribes to.** The handler publishes "COF changed for agent X" to an abstraction it is given; the region module subscribes and reconciles against the live `ScenePresence`. | A new interface plus a region-side consumer. In-process it is an event; on Robust it needs a real transport. | Nothing structurally — this is the option that survives Phase 2 intact. Handler stays Scene-free. |
| **B2. Narrow seam: one capability, detach/attach by item id for a present agent.** | Smallest code. | **A lot.** It only works for a present agent, so Phase 2 gains nothing, and the moment the interface exists it will grow. It also re-introduces, in spirit, the coupling P-2 exists to prevent. |
| **B3. Reconcile at login.** On presence creation, diff stored appearance against COF and correct. | Cheap, entirely region-side, no handler change at all, and **it is the only option that fixes an offline mutation**. | Leaves a present avatar visibly wrong until relog. |
| **B4. Do nothing; rely on the viewer.** | Free. | Accepts both holes in §4 — the `isFullyLoaded` skip and the offline agent. |

**Recommendation: B3 now, B1 when Phase 2 lands. Not B2.**

- **B3 first** because it is the only option that covers the offline case, which is Phase 2's normal case, and
  because it is a pure region-side addition with no AIS or P-2 impact. It also happens to be a safety net for
  the `isFullyLoaded` skip and for anything else that leaves the stores disagreeing: at every login, COF wins.
- **B1 when the handler actually needs to reach a live region**, i.e. when Phase 2 makes "the region that has
  this agent" a different process. Designing it before then risks building the wrong transport.
- **Not B2.** A capability that only works for a present agent buys nothing for the case that is about to become
  normal, and it spends P-2 to get there.

**What breaks if B3 is wrong:** login-time reconciliation makes COF authoritative over the appearance record.
If COF is ever *itself* wrong — a partial slam, a failed create — we would faithfully reproduce the wrong
outfit and, worse, overwrite a correct appearance record with it. Two guards: reconcile only when the two
disagree, and never strip on an empty or unreadable COF (the same "never trust an empty result" rule that S1e
applied to bake channels). An avatar that logs in with an unreadable COF must keep what it had.

## 6. What this session did not do

No behaviour changed, no test written, nothing deployed, the database untouched. `A-Q17` should be closed with
the §2 finding, and two new items opened — the lost appearance write (Problem A) and the reconciliation gap
(Problem B) — but the brief asked for a design and a stop, so those edits are left for the session that acts.

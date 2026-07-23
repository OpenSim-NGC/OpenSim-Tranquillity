/* Copyright (c) 2026 Legion Grid / Tranquillity integration
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Services.Interfaces;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// Stable seam between Phlox's experience LSL surface and Tranquillity's NGC
    /// experience subsystem.
    ///
    /// Phlox's LSLSystemAPI calls the Legion-shaped experience surface it has always
    /// used (ReadKeyValue/CreateKeyValue→bool/IsAgentGranted/GetExperience/...). This
    /// adapter exposes exactly that surface and maps each call onto Tranquillity's NGC
    /// <see cref="IExperienceService"/> / <see cref="IExperienceModule"/>.
    ///
    /// The NGC service is AUTHORITATIVE for storage and is never modified. All
    /// translation lives here — this is the single place where the team will later
    /// evolve experience behaviour toward Legion/SL semantics (search TODO(legion-semantics)).
    /// Do NOT introduce a second experience store.
    /// </summary>
    public class PhloxExperienceAdapter
    {
        private readonly Scene m_scene;
        private readonly SceneObjectPart m_host;
        private readonly IExperienceService m_service;
        private readonly IExperienceModule m_module;

        public PhloxExperienceAdapter(Scene world, SceneObjectPart host)
        {
            m_scene = world;
            m_host = host;
            m_service = world?.RequestModuleInterface<IExperienceService>();
            m_module = world?.RequestModuleInterface<IExperienceModule>();
        }

        /// <summary>True when at least one NGC experience backend is present.</summary>
        public bool IsAvailable => m_service != null || m_module != null;

        /// <summary>
        /// Minimal Legion-shaped experience record carrying only the fields Phlox reads
        /// (Name/OwnerId/Description/GroupId/Maturity). Mapped from NGC ExperienceInfo
        /// (snake_case) so LSLSystemAPI call sites stay unchanged.
        /// </summary>
        public class PhloxExperienceInfo
        {
            public string Name;
            public UUID OwnerId;
            public string Description;
            public UUID GroupId;
            public int Maturity;
            // Viewer property bitmask (PROPERTY_DISABLED/PROPERTY_PRIVATE/…). Mapped from NGC
            // ExperienceInfo.properties so the Phlox surface (llGetExperienceDetails state) can
            // report enabled/disabled — T1/SS-1.
            public int Properties;
        }

        // ────────────────────────── Key-Value store ──────────────────────────
        // NGC GetKeyValue returns the value or null (same contract as Legion ReadKeyValue).
        // NGC mutators return status strings ("success"/"exists"/"missing"/"mismatch"/"full"/...);
        // Legion expects bool, so we translate "success" => true.

        public string ReadKeyValue(UUID experienceId, string key)
            => m_service?.GetKeyValue(experienceId, key);

        public bool CreateKeyValue(UUID experienceId, string key, string value)
            => m_service != null && m_service.CreateKeyValue(experienceId, key, value) == "success";

        public bool UpdateKeyValue(UUID experienceId, string key, string value, string check)
        {
            if (m_service == null)
                return false;
            // Legion: a non-empty 'check' means compare-and-set against that original value.
            bool doCheck = !string.IsNullOrEmpty(check);
            return m_service.UpdateKeyValue(experienceId, key, value, doCheck, check ?? string.Empty) == "success";
        }

        public bool DeleteKeyValue(UUID experienceId, string key)
            => m_service != null && m_service.DeleteKey(experienceId, key) == "success";

        public int KeyCountKeyValue(UUID experienceId)
            => m_service?.GetKeyCount(experienceId) ?? 0;

        public List<string> KeysKeyValue(UUID experienceId, int start, int count)
        {
            string[] keys = m_service?.GetKeys(experienceId, start, count);
            return keys != null ? new List<string>(keys) : new List<string>();
        }

        public long DataSizeKeyValue(UUID experienceId)
            => m_service?.GetSize(experienceId) ?? 0L;

        // ────────────────────────── Permissions ──────────────────────────

        public bool IsAgentGranted(UUID experienceId, UUID agentId)
        {
            if (m_module != null)
                return m_module.GetExperiencePermission(agentId, experienceId) == ExperiencePermission.Allowed;
            if (m_service != null)
                return m_service.FetchExperiencePermissions(agentId).TryGetValue(experienceId, out bool granted) && granted;
            return false;
        }

        public bool IsAgentBlocked(UUID experienceId, UUID agentId)
        {
            // T4: block enforcement read. The module's per-agent cache (loaded on login, updated on
            // the ExperiencePreferences PUT) is authoritative when it HAS the entry. On a cache MISS
            // (None) — or when only the service is available — consult the service: Tranquillity's
            // single permissions table stores block as allow=FALSE, so a present entry with allow=false
            // is a BLOCK and an ABSENT entry is not-granted. FetchExperiencePermissions returns both,
            // so a persisted block IS distinguishable here (closes the earlier legion-semantics TODO).
            if (m_module != null)
            {
                ExperiencePermission p = m_module.GetExperiencePermission(agentId, experienceId);
                if (p == ExperiencePermission.Blocked) return true;
                if (p == ExperiencePermission.Allowed) return false;
                // p == None -> cache miss; fall through to the service.
            }
            if (m_service != null)
                return m_service.FetchExperiencePermissions(agentId).TryGetValue(experienceId, out bool granted) && !granted;
            return false;
        }

        public bool GrantPermission(UUID experienceId, UUID agentId)
        {
            // Prefer the MODULE's setter: it writes the service AND updates the per-agent cache that
            // the read path (GetExperiencePermission) uses — so an accepted grant is immediately seen
            // as already-granted (no re-prompt on the next request). Service-only would persist the DB
            // but leave the cache stale until the next login reload. (T4 coherency; NGC storage untouched.)
            if (m_module != null)
                return m_module.SetExperiencePermissions(agentId, experienceId, true);
            if (m_service != null)
                return m_service.UpdateExperiencePermissions(agentId, experienceId, ExperiencePermission.Allowed);
            return false;
        }

        // ────────────────────────── Region allow list ──────────────────────────

        public List<UUID> GetAllowedExperiences(UUID regionId)
        {
            // TODO(legion-semantics): Legion's allow-list is per-REGION. NGC exposes estate-scoped
            // (GetEstateAllowedExperiences) and avatar-scoped lists, not a direct per-region list.
            // Estate scope is the closest analogue for gating an experience inside the region.
            UUID[] allowed = m_module?.GetEstateAllowedExperiences();
            return allowed != null ? new List<UUID>(allowed) : new List<UUID>();
        }

        /// <summary>
        /// Region-TRUSTED experiences. Tranquillity's trusted list = the estate "key" experiences
        /// (EstateKeyExperience) — NOT a dedicated table (Legion uses experience_trusted; we do not
        /// port Legion's storage). A trusted experience grants consent silently (no dialog). This is
        /// the T3 consent SEAM; full trusted ENFORCEMENT (admission/consent-bypass) is T5.
        /// </summary>
        public List<UUID> GetTrustedExperiences(UUID regionId)
        {
            UUID[] trusted = m_module?.GetEstateKeyExperiences();
            return trusted != null ? new List<UUID>(trusted) : new List<UUID>();
        }

        // ────────────────────────── Experience metadata ──────────────────────────

        public PhloxExperienceInfo GetExperience(UUID experienceId)
        {
            ExperienceInfo info = m_module?.GetExperienceInfo(experienceId, true);
            if (info == null && m_service != null)
            {
                ExperienceInfo[] infos = m_service.GetExperienceInfos(new[] { experienceId });
                if (infos != null && infos.Length > 0)
                    info = infos[0];
            }
            if (info == null)
                return null;

            return new PhloxExperienceInfo
            {
                Name = info.name,
                OwnerId = info.owner_id,
                Description = info.description,
                GroupId = info.group_id,
                Maturity = info.maturity,
                Properties = info.properties,
            };
        }

        // ────────────────────────── Script ↔ experience association ──────────────────────────

        public UUID GetScriptExperience(UUID scriptItemId)
        {
            // SL-canonical source: the script's task-inventory item carries its ExperienceID,
            // set when the script is compiled under an experience. This replaces Legion's
            // in-memory ExperienceModule.m_ScriptExperiences map with the persisted item field.
            TaskInventoryItem item = m_host?.Inventory?.GetInventoryItem(scriptItemId);
            return item?.ExperienceID ?? UUID.Zero;
        }

        public void InvalidatePermission(UUID experienceId, UUID agentId)
        {
            // TODO(legion-semantics): Legion's ExperienceModule kept a permission cache that this
            // call flushed. The NGC path reads permissions live, so there is nothing to invalidate.
            // Intentionally a no-op — must NOT call ForgetExperiencePermissions (that would REVOKE
            // the grant rather than just drop a cache entry).
        }
    }
}

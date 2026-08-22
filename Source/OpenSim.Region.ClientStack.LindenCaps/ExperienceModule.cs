using System.Reflection;
using Nini.Config;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using Caps = OpenSim.Framework.Capabilities.Caps;
using System.Text;
using System.Collections.Specialized;
using System.Web;
using OpenSim.Framework;
using OpenSim.Services.Interfaces;
using System.Net;

using Microsoft.Extensions.Logging;

namespace OpenSim.Region.ClientStack.LindenCaps;

public class ExperienceModule : IExperienceModule, ISharedRegionModule
{
    private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    // Dictionary of Agent IDs, with a dictionary of experience permissions and their bools
    private Dictionary<UUID, Dictionary<UUID, bool>> m_ExperiencePermissions = new Dictionary<UUID, Dictionary<UUID, bool>>();

    private ExpiringCache<UUID, ExperienceInfo> m_ExperienceInfoCache = new ExpiringCache<UUID, ExperienceInfo>();

    private IExperienceService m_ExperienceService = null;

    private IScriptModule[] m_ScriptModules = null;

    protected Scene m_scene = null;

    private bool m_Enabled = false;

    private int CacheTimeout = 1 * 60;

    // B1 (T7 / Legion DEC-3) — acquire policy: who may create ("Acquire an Experience") via the
    // viewer. SL gates on Premium; the deliberate Legion deviation is grid-configurable. Same key
    // name + values + default as Legion ([Experience] ExperienceCreators) so operators moving
    // between Legion and Tranquillity see the same knob. Values: EstateManagersAndRegionOwners
    // (default) | Anyone | AdminsOnly.
    private string m_AcquirePolicy = "EstateManagersAndRegionOwners";

    public void Initialise(IConfigSource source)
    {
        IConfig config = source.Configs["Experience"];
        if (config == null)
            return;

        if (config != null && config.GetString("Enabled", "false") == "true")
        {
            m_Enabled = true;
        }

        if (!m_Enabled)
            return;

        // B1 — DEC-3 acquire policy (default estate-managers + region-owners), mirroring Legion's
        // [Experience] ExperienceCreators key/values/default.
        m_AcquirePolicy = config.GetString("ExperienceCreators", "EstateManagersAndRegionOwners");

        m_log.LogInformation("[Experience] Plugin enabled!");
    }

    #region ISharedRegionModule

    public void AddRegion(Scene scene)
    {
        if (!m_Enabled)
            return;

        m_scene = scene;

        m_scene.EventManager.OnAvatarEnteringNewParcel += EventManager_OnAvatarEnteringNewParcel;
    }

    private void EventManager_OnAvatarEnteringNewParcel(ScenePresence avatar, int localLandID, UUID regionID)
    {
        UpdateScriptExperiencePerms(avatar, false);
    }

    public void RemoveRegion(Scene scene)
    {
        if (!m_Enabled)
            return;

        scene.EventManager.OnRegisterCaps -= RegisterCaps;
        scene.EventManager.OnNewClient -= OnNewClient;
        scene = null;
    }

    public void RegionLoaded(Scene scene)
    {
        if (!m_Enabled)
            return;

        m_ExperienceService = scene.RequestModuleInterface<IExperienceService>();
        if (m_ExperienceService == null)
        {
            m_log.LogInformation("[EXPERIENCE]: Module disabled becuase IExperienceService was not found!");
            return;
        }

        m_ScriptModules = scene.RequestModuleInterfaces<IScriptModule>();

        scene.RegisterModuleInterface<IExperienceModule>(this);

        scene.EventManager.OnRegisterCaps += RegisterCaps;
        scene.EventManager.OnNewClient += OnNewClient;
    }

    private void OnNewClient(IClientAPI client)
    {
        m_ExperiencePermissions[client.AgentId] = m_ExperienceService.FetchExperiencePermissions(client.AgentId);
    }

    public void PostInitialise() {}

    public void Close() {}

    public string Name { get { return "ExperienceModule"; } }

    public Type ReplaceableInterface
    {
        get { return null; }
    }

    #endregion

    public void RegisterCaps(UUID agent, Caps caps)
    {
        caps.RegisterHandler("GetExperiences", new GetExperiencesGetHandler(agent, this));
        caps.RegisterHandler("GetAdminExperiences", new GetAdminExperiencesGetHandler(agent, this));
        caps.RegisterHandler("GetCreatorExperiences", new GetCreatorExperiencesGetHandler(agent, this));
        // B1 (T7 / Legion Slice 6) — AgentExperiences is GET (Owned tab) AND POST (Acquire).
        // RegisterSimpleHandler method-dispatches both on one cap URL (like ExperiencePreferences),
        // replacing the former GET-only handler. POST creates an experience owned by the agent when
        // the acquire policy permits; the `purchase` key (emitted only for permitted agents) is what
        // enables the viewer's Acquire button (llfloaterexperiences.cpp:240).
        caps.RegisterSimpleHandler("AgentExperiences",
            new SimpleStreamHandler(string.Format("/caps/{0}", UUID.Random()), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            {
                HandleAgentExperiences(httpRequest, httpResponse, agent);
            }));
        caps.RegisterHandler("GetExperienceInfo", new GetExperienceInfoGetHandler(agent, this));
        caps.RegisterHandler("IsExperienceAdmin", new IsExperienceAdminGetHandler(agent, this));
        caps.RegisterHandler("IsExperienceContributor", new IsExperienceContributorGetHandler(agent, this));
        caps.RegisterHandler("RegionExperiences", new RegionExperiencesGetHandler(agent, this));
        caps.RegisterHandler("UpdateExperience", new UpdateExperiencePostHandler(agent, this));
        caps.RegisterHandler("GetMetadata", new GetMetadataPostHandler(agent, this, m_scene));
        caps.RegisterHandler("GroupExperiences", new GroupExperiencesGetHandler(agent, this));
        caps.RegisterHandler("FindExperienceByName", new FindExperienceByNameGetHandler(agent, this));
        // A1 (Legion Slice 6, D-EEP documented NO-OP): SL uses ExperienceQuery to clear
        // experience-driven per-agent ENVIRONMENT injections that are no longer permitted on the
        // agent's new parcel. Tranquillity's per-agent EEP (llSetAgentEnvironment/
        // llReplaceAgentEnvironment) is STUBBED (log-only), so no injection ever exists to police —
        // we answer every queried experience as permitted (true), so the viewer clears nothing.
        // Matches Legion CoreModules/Experience/ExperienceModule.cs:929 HandleExperienceQuery.
        caps.RegisterHandler("ExperienceQuery", new ExperienceQueryGetHandler(agent, this));

        caps.RegisterSimpleHandler("ExperiencePreferences",
            new SimpleStreamHandler(string.Format("/caps/{0}", UUID.Random()), delegate (IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
            {
                HandleExperiencePreferences(httpRequest, httpResponse, agent);
            }));
    }


    private void HandleExperiencePreferences(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
    {
        switch (request.HttpMethod)
        {
            case "PUT":
                HandlePutExperiencePreferences(request, response, agentID);
                return;
            case "GET":
                HandleGetExperiencePreferences(request, response, agentID);
                return;
            case "DELETE":
                HandleDeleteExperiencePreferences(request, response, agentID);
                return;
            default:
                {
                    response.StatusCode = (int)HttpStatusCode.MethodNotAllowed;
                    return;
                }
        }
    }

    private void HandleDeleteExperiencePreferences(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
    {
        byte[] response_bytes = new byte[0];

        string[] split = request.Url.ToString().Split(new[] { request.UriPath.ToString() }, StringSplitOptions.None);

        if (split.Length == 2)
        {
            string key_str = split[1].StartsWith("?") ? split[1].Remove(0, 1) : split[1];

            UUID experience_id = UUID.Parse(key_str);

            ForgetExperiencePermissions(agentID, experience_id);

            string response_str = "<llsd><map><key>blocked</key><undef /><key>experiences</key><undef /></map></llsd>";

            response_bytes = Encoding.UTF8.GetBytes(response_str);
        }

        response.RawBuffer = response_bytes;
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    private void HandleGetExperiencePreferences(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
    {
        byte[] response_bytes = new byte[0];

        string[] split = request.Url.ToString().Split(new[] { request.UriPath.ToString() }, StringSplitOptions.None);

        if (split.Length == 2)
        {
            string key_str = split[1].StartsWith("?") ? split[1].Remove(0, 1) : split[1];

            UUID experience_id = UUID.Parse(key_str);

            ExperiencePermission experiencePermission = GetExperiencePermission(agentID, experience_id);

            string response_str = "<llsd><map><key>blocked</key><array>" +
                (experiencePermission == ExperiencePermission.Blocked ? string.Format("<uuid>{0}</uuid>", experience_id) : "<undef />") +
                "</array><key>experiences</key><array>" +
                (experiencePermission == ExperiencePermission.Allowed ? string.Format("<uuid>{0}</uuid>", experience_id) : "<undef />") +
                "</array></map></llsd>";

            response_bytes = Encoding.UTF8.GetBytes(response_str);
        }

        response.RawBuffer = response_bytes;
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    private void HandlePutExperiencePreferences(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
    {
        OSDMap map = (OSDMap)OSDParser.DeserializeLLSDXml(request.InputStream);

        byte[] response_bytes = new byte[0];

        if (map.Keys.Count == 1)
        {
            string first_key = map.Keys.First();
            UUID experience_id = UUID.Parse(first_key);

            OSDMap m = (OSDMap)map[first_key];

            if (m.ContainsKey("permission"))
            {
                bool allowed = m["permission"].AsString() == "Allow";

                SetExperiencePermissions(agentID, experience_id, allowed);

                string response_str = "<llsd><map><key>blocked</key><array>" +
                (allowed == false ? string.Format("<uuid>{0}</uuid>", experience_id) : "<undef />") +
                    "</array><key>experiences</key><array>" +
                (allowed ? string.Format("<uuid>{0}</uuid>", experience_id) : "<undef />") +
                    "</array></map></llsd>";

                response_bytes = Encoding.UTF8.GetBytes(response_str);
            }
        }

        response.RawBuffer = response_bytes;
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    // B1 (T7 / Legion DEC-3) — AgentExperiences GET/POST. GET returns the agent's OWNED experiences
    // (viewer Owned tab). POST is "Acquire an Experience": when the acquire policy permits, create a
    // fresh experience owned by the agent; the viewer snapshots its owned ids, POSTs, diffs the
    // returned experience_ids, and opens the profile (edit mode) on the new id for the user to name
    // it (llfloaterexperiences.cpp:246-264). Mirrors Legion HandleAgentExperiences
    // (CoreModules/Experience/ExperienceModule.cs:737).
    private void HandleAgentExperiences(IOSHttpRequest request, IOSHttpResponse response, UUID agentID)
    {
        bool canAcquire = CanAcquireExperience(agentID);

        if (request.HttpMethod == "POST")
        {
            if (canAcquire)
            {
                // Fresh random public_id per acquire -> a brand-new experience row. Tranquillity's
                // uniqueness guard is on the KEY, so — unlike Legion, which uses an EMPTY name to skip
                // a NAME-uniqueness guard — no name trick is needed here (two acquirers never collide
                // on the random UUID). Empty name is still correct SL behaviour: the user names it in
                // the profile the viewer opens. Grid-wide + enabled (Disabled bit clear), owner = agent.
                ExperienceInfo created = new ExperienceInfo
                {
                    public_id = UUID.Random(),
                    owner_id = agentID,
                    name = string.Empty,
                    properties = (int)ExperienceFlags.Grid
                };
                ExperienceInfo stored = UpdateExperienceInfo(created);
                if (stored != null)
                    m_log.LogInformation("[EXPERIENCE]: agent {0} acquired experience {1} (policy={2})",
                        agentID, created.public_id, m_AcquirePolicy);
                else
                    m_log.LogWarning("[EXPERIENCE]: agent {0} acquire failed to persist (policy={1})",
                        agentID, m_AcquirePolicy);
            }
            else
            {
                // Defensive: a non-permitted POST creates nothing (the viewer already keeps the button
                // disabled via the missing `purchase` key; this guards a hand-crafted request).
                m_log.LogWarning("[EXPERIENCE]: agent {0} not permitted to acquire an experience (policy={1}) — no create",
                    agentID, m_AcquirePolicy);
            }
        }

        UUID[] owned = GetAgentExperiences(agentID); // owner_id = agent (the agent's OWNED experiences)

        string response_str = "<llsd><map><key>experience_ids</key>";
        if (owned.Length > 0)
        {
            response_str += "<array>";
            foreach (UUID id in owned)
                response_str += string.Format("<uuid>{0}</uuid>", id);
            response_str += "</array>";
        }
        else response_str += "<undef />";

        // The `purchase` key's PRESENCE (not its value) enables the viewer's Acquire button
        // (llfloaterexperiences.cpp:240 enableButton(content.has("purchase"))). Emit it ONLY for a
        // permitted agent so a non-permitted agent's button stays disabled. 0 = no cost.
        if (canAcquire)
            response_str += "<key>purchase</key><integer>0</integer>";

        response_str += "</map></llsd>";

        response.RawBuffer = Encoding.UTF8.GetBytes(response_str);
        response.StatusCode = (int)HttpStatusCode.OK;
    }

    // B1 — DEC-3 acquire policy check. Default EstateManagersAndRegionOwners uses the estate-command
    // gate (estate owner/manager OR god), matching Legion CanAcquireExperience
    // (CoreModules/Experience/ExperienceModule.cs:778). Grid-configurable via [Experience]
    // ExperienceCreators; Anyone -> always, AdminsOnly -> god/administrator only.
    private bool CanAcquireExperience(UUID agentId)
    {
        switch (m_AcquirePolicy)
        {
            case "Anyone": return true;
            case "AdminsOnly": return m_scene.Permissions.IsAdministrator(agentId);
            case "EstateManagersAndRegionOwners":
            default: return m_scene.Permissions.CanIssueEstateCommand(agentId, false);
        }
    }

    #region IExperienceModule

    public ExperiencePermission GetExperiencePermission(UUID avatar_id, UUID experience_id)
    {
        if(m_ExperiencePermissions.ContainsKey(avatar_id))
        {
            if(m_ExperiencePermissions[avatar_id].ContainsKey(experience_id))
            {
                return m_ExperiencePermissions[avatar_id][experience_id] ? ExperiencePermission.Allowed : ExperiencePermission.Blocked;
            }
        }
        return ExperiencePermission.None;
    }

    public bool SetExperiencePermissions(UUID avatar_id, UUID experience_id, bool allow)
    {
        bool updated = m_ExperienceService.UpdateExperiencePermissions(avatar_id, experience_id, allow ? ExperiencePermission.Allowed : ExperiencePermission.Blocked);
        if(updated)
        {
            if (m_ExperiencePermissions.ContainsKey(avatar_id) == false)
                m_ExperiencePermissions.Add(avatar_id, new Dictionary<UUID, bool>());

            m_ExperiencePermissions[avatar_id][experience_id] = allow;

            if (!allow)
            {
                ScenePresence scenePresence;
                if (m_scene.TryGetScenePresence(avatar_id, out scenePresence))
                {
                    UpdateScriptExperiencePerms(scenePresence, true);
                }
            }
        }
        return updated;
    }

    public bool ForgetExperiencePermissions(UUID avatar_id, UUID experience_id)
    {
        if (m_ExperiencePermissions.ContainsKey(avatar_id))
        {
            if (m_ExperiencePermissions[avatar_id].ContainsKey(experience_id))
            {
                bool updated = m_ExperienceService.UpdateExperiencePermissions(avatar_id, experience_id, ExperiencePermission.None);
                if(updated)
                {
                    m_ExperiencePermissions[avatar_id].Remove(experience_id);
                }
                return updated;
            }
        }
        return false;
    }

    public UUID[] GetAllowedExperiences(UUID avatar_id)
    {
        if (m_ExperiencePermissions.ContainsKey(avatar_id))
        {
            List<UUID> allowed_experiences = new List<UUID>();
            foreach(var x in m_ExperiencePermissions[avatar_id])
            {
                if(x.Value)
                {
                    allowed_experiences.Add(x.Key);
                }
            }
            return allowed_experiences.ToArray();
        }
        return new UUID[0];
    }

    public UUID[] GetBlockedExperiences(UUID avatar_id)
    {
        if (m_ExperiencePermissions.ContainsKey(avatar_id))
        {
            List<UUID> allowed_experiences = new List<UUID>();
            foreach (var x in m_ExperiencePermissions[avatar_id])
            {
                if (x.Value == false)
                {
                    allowed_experiences.Add(x.Key);
                }
            }
            return allowed_experiences.ToArray();
        }
        return new UUID[0];
    }

    public UUID[] GetAgentExperiences(UUID agent_id)
    {
        return m_ExperienceService.GetAgentExperiences(agent_id);
    }

    public ExperienceInfo GetExperienceInfo(UUID experience_id, bool fetch)
    {
        if (!fetch && m_ExperienceInfoCache.Contains(experience_id))
        {
            return (ExperienceInfo)m_ExperienceInfoCache[experience_id];
        }

        ExperienceInfo[] infos = m_ExperienceService.GetExperienceInfos(new UUID[] { experience_id });
        if (infos.Length == 1)
        {
            m_ExperienceInfoCache.AddOrUpdate(experience_id, infos[0], CacheTimeout);
            return infos[0];
        }
        else return null;
    }

    public ExperienceInfo[] GetExperienceInfos(UUID[] experience_ids, bool fetch)
    {
        List<ExperienceInfo> infos = new List<ExperienceInfo>();
        List<UUID> missing = new List<UUID>();

        if (!fetch)
        {
            foreach (var key in experience_ids)
            {
                ExperienceInfo info;
                if (m_ExperienceInfoCache.TryGetValue(key, out info))
                {
                    infos.Add(info);
                }
                else
                {
                    missing.Add(key);
                }
            }
        }
        else missing.AddRange(experience_ids);

        ExperienceInfo[] retrieved = m_ExperienceService.GetExperienceInfos(missing.ToArray());

        foreach(var info in retrieved)
        {
            m_ExperienceInfoCache.AddOrUpdate(info.public_id, info, CacheTimeout);
        }

        infos.AddRange(retrieved);

        return infos.ToArray();
    }

    public bool SetExperiencePermission(UUID avatar_id, UUID experience_id, ExperiencePermission perm)
    {
        if (perm == ExperiencePermission.None)
            ForgetExperiencePermissions(avatar_id, experience_id);
        else
            SetExperiencePermissions(avatar_id, experience_id, perm == ExperiencePermission.Allowed);
        return true;
    }

    public ExperienceInfo[] FindExperiencesByName(string query)
    {
        return m_ExperienceService.FindExperiencesByName(query);
    }

    public UUID[] GetGroupExperiences(UUID group_id)
    {
        return m_ExperienceService.GetGroupExperiences(group_id);
    }

    public ExperienceInfo UpdateExperienceInfo(ExperienceInfo info)
    {
        ExperienceInfo updated = m_ExperienceService.UpdateExperienceInfo(info);
        if(updated != null)
        {
            m_ExperienceInfoCache.AddOrUpdate(updated.public_id, updated, CacheTimeout);
        }
        return updated;
    }

    public UUID[] GetAdminExperiences(UUID agent_id)
    {
        var experiences = new List<UUID>();

        experiences.AddRange(GetAgentExperiences(agent_id));

        List<UUID> groups = new List<UUID>();

        var presence = m_scene.GetScenePresence(agent_id);
        if(presence != null)
        {
            var powers = presence.ControllingClient.GetGroupPowers();
            foreach(var pair in powers)
            {
                if((pair.Value & (ulong)GroupPowers.ExperienceAdmin) != 0)
                {
                    groups.Add(pair.Key);
                }
            }
        }

        if (groups.Count == 0)
            return experiences.ToArray();

        var fetched_groups = m_ExperienceService.GetExperiencesForGroups(groups.ToArray());
        return experiences.Union(fetched_groups).ToArray();
    }

    public UUID[] GetConributorExperiences(UUID agent_id)
    {
        var experiences = new List<UUID>();

        experiences.AddRange(GetAgentExperiences(agent_id));

        List<UUID> groups = new List<UUID>();

        var presence = m_scene.GetScenePresence(agent_id);
        if (presence != null)
        {
            var powers = presence.ControllingClient.GetGroupPowers();
            foreach (var pair in powers)
            {
                if ((pair.Value & (ulong)GroupPowers.ExperienceCreator) != 0)
                {
                    groups.Add(pair.Key);
                }
            }
        }

        if (groups.Count == 0)
            return experiences.ToArray();

        var fetched_groups = m_ExperienceService.GetExperiencesForGroups(groups.ToArray());
        return experiences.Union(fetched_groups).ToArray();
    }

    public bool IsExperienceAdmin(UUID agent_id, UUID experience_id)
    {
        ExperienceInfo info = GetExperienceInfo(experience_id, true);
        // A2 — guard an unresolved experience (unknown id) so the caps that surface this predicate
        // return {status:false} rather than NRE'ing into a 500 the viewer can't parse. Matches
        // Legion IsAgentExperienceAdmin (CoreModules/Experience/ExperienceModule.cs:887).
        if (info == null || agent_id == UUID.Zero)
            return false;
        if (info.owner_id == agent_id)
            return true;

        if(info.group_id != UUID.Zero)
        {
            var presence = m_scene.GetScenePresence(agent_id);
            if (presence != null)
            {
                var powers = presence.ControllingClient.GetGroupPowers(info.group_id);
                if ((powers & (ulong)GroupPowers.ExperienceAdmin) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public bool IsExperienceContributor(UUID agent_id, UUID experience_id)
    {
        ExperienceInfo info = GetExperienceInfo(experience_id, true);
        // A2 — same unresolved-experience guard as IsExperienceAdmin (Legion
        // IsAgentExperienceContributor:901). owner ∪ GP_EXPERIENCE_CREATOR predicate below already
        // matches Legion; the cap surface ({status:bool}) is unchanged (already conformant).
        if (info == null || agent_id == UUID.Zero)
            return false;
        if (info.owner_id == agent_id)
            return true;

        if (info.group_id != UUID.Zero)
        {
            var presence = m_scene.GetScenePresence(agent_id);
            if (presence != null)
            {
                var powers = presence.ControllingClient.GetGroupPowers(info.group_id);
                if ((powers & (ulong)GroupPowers.ExperienceCreator) != 0)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public UUID[] GetEstateAllowedExperiences()
    {
        return m_scene.RegionInfo.EstateSettings.AllowedExperiences;
    }

    public UUID[] GetEstateKeyExperiences()
    {
        return m_scene.RegionInfo.EstateSettings.KeyExperiences;
    }

    public UUID[] GetEstateBlockedExperiences()
    {
        return m_scene.RegionInfo.EstateSettings.BlockedExperiences;
    }


    // These need to be added to the existing AccessList enum!
    public const int ACCESS_LIST_ALLOWED = 8;

    private void UpdateScriptExperiencePerms(ScenePresence avatar, bool via_agent)
    {
        var land = m_scene.LandChannel.GetLandObject(avatar.AbsolutePosition);

        var estate_experiences = m_scene.RegionInfo.EstateSettings.AllowedExperiences.Union(m_scene.RegionInfo.EstateSettings.KeyExperiences);

        var parcel_experiences = land.LandData.ParcelAccessList.Where(x => (int)x.Flags == ACCESS_LIST_ALLOWED).Select(x => x.AgentID);

        var agent_allowed = m_ExperiencePermissions.ContainsKey(avatar.UUID) ? m_ExperiencePermissions[avatar.UUID].Where(x => x.Value == true).Select(x => x.Key) : Enumerable.Empty<UUID>();

        var allowed = estate_experiences.Union(parcel_experiences).Where(x => agent_allowed.Contains(x));

        m_scene.ForEachSOG(sog =>
        {
            sog.ForEachPart(part =>
            {
                foreach (TaskInventoryItem item in part.Inventory.GetInventoryItems())
                {
                    // Todo: fix the enum and make a constant for the perm mask
                    if (item.PermsMask == 408628 && item.PermsGranter == avatar.UUID)
                    {
                        if (!allowed.Contains(item.ExperienceID))
                        {
                            item.PermsGranter = UUID.Zero;
                            item.PermsMask = 0;

                            foreach (var e in m_ScriptModules)
                            {
                                e.PostScriptEvent(item.ItemID, "experience_permissions_denied", new Object[] {
                                    avatar.UUID.ToString(),
                                    // I've decided to just hard code the ints rather than include Shared.Api.Runtime in LindenCaps
                                    via_agent ? 4 /*ScriptBaseClass.XP_ERROR_NOT_PERMITTED*/ : 17 /*ScriptBaseClass.XP_ERROR_NOT_PERMITTED_LAND*/
                                });
                            }
                        }
                    }
                }
            });


            if (sog.IsAttachment && sog.AttachedExperienceID != UUID.Zero)
            {
                if (!allowed.Contains(sog.AttachedExperienceID))
                {
                    m_scene.AttachmentsModule.DetachSingleAttachmentToInv(avatar, sog);
                }
            }
        });
    }

    public bool IsExperienceEnabled(UUID experience_id)
    {
        ExperienceInfo info = GetExperienceInfo(experience_id, false);
        if(info != null)
        {
            return (info.properties & (int)(ExperienceFlags.Disabled | ExperienceFlags.Suspended)) == 0;
        }
        return false;
    }

    public string GetKeyValue(UUID experience, string key)
    {
        return m_ExperienceService.GetKeyValue(experience, key);
    }

    public string CreateKeyValue(UUID experience, string key, string value)
    {
        return m_ExperienceService.CreateKeyValue(experience, key, value);
    }

    public string UpdateKeyValue(UUID experience, string key, string val, bool check, string original)
    {
        return m_ExperienceService.UpdateKeyValue(experience, key, val, check, original);
    }

    public string DeleteKey(UUID experience, string key)
    {
        return m_ExperienceService.DeleteKey(experience, key);
    }

    public int GetKeyCount(UUID experience)
    {
        return m_ExperienceService.GetKeyCount(experience);
    }

    public string[] GetKeys(UUID experience, int start, int count)
    {
        return m_ExperienceService.GetKeys(experience, start, count);
    }

    public int GetSize(UUID experience)
    {
        return m_ExperienceService.GetSize(experience);
    }

    #endregion
}

#region Cap HTTP Handlers

public class FindExperienceByNameGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public FindExperienceByNameGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public FindExperienceByNameGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        //m_log.LogInformation("[EXPERIENCE] FindExperienceByName path = {0}", path);

        NameValueCollection query = HttpUtility.ParseQueryString(httpRequest.Url.Query);

        string query_str = query.Get("query") ?? string.Empty;

        // A3 — pagination (Legion Slice 0, CoreModules/Experience/ExperienceModule.cs:584
        // HandleFindExperienceByName). The picker's `page` param is 1-BASED: it sends page=1 for
        // the first search and onPage() clamps to >=1 (llpanelexperiencepicker.cpp:443-446).
        // Treating it as 0-based dropped every result. Clamp <1 to page one; page_size defaults 30.
        int page = 0, page_size = 30;
        int.TryParse(query.Get("page"), out page);
        if (!int.TryParse(query.Get("page_size"), out page_size) || page_size <= 0) page_size = 30;
        if (page < 1) page = 1;

        // Tranquillity's data-layer FindExperiences has no SQL LIMIT (returns every match), so we
        // page over the full set in-handler — no row is unreachable (Legion had to fix a 50-row
        // SQL cap; we don't, honoring "no storage change"). Start of this page, one window wide.
        ExperienceInfo[] results = m_ExperienceModule.FindExperiencesByName(query_str)
            ?? new ExperienceInfo[0];
        int start = (page - 1) * page_size;
        int end = start + page_size;
        if (end > results.Length) end = results.Length;
        bool hasNext = results.Length > page * page_size;

        string new_str = "<?xml version=\"1.0\" ?><llsd><map><key>experience_keys</key><array>";

        for (int i = start; i < end; i++)
        {
            ExperienceInfo info = results[i];
            string extended_meta = string.Format("<llsd><map><key>logo</key><uuid>{0}</uuid><key>marketplace</key>{1}</map></llsd>", info.logo, info.marketplace != string.Empty ? string.Format("<string>{0}</string>", info.marketplace) : "<string />");

            new_str += string.Format("<map>" +
                "<key>public_id</key><uuid>{0}</uuid>" +
                "<key>description</key><string>{1}</string>" +
                "<key>name</key><string>{2}</string>" +
                "<key>quota</key><integer>128</integer>" +
                "<key>slurl</key><string>{6}</string>" +
                "<key>maturity</key><integer>{7}</integer>" +
                "<key>expiration</key><integer>600</integer>" +
                "<key>extended_metadata</key><string>{5}</string>" +
                "<key>group_id</key><uuid>{3}</uuid>" +
                "<key>properties</key><integer>{8}</integer>" +
                "<key>agent_id</key><uuid>{4}</uuid>" +
                "</map>", info.public_id, info.description, info.name, info.group_id, info.owner_id, HttpUtility.HtmlEncode(extended_meta), info.slurl, info.maturity, info.properties);
        }

        new_str += "</array>";

        // The picker enables its page buttons purely on the PRESENCE of these keys
        // (llpanelexperiencepicker.cpp:248-249) and re-requests via ?page=N±1; the values are
        // never dereferenced, but we emit real re-query URLs for honesty (Legion parity).
        string basePath = httpRequest.Url.AbsolutePath;
        if (hasNext)
            new_str += string.Format("<key>next_page_url</key><string>{0}</string>",
                HttpUtility.HtmlEncode(PageUrl(basePath, query_str, page + 1, page_size)));
        if (page > 1)
            new_str += string.Format("<key>previous_page_url</key><string>{0}</string>",
                HttpUtility.HtmlEncode(PageUrl(basePath, query_str, page - 1, page_size)));

        new_str += "</map></llsd>";

        return Encoding.UTF8.GetBytes(new_str);
    }

    private static string PageUrl(string basePath, string query, int page, int pageSize)
    {
        return (basePath ?? string.Empty) + "?page=" + page + "&page_size=" + pageSize +
               "&query=" + Uri.EscapeDataString(query ?? string.Empty);
    }
}

// A1 — ExperienceQuery cap (Legion Slice 6, D-EEP resolved as a documented NO-OP).
// SL calls this (llenvironment.cpp testExperiencesOnParcelCoro) to learn which of a set of
// experiences may still inject a per-agent ENVIRONMENT on the agent's current parcel; a
// `false` answer makes the viewer clearInjections(). Tranquillity's per-agent EEP
// (llSetAgentEnvironment / llReplaceAgentEnvironment) is STUBBED (log-only — see
// Phlox.ScriptEngine/LSLSystemAPI.cs), so no injection ever exists to police. We answer every
// queried experience as permitted (true) so the viewer clears nothing — identical behaviour to
// Legion HandleExperienceQuery (CoreModules/Experience/ExperienceModule.cs:929). GET
// ?experiences=<uuid>,<uuid>,... -> { experiences: { <uuid>: true, ... } }.
public class ExperienceQueryGetHandler : BaseStreamHandler
{
    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public ExperienceQueryGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public ExperienceQueryGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        NameValueCollection query = HttpUtility.ParseQueryString(httpRequest.Url.Query);
        string csv = query.Get("experiences");

        string response_str = "<?xml version=\"1.0\" ?><llsd><map><key>experiences</key><map>";
        if (!string.IsNullOrEmpty(csv))
        {
            foreach (string part in csv.Split(','))
            {
                UUID id;
                if (UUID.TryParse(part.Trim(), out id))
                    response_str += string.Format("<key>{0}</key><boolean>true</boolean>", id);
            }
        }
        response_str += "</map></map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class GroupExperiencesGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public GroupExperiencesGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public GroupExperiencesGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        string response_str = "<llsd><map><key>experience_ids</key><array>";

        if (httpRequest.Query.ContainsKey(""))
        {
            UUID group_id = UUID.Parse(httpRequest.Query[""].ToString());

            UUID[] experiences = m_ExperienceModule.GetGroupExperiences(group_id);

            foreach(UUID id in experiences)
            {
                response_str += string.Format("<uuid>{0}</uuid>", id);
            }
        }

        response_str += "</array></map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class GetMetadataPostHandler : ReadBaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;
    private Scene m_Scene = null;

    public GetMetadataPostHandler(UUID agent_id, IExperienceModule experienceModule, Scene scene)
        : this(string.Format("/caps/{0}", UUID.Random()),agent_id, experienceModule, scene)
    {

    }

    public GetMetadataPostHandler(string path, UUID agent_id, IExperienceModule experienceModule, Scene scene)
        : base("POST", path)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
        m_Scene = scene;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        byte[] data = ReadFully(request);

        //m_log.LogInformation("[EXPERIENCE] GetMetadata == {0}", Encoding.UTF8.GetString(data));

        OSDMap map = (OSDMap)OSDParser.DeserializeLLSDXml(data);

        UUID object_id = UUID.Zero;
        UUID item_id = UUID.Zero;

        OSD object_id_osd = null;
        if (map.TryGetValue("object-id", out object_id_osd))
        {
            object_id = object_id_osd.AsUUID();
        }

        OSD item_id_osd = null;
        if(map.TryGetValue("item-id", out item_id_osd))
        {
            item_id = item_id_osd.AsUUID();
        }


        SceneObjectPart scene_object = m_Scene.GetSceneObjectPart(object_id);

        if(scene_object != null)
        {
            TaskInventoryItem inv_item = scene_object.Inventory.GetInventoryItem(item_id);

            if(inv_item != null)
            {
                string response_str = "<llsd><map>";

                // todo: iterate over fields and add the requested ones
                if (inv_item.ExperienceID != UUID.Zero)
                {
                    response_str += string.Format("<key>experience</key><uuid>{0}</uuid>", inv_item.ExperienceID);
                }
                response_str += "</map></llsd>";

                return Encoding.UTF8.GetBytes(response_str);
            }
        }

        return Encoding.UTF8.GetBytes("<llsd><undef/></llsd>");
    }
}

public class UpdateExperiencePostHandler : ReadBaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;
    
    public UpdateExperiencePostHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {

    }

    public UpdateExperiencePostHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("POST", path)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        byte[] read = ReadFully(request);

        OSDMap experience = (OSDMap)OSDParser.Deserialize(read);

        UUID public_id = experience["public_id"].AsUUID();
        UUID group_id = experience["group_id"].AsUUID();
        string name = experience["name"].AsString();
        string desc = experience["description"].AsString();
        string slurl = experience["slurl"].AsString();
        string metadata = experience["extended_metadata"].AsString();
        int maturity = experience["maturity"].AsInteger();
        int properties = experience["properties"].AsInteger();

        // 42 = adult, 21 = mature, 13 = general
        if (maturity != 42 && maturity != 13)
            maturity = 21;

        string decoded_meta = HttpUtility.HtmlDecode(metadata);

        OSDMap extended = (OSDMap)OSDParser.Deserialize(decoded_meta);

        UUID logo = extended["logo"].AsUUID();
        string marketplace = extended["marketplace"].AsString();

        ExperienceInfo currentInfo = m_ExperienceModule.GetExperienceInfo(public_id);

        // A5 — GATE: only an experience admin (owner OR group ExperienceAdmin) may edit. When NOT
        // an admin, the update block is skipped and the UNCHANGED experience is echoed back below —
        // the reject-shaped response Legion uses (CoreModules/Experience/ExperienceModule.cs:987-991
        // WriteLLSD(BuildUpdateExperienceResponse(info))); the viewer parses it and shows no edit.
        bool is_admin = m_ExperienceModule.IsExperienceAdmin(m_AgentID, public_id);

        if(is_admin)
        {
            currentInfo.name = name;
            currentInfo.description = desc;

            // A5 — GROUP FIELD IS OWNER-ONLY. SL's explicit rule: any experience administrator may
            // edit every field EXCEPT the group; only the experience OWNER may change the group.
            // Previously ANY admin could reassign it. Legion ExperienceModule.cs:1010-1023.
            if (group_id != currentInfo.group_id)
            {
                if (currentInfo.owner_id == m_AgentID)
                    currentInfo.group_id = group_id;
                // else: admin-but-not-owner -> keep the existing group (change ignored).
            }

            if (slurl != "last")
                currentInfo.slurl = slurl;

            currentInfo.marketplace = marketplace;
            currentInfo.logo = logo;
            currentInfo.maturity = maturity;

            if((properties & (int)ExperienceFlags.Disabled) != 0)
            {
                currentInfo.properties |= (int)ExperienceFlags.Disabled;
            }
            else
            {
                currentInfo.properties &= ~(int)ExperienceFlags.Disabled;
            }

            var updated_info = m_ExperienceModule.UpdateExperienceInfo(currentInfo);
            if(updated_info != null)
            {
                currentInfo = updated_info;
            }
        }

        string extended_meta = string.Format("<llsd><map><key>logo</key><uuid>{0}</uuid><key>marketplace</key>{1}</map></llsd>", currentInfo.logo, currentInfo.marketplace != string.Empty ? string.Format("<string>{0}</string>", currentInfo.marketplace) : "<string />");

        string response_str = string.Format("<llsd><map><key>experience_keys</key><array><map>" +
            "<key>public_id</key><uuid>{0}</uuid>" +
            "<key>description</key><string>{1}</string>" +
            "<key>name</key><string>{2}</string>" +
            "<key>quota</key><integer>128</integer>" + // A4 — was info.quota (default 16); Legion emits 128 (ExperienceToOSD:375)
            "<key>slurl</key><string>{6}</string>" +
            "<key>maturity</key><integer>{7}</integer>" +
            "<key>expiration</key><integer>600</integer>" +
            "<key>extended_metadata</key><string>{5}</string>" +
            "<key>group_id</key><uuid>{3}</uuid>" +
            "<key>properties</key><integer>{8}</integer>" +
            "<key>agent_id</key><uuid>{4}</uuid>" +
            "</map></array></map></llsd>",
            currentInfo.public_id, currentInfo.description,
            currentInfo.name, currentInfo.group_id, currentInfo.owner_id,
            HttpUtility.HtmlEncode(extended_meta), currentInfo.slurl, currentInfo.maturity, currentInfo.properties);

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class IsExperienceContributorGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;
    
    public IsExperienceContributorGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public IsExperienceContributorGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        bool is_contributor = false;
        
        if (httpRequest.Query.ContainsKey("experience_id"))
        {
            UUID experience_id;
            if(UUID.TryParse(httpRequest.Query["experience_id"].ToString(), out experience_id))
            {
                is_contributor = m_ExperienceModule.IsExperienceContributor(m_AgentID, experience_id);
            }
        }

        string response_str = "<?xml version=\"1.0\" ?><llsd><map><key>status</key><boolean>" + (is_contributor ? "true" : "false") + "</boolean></map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class IsExperienceAdminGetHandler : BaseStreamHandler
{
    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public IsExperienceAdminGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public IsExperienceAdminGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        bool is_admin = false;

        if (httpRequest.Query.ContainsKey("experience_id"))
        {
            UUID experience_id;
            if (UUID.TryParse(httpRequest.Query["experience_id"].ToString(), out experience_id))
            {
                is_admin = m_ExperienceModule.IsExperienceAdmin(m_AgentID, experience_id);
            }
        }

        string response_str = "<?xml version=\"1.0\" ?><llsd><map><key>status</key><boolean>" + (is_admin ? "true" : "false") + "</boolean></map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class RegionExperiencesGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public RegionExperiencesGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public RegionExperiencesGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        //m_log.LogInformation("[EXPERIENCE] RegionExperiences request on {0}", path);

        UUID[] allowed = m_ExperienceModule.GetEstateAllowedExperiences();
        UUID[] key = m_ExperienceModule.GetEstateKeyExperiences();
        UUID[] blocked = m_ExperienceModule.GetEstateBlockedExperiences();

        string response_str = "<llsd><map><key>allowed</key>";
        if (allowed.Length > 0)
        {
            response_str += "<array>";
            foreach (UUID id in allowed)
            {
                response_str += string.Format("<uuid>{0}</uuid>", id);
            }
            response_str += "</array>";
        }
        else response_str += "<undef />";

        // A6 — the `blocked` list now surfaces the T5b estate BlockedExperiences (Legion emits it as
        // a real array, BuildRegionExperiencesLLSD:422-427). It was hardcoded <undef/>, so the
        // Region/Estate > Experiences Blocked editor always rendered empty even when experiences
        // were region-blocked. `default`/`disabled` stay omitted-shaped (Tranquillity models
        // neither; the panel reads them conditionally, so undef/empty is the correct "not set" wire).
        // Error shape: this GET only reads estate lists and always emits a well-formed LLSD map, so
        // it is already viewer-safe (never a parse-breaking response) — no error-case change needed.
        response_str += "<key>blocked</key>";
        if (blocked.Length > 0)
        {
            response_str += "<array>";
            foreach (UUID id in blocked)
            {
                response_str += string.Format("<uuid>{0}</uuid>", id);
            }
            response_str += "</array>";
        }
        else response_str += "<undef />";

        response_str += "<key>default</key><uuid /><key>disabled</key><undef /><key>trusted</key>";

        if (key.Length > 0)
        {
            response_str += "<array>";
            foreach (UUID id in key)
            {
                response_str += string.Format("<uuid>{0}</uuid>", id);
            }
            response_str += "</array>";
        }
        else response_str += "<undef />";

        response_str += "</map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class GetExperienceInfoGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;
    public GetExperienceInfoGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public GetExperienceInfoGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        //m_log.LogInformation("[EXPERIENCE] GetExperienceInfo request on {0}", path);

        NameValueCollection query = HttpUtility.ParseQueryString(httpRequest.Url.Query);
        string[] ids = query.GetValues("public_id");
        //m_log.LogInformation("[EXPERIENCE] GetExperienceInfo public_ids = {0}", string.Join(", ", ids));

        string response_str = "<?xml version=\"1.0\" ?><llsd><map><key>experience_keys</key><array>";

        foreach (string id in ids)
        {
            UUID experience_id = UUID.Parse(id);

            ExperienceInfo info = m_ExperienceModule.GetExperienceInfo(experience_id);

            if (info != null)
            {
                // A4 — was hardcoding an EMPTY marketplace (dropping info.marketplace), so the
                // profile floater's Marketplace panel never showed a store link. Emit the real
                // value like FindExperienceByName/UpdateExperience do (Legion BuildExtendedMetadata
                // always emits logo + marketplace, CoreModules/Experience/ExperienceModule.cs:409).
                // quota stays the hardcoded 128 below — matching Legion's ExperienceToOSD:375 and
                // now consistent with the Find/Update handlers (which A4 also moved to 128).
                string extended_meta = string.Format("<llsd><map><key>logo</key><uuid>{0}</uuid><key>marketplace</key>{1}</map></llsd>", info.logo, info.marketplace != string.Empty ? string.Format("<string>{0}</string>", info.marketplace) : "<string />");

                response_str += string.Format("<map>" +
                    "<key>public_id</key><uuid>{0}</uuid>" +
                    "<key>description</key><string>{1}</string>" +
                    "<key>name</key><string>{2}</string>" +
                    "<key>quota</key><integer>128</integer>" +
                    "<key>slurl</key><string>{6}</string>" +
                    "<key>maturity</key><integer>{7}</integer>" +
                    "<key>expiration</key><integer>600</integer>" +
                    "<key>extended_metadata</key><string>{5}</string>" +
                    "<key>group_id</key><uuid>{3}</uuid>" +
                    "<key>properties</key><integer>{8}</integer>" +
                    "<key>agent_id</key><uuid>{4}</uuid>" +
                    "</map>", info.public_id, info.description, info.name, info.group_id, info.owner_id, HttpUtility.HtmlEncode(extended_meta), info.slurl, info.maturity, info.properties);
            }
        }

        response_str += "</array></map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class GetCreatorExperiencesGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public GetCreatorExperiencesGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public GetCreatorExperiencesGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        //m_log.LogInformation("[EXPERIENCE] GetCreatorExperiences request on {0}", path);

        string response_str = "<llsd><map><key>experience_ids</key>";

        UUID[] agent_experiences = m_ExperienceModule.GetConributorExperiences(m_AgentID);

        if (agent_experiences.Length > 0)
        {
            response_str += "<array>";

            foreach (UUID id in agent_experiences)
                response_str += string.Format("<uuid>{0}</uuid>", id);

            response_str += "</array>";
        }
        else
        {
            response_str += "<undef />";
        }

        response_str += "</map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

public class GetAdminExperiencesGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public GetAdminExperiencesGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public GetAdminExperiencesGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        //m_log.LogInformation("[EXPERIENCE] GetAdminExperiences request on {0}", path);

        string response_str = "<llsd><map><key>experience_ids</key>";

        UUID[] agent_experiences = m_ExperienceModule.GetAdminExperiences(m_AgentID);

        if (agent_experiences.Length > 0)
        {
            response_str += "<array>";

            foreach (UUID id in agent_experiences)
                response_str += string.Format("<uuid>{0}</uuid>", id);

            response_str += "</array>";
        }
        else
        {
            response_str += "<undef />";
        }

        response_str += "</map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

// B1 — the former AgentExperiencesGetHandler (GET-only) was superseded by the module's
// HandleAgentExperiences GET/POST delegate (RegisterCaps), which adds the Acquire (POST) path.

public class GetExperiencesGetHandler : BaseStreamHandler
{
    //private static readonly ILogger m_log = LoggerProvider.CreateLogger(MethodBase.GetCurrentMethod().DeclaringType);

    private UUID m_AgentID = UUID.Zero;
    private IExperienceModule m_ExperienceModule = null;

    public GetExperiencesGetHandler(UUID agent_id, IExperienceModule experienceModule)
        : this(string.Format("/caps/{0}", UUID.Random()), agent_id, experienceModule)
    {
    }

    public GetExperiencesGetHandler(string path, UUID agent_id, IExperienceModule experienceModule)
        : base("GET", path, null, null)
    {
        m_AgentID = agent_id;
        m_ExperienceModule = experienceModule;
    }

    protected override byte[] ProcessRequest(string path, Stream request, IOSHttpRequest httpRequest, IOSHttpResponse httpResponse)
    {
        //m_log.LogInformation("[EXPERIENCE] GetExperiences request on {0}", path);

        string response_str = "<llsd><map><key>blocked</key>";

        UUID[] allowed = m_ExperienceModule.GetAllowedExperiences(m_AgentID);
        UUID[] blocked = m_ExperienceModule.GetBlockedExperiences(m_AgentID);

        if(blocked.Length > 0)
        {
            response_str += "<array>";

            foreach (UUID id in blocked)
                response_str += string.Format("<uuid>{0}</uuid>", id);

            response_str += "</array>";
        }
        else
        {
            response_str += "<undef />";
        }

        response_str += "<key>experiences</key>";

        if (allowed.Length > 0)
        {
            response_str += "<array>";

            foreach (UUID id in allowed)
                response_str += string.Format("<uuid>{0}</uuid>", id);

            response_str += "</array>";
        }
        else
        {
            response_str += "<undef />";
        }

        response_str += "</map></llsd>";

        return Encoding.UTF8.GetBytes(response_str);
    }
}

#endregion

public class ReadBaseStreamHandler : BaseStreamHandler
{
    public ReadBaseStreamHandler(string method, string url) : base(method, url, null, null)
    {
    }

    protected static byte[] ReadFully(Stream stream)
    {
        byte[] buffer = new byte[1024];
        using (MemoryStream ms = new MemoryStream(1024 * 256))
        {
            while (true)
            {
                int read = stream.Read(buffer, 0, buffer.Length);

                if (read <= 0)
                {
                    return ms.ToArray();
                }

                ms.Write(buffer, 0, read);
            }
        }
    }
}


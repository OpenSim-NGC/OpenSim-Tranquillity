/*
 * The OSSL permission gate, ported from OSSL_Api.CheckThreatLevel / CheckThreatLevelTest
 * (Source/OpenSim.Region.ScriptEngine.Shared/Api/OSSL_Api.cs:180-215, 301-530) so that a grid
 * operator's [OSSL] settings mean the same thing on Phlox as on YEngine. Keys honoured, all from the
 * [OSSL] section (falling back to the engine's own section when [OSSL] is absent, as upstream does):
 *   AllowOSFunctions         (default true)   - false disables every gated function
 *   OSFunctionThreatLevel    (default VeryLow) - NoAccess/None/VeryLow/Low/Moderate/High/VeryHigh/Severe
 *   PermissionErrorToOwner   (default false)  - prefix the error with (OWNER)
 *   Allow_<function>         - true | false | comma list of owner UUIDs and/or PARCEL_OWNER,
 *                              PARCEL_GROUP_MEMBER, ESTATE_MANAGER, ESTATE_OWNER, ACTIVE_GOD, GOD, GRID_GOD
 *   Creators_<function>      - comma list of script-creator UUIDs
 * A denied call throws, and the script stops with the same message YEngine gives.
 */
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using Nini.Config;
using OpenMetaverse;
using OpenSim.Framework;
using OpenSim.Region.Framework.Interfaces;
using OpenSim.Region.Framework.Scenes;
using OpenSim.Region.ScriptEngine.Shared.Api.Interfaces;
using InWorldz.Phlox.VM;
using Microsoft.Extensions.Logging;

namespace Phlox.ScriptEngine
{
    internal sealed class OsslGate
    {
        private static readonly ILogger m_log = LoggerProvider.CreateLogger(typeof(OsslGate));

        [Flags]
        private enum AllowedControlFlags
        {
            THREATLEVEL = 1, ALL = 2, OWNERUUID = 4, CREATORUUID = 8,
            PARCEL_OWNER = 16, PARCEL_GROUP_MEMBER = 32, ESTATE_MANAGER = 64, ESTATE_OWNER = 128,
            ACTIVE_GOD = 256, GOD = 512, GRID_GOD = 1024,
        }

        private sealed class FunctionPerms
        {
            public AllowedControlFlags AllowedControl;
            public List<UUID> AllowedOwners;
            public List<UUID> AllowedCreators;
        }

        private readonly IConfig m_osslconfig;
        private readonly bool m_enabled;
        private readonly bool m_errorToOwner;
        private readonly ThreatLevel m_maxThreatLevel;
        private readonly ConcurrentDictionary<string, FunctionPerms> m_perms = new();

        public OsslGate(IConfigSource config)
        {
            m_osslconfig = config?.Configs["OSSL"] ?? config?.Configs["InWorldz.Phlox"];
            m_enabled = m_osslconfig?.GetBoolean("AllowOSFunctions", true) ?? true;
            m_errorToOwner = m_osslconfig?.GetBoolean("PermissionErrorToOwner", false) ?? false;
            string risk = m_osslconfig?.GetString("OSFunctionThreatLevel", "VeryLow") ?? "VeryLow";
            m_maxThreatLevel = risk switch
            {
                "NoAccess" => ThreatLevel.NoAccess,
                "None" => ThreatLevel.None,
                "VeryLow" => ThreatLevel.VeryLow,
                "Low" => ThreatLevel.Low,
                "Moderate" => ThreatLevel.Moderate,
                "High" => ThreatLevel.High,
                "VeryHigh" => ThreatLevel.VeryHigh,
                "Severe" => ThreatLevel.Severe,
                _ => ThreatLevel.VeryLow,
            };
        }

        public bool Enabled => m_enabled;
        public ThreatLevel MaxThreatLevel => m_maxThreatLevel;

        /// <summary>The bare CheckThreatLevel(): only the master switch.</summary>
        public void Check()
        {
            if (!m_enabled)
                throw new VMException(Prefix() + "OSSL Permission Error: All unsafe OSSL funtions disabled");
        }

        public void Check(ThreatLevel level, string function, Scene world, SceneObjectPart host, TaskInventoryItem item)
        {
            if (!m_enabled)
                throw new VMException(Prefix() + "OSSL Permission Error: All unsafe OSSL funtions disabled");
            string why = Test(level, function, world, host, item);
            if (!string.IsNullOrEmpty(why))
                throw new VMException(Prefix() + "OSSL Permission Error: " + why);
        }

        private string Prefix() => m_errorToOwner ? "(OWNER)" : "";

        /// <summary>Empty when permitted, else the reason - the upstream text, verbatim.</summary>
        public string Test(ThreatLevel level, string function, Scene world, SceneObjectPart host, TaskInventoryItem item)
        {
            if (!m_perms.TryGetValue(function, out FunctionPerms perms))
            {
                perms = new FunctionPerms();
                string ownerPerm = m_osslconfig?.GetString("Allow_" + function, "") ?? "";
                string creatorPerm = m_osslconfig?.GetString("Creators_" + function, "") ?? "";
                if (string.IsNullOrWhiteSpace(ownerPerm) && string.IsNullOrWhiteSpace(creatorPerm))
                {
                    perms.AllowedControl = AllowedControlFlags.THREATLEVEL;
                }
                else
                {
                    if (bool.TryParse(ownerPerm, out bool allowed))
                    {
                        if (allowed) perms.AllowedControl = AllowedControlFlags.ALL;
                    }
                    else
                    {
                        bool error = false;
                        if (!string.IsNullOrWhiteSpace(ownerPerm))
                        {
                            foreach (string id in ownerPerm.Split(','))
                            {
                                string current = id.Trim().ToUpper();
                                switch (current)
                                {
                                    case "": break;
                                    case "PARCEL_OWNER": perms.AllowedControl |= AllowedControlFlags.PARCEL_OWNER; break;
                                    case "PARCEL_GROUP_MEMBER": perms.AllowedControl |= AllowedControlFlags.PARCEL_GROUP_MEMBER; break;
                                    case "ESTATE_MANAGER": perms.AllowedControl |= AllowedControlFlags.ESTATE_MANAGER; break;
                                    case "ESTATE_OWNER": perms.AllowedControl |= AllowedControlFlags.ESTATE_OWNER; break;
                                    case "ACTIVE_GOD": perms.AllowedControl |= AllowedControlFlags.ACTIVE_GOD; break;
                                    case "GOD": perms.AllowedControl |= AllowedControlFlags.GOD; break;
                                    case "GRID_GOD": perms.AllowedControl |= AllowedControlFlags.GRID_GOD; break;
                                    default:
                                        if (UUID.TryParse(current, out UUID uuid))
                                        {
                                            if (uuid.IsNotZero())
                                            {
                                                perms.AllowedOwners ??= new List<UUID>();
                                                perms.AllowedControl |= AllowedControlFlags.OWNERUUID;
                                                perms.AllowedOwners.Add(uuid);
                                            }
                                        }
                                        else error = true;
                                        break;
                                }
                            }
                            if (error) m_log.LogWarning("[OSSLENABLE]: error parsing line Allow_{0} = {1}", function, ownerPerm);
                        }
                        error = false;
                        if (!string.IsNullOrWhiteSpace(creatorPerm))
                        {
                            foreach (string id in creatorPerm.Split(','))
                            {
                                if (UUID.TryParse(id.Trim(), out UUID uuid))
                                {
                                    if (!uuid.IsZero())
                                    {
                                        perms.AllowedCreators ??= new List<UUID>();
                                        perms.AllowedControl |= AllowedControlFlags.CREATORUUID;
                                        perms.AllowedCreators.Add(uuid);
                                    }
                                }
                                else error = true;
                            }
                            if (error) m_log.LogWarning("[OSSLENABLE]: error parsing line Creators_{0} = {1}", function, creatorPerm);
                        }
                    }
                }
                m_perms.TryAdd(function, perms);
            }

            AllowedControlFlags functionControl = perms.AllowedControl;
            if (functionControl == AllowedControlFlags.THREATLEVEL)
            {
                if (level <= m_maxThreatLevel) return string.Empty;
                return $"{function} permission denied.  Allowed threat level is {m_maxThreatLevel} but function threat level is {level}";
            }
            if (functionControl == 0) return $"{function} disabled in region configuration";
            if (functionControl == AllowedControlFlags.ALL) return string.Empty;

            UUID ownerId = item?.OwnerID ?? host.OwnerID;
            if ((functionControl & AllowedControlFlags.OWNERUUID) != 0 && perms.AllowedOwners.Contains(host.OwnerID))
                return string.Empty;
            if ((functionControl & AllowedControlFlags.PARCEL_OWNER) != 0)
            {
                ILandObject land = world.LandChannel.GetLandObject(host.AbsolutePosition);
                if (land != null && land.LandData.OwnerID.Equals(ownerId)) return string.Empty;
            }
            if ((functionControl & AllowedControlFlags.PARCEL_GROUP_MEMBER) != 0 && item != null)
            {
                ILandObject land = world.LandChannel.GetLandObject(host.AbsolutePosition);
                if (land != null && land.LandData.GroupID.Equals(item.GroupID) && !land.LandData.GroupID.IsZero()) return string.Empty;
            }
            if ((functionControl & AllowedControlFlags.ESTATE_OWNER) != 0
                && world.RegionInfo.EstateSettings.EstateOwner.Equals(ownerId)) return string.Empty;
            if ((functionControl & AllowedControlFlags.ESTATE_MANAGER) != 0
                && world.RegionInfo.EstateSettings.IsEstateManagerOrOwner(ownerId)) return string.Empty;
            if ((functionControl & AllowedControlFlags.GRID_GOD) != 0 && world.Permissions.IsGridGod(ownerId)) return string.Empty;
            if ((functionControl & AllowedControlFlags.GOD) != 0 && world.Permissions.IsAdministrator(ownerId)) return string.Empty;
            if ((functionControl & AllowedControlFlags.ACTIVE_GOD) != 0)
            {
                ScenePresence sp = world.GetScenePresence(ownerId);
                if (sp != null && !sp.IsDeleted && sp.IsGod) return string.Empty;
            }
            if ((functionControl & AllowedControlFlags.CREATORUUID) == 0)
                return $"{function} permission denied";
            if (item == null || !perms.AllowedCreators.Contains(item.CreatorID))
                return $"{function} permission denied. Script creator is not in the list of users allowed to execute this function and prim owner also has no permission";
            if (item.CreatorID.NotEqual(item.OwnerID) && (item.CurrentPermissions & (uint)OpenSim.Framework.PermissionMask.Modify) != 0)
                return $"{function} permission denied. Script creator is not prim owner";
            return string.Empty;
        }
    }
}

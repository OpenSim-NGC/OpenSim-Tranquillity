/*
 * Copyright (c) Contributors, http://opensimulator.org/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSimulator Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES;
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Autofac;

using OpenSim.Framework;
using OpenSim.Data;
using OpenSim.Services.Interfaces;

using OpenMetaverse;
using OpenMetaverse.StructuredData;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Services.ProfilesService
{
    public class UserProfilesService: IUserProfilesService
    {
        protected IUserAccountService? userAccounts;
        
        protected readonly IProfilesData ProfilesData;
        protected readonly IConfiguration m_configuration;
        protected readonly ILogger<UserProfilesService> m_logger;
        protected readonly IComponentContext m_context;

        protected string ConfigName { get; private set; } = "UserProfileService";

        public UserProfilesService(
            IComponentContext componentContext,
            IConfiguration config,
            ILogger<UserProfilesService> logger,
            IProfilesData profilesData)
        {
            m_context = componentContext;
            m_configuration = config;
            m_logger = logger;

            ProfilesData = profilesData;

            string? connString = String.Empty;
            string? realm = String.Empty;

            var dbConfig = m_configuration.GetSection("DatabaseService");
            if (dbConfig.Exists() is true)
            {
                connString = dbConfig.GetValue("ConnectionString", String.Empty);
            }

            var profilesConfig = m_configuration.GetSection(ConfigName);
            if (profilesConfig.Exists() is true)
            {
                connString = profilesConfig.GetValue("ConnectionString", connString);
                realm = profilesConfig.GetValue("Realm", realm);
            }

            ProfilesData.Initialize(connString, realm);

            string? accountService = profilesConfig.GetValue("UserAccountService", String.Empty);
            if (string.IsNullOrEmpty(accountService) is false)
                userAccounts = m_context.ResolveNamed<IUserAccountService>(accountService);
        }

        #region Classifieds
        public OSD AvatarClassifiedsRequest(UUID creatorId)
        {
            OSDArray records = ProfilesData.GetClassifiedRecords(creatorId);

            return records;
        }

        public bool ClassifiedUpdate(UserClassifiedAdd ad, ref string result)
        {
            if(!ProfilesData.UpdateClassifiedRecord(ad, ref result))
            {
                return false;
            }
            result = "success";
            return true;
        }

        public bool ClassifiedDelete(UUID recordId)
        {
            if(ProfilesData.DeleteClassifiedRecord(recordId))
                return true;

            return false;
        }

        public bool ClassifiedInfoRequest(ref UserClassifiedAdd ad, ref string result)
        {
            if(ProfilesData.GetClassifiedInfo(ref ad, ref result))
                return true;

            return false;
        }
        #endregion Classifieds

        #region Picks
        public OSD AvatarPicksRequest(UUID creatorId)
        {
            OSDArray records = ProfilesData.GetAvatarPicks(creatorId);

            return records;
        }

        public bool PickInfoRequest(ref UserProfilePick pick, ref string result)
        {
            pick = ProfilesData.GetPickInfo(pick.CreatorId, pick.PickId);
            result = "OK";
            return true;
        }

        public bool PicksUpdate(ref UserProfilePick pick, ref string result)
        {
            return ProfilesData.UpdatePicksRecord(pick);
        }

        public bool PicksDelete(UUID pickId)
        {
            return ProfilesData.DeletePicksRecord(pickId);
        }
        #endregion Picks

        #region Notes
        public bool AvatarNotesRequest(ref UserProfileNotes note)
        {
            return ProfilesData.GetAvatarNotes(ref note);
        }

        public bool NotesUpdate(ref UserProfileNotes note, ref string result)
        {
            return ProfilesData.UpdateAvatarNotes(ref note, ref result);
        }
        #endregion Notes

        #region Profile Properties
        public bool AvatarPropertiesRequest(ref UserProfileProperties prop, ref string result)
        {
            return ProfilesData.GetAvatarProperties(ref prop, ref result);
        }

        public bool AvatarPropertiesUpdate(ref UserProfileProperties prop, ref string result)
        {
            return ProfilesData.UpdateAvatarProperties(ref prop, ref result);
        }
        #endregion Profile Properties

        #region Interests
        public bool AvatarInterestsUpdate(UserProfileProperties prop, ref string result)
        {
            return ProfilesData.UpdateAvatarInterests(prop, ref result);
        }
        #endregion Interests


        #region User Preferences
        public bool UserPreferencesUpdate(ref UserPreferences pref, ref string result)
        {
            if(string.IsNullOrEmpty(pref.EMail))
            {
                UserAccount account = new UserAccount();
                if(userAccounts is UserAccountService.UserAccountService)
                {
                    try
                    {
                        account = userAccounts.GetUserAccount(UUID.Zero, pref.UserId);
                        if(string.IsNullOrEmpty(account.Email))
                        {
                            pref.EMail = string.Empty;
                        }
                        else
                            pref.EMail = account.Email;
                    }
                    catch (Exception e)
                    {
                        m_logger.LogError (e, $"[PROFILES SERVICE]: UserAccountService Exception: Could not get user account");
                        result = "UserAccountService settings error in UserProfileService!";
                        return false;
                    }
                }
                else
                {
                    m_logger.LogError ("[PROFILES SERVICE]: UserAccountService: Could not get user account");
                    result = "UserAccountService settings error in UserProfileService!";
                    return false;
                }
            }
            return ProfilesData.UpdateUserPreferences(ref pref, ref result);
        }

        public bool UserPreferencesRequest(ref UserPreferences pref, ref string result)
        {
            if (!ProfilesData.GetUserPreferences(ref pref, ref result))
                return false;

            if(string.IsNullOrEmpty(pref.EMail))
            {
                UserAccount account = new UserAccount();
                if(userAccounts is UserAccountService.UserAccountService)
                {
                    try
                    {
                        account = userAccounts.GetUserAccount(UUID.Zero, pref.UserId);
                        if(string.IsNullOrEmpty(account.Email))
                        {
                            pref.EMail = string.Empty;
                        }
                        else
                        {
                            pref.EMail = account.Email;
                            UserPreferencesUpdate(ref pref, ref result);
                        }
                    }
                    catch (Exception e)
                    {
                        m_logger.LogError (e, "[PROFILES SERVICE]: UserAccountService Exception: Could not get user account");
                        result = "UserAccountService settings error in UserProfileService!";
                        return false;
                    }
                }
                else
                {
                    m_logger.LogError ("[PROFILES SERVICE]: UserAccountService: Could not get user account");
                    result = "UserAccountService settings error in UserProfileService!";
                    return false;
                }
            }

            if(string.IsNullOrEmpty(pref.EMail))
                pref.EMail = "No Email Address On Record";

            return true;
        }
        #endregion User Preferences


        #region Utility
        public OSD AvatarImageAssetsRequest(UUID avatarId)
        {
            OSDArray records = ProfilesData.GetUserImageAssets(avatarId);
            return records;
        }
        #endregion Utility

        #region UserData
        public bool RequestUserAppData(ref UserAppData prop, ref string result)
        {
            return ProfilesData.GetUserAppData(ref prop, ref result);
        }

        public bool SetUserAppData(UserAppData prop, ref string result)
        {
            return true;
        }
        #endregion UserData
    }
}


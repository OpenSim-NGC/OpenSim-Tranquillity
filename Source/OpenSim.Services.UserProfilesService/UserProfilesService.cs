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

using System;
using System.Reflection;
using System.Text;
using Nini.Config;
using log4net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using OpenSim.Server.Base;
using OpenSim.Services.Interfaces;
using OpenSim.Services.UserAccountService;
using OpenSim.Data;
using OpenMetaverse;
using OpenMetaverse.StructuredData;
using OpenSim.Framework;

namespace OpenSim.Services.ProfilesService;

public class UserProfilesService(
    IConfiguration configuration,
    ILogger<UserProfilesService> logger,
    IProfilesData profilesData,
    IUserAccountService userAccounts)
    : IUserProfilesService
{
    protected readonly IConfiguration _configuration = configuration;
    protected readonly ILogger<UserProfilesService> _logger = logger;
    protected readonly IProfilesData _profilesData = profilesData;
    protected readonly IUserAccountService _userAccounts = userAccounts;

    #region Classifieds
    public OSD AvatarClassifiedsRequest(UUID creatorId)
    {
        OSDArray records = _profilesData.GetClassifiedRecords(creatorId);
        return records;
    }

    public bool ClassifiedUpdate(UserClassifiedAdd ad, ref string result)
    {
        if(_profilesData.UpdateClassifiedRecord(ad, ref result) is false)
        {
            return false;
        }
        
        result = "success";
        return true;
    }

    public bool ClassifiedDelete(UUID recordId)
    {
        if(_profilesData.DeleteClassifiedRecord(recordId))
            return true;

        return false;
    }

    public bool ClassifiedInfoRequest(ref UserClassifiedAdd ad, ref string result)
    {
        if (_profilesData.GetClassifiedInfo(ref ad, ref result) is true)
            return true;

        return false;
    }
    
    #endregion Classifieds

    #region Picks
    public OSD AvatarPicksRequest(UUID creatorId)
    {
        OSDArray records = _profilesData.GetAvatarPicks(creatorId);
        return records;
    }

    public bool PickInfoRequest(ref UserProfilePick pick, ref string result)
    {
        pick = _profilesData.GetPickInfo(pick.CreatorId, pick.PickId);
        result = "OK";
        return true;
    }

    public bool PicksUpdate(ref UserProfilePick pick, ref string result)
    {
        return _profilesData.UpdatePicksRecord(pick);
    }

    public bool PicksDelete(UUID pickId)
    {
        return _profilesData.DeletePicksRecord(pickId);
    }
    #endregion Picks

    #region Notes
    public bool AvatarNotesRequest(ref UserProfileNotes note)
    {
        return _profilesData.GetAvatarNotes(ref note);
    }

    public bool NotesUpdate(ref UserProfileNotes note, ref string result)
    {
        return _profilesData.UpdateAvatarNotes(ref note, ref result);
    }
    #endregion Notes

    #region Profile Properties
    public bool AvatarPropertiesRequest(ref UserProfileProperties prop, ref string result)
    {
        return _profilesData.GetAvatarProperties(ref prop, ref result);
    }

    public bool AvatarPropertiesUpdate(ref UserProfileProperties prop, ref string result)
    {
        return _profilesData.UpdateAvatarProperties(ref prop, ref result);
    }
    #endregion Profile Properties

    #region Interests
    public bool AvatarInterestsUpdate(UserProfileProperties prop, ref string result)
    {
        return _profilesData.UpdateAvatarInterests(prop, ref result);
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
                catch
                {
                    _logger.LogError ("[PROFILES SERVICE]: UserAccountService Exception: Could not get user account");
                    result = "UserAccountService settings error in UserProfileService!";
                    return false;
                }
            }
            else
            {
                _logger.LogError ("[PROFILES SERVICE]: UserAccountService: Could not get user account");
                result = "UserAccountService settings error in UserProfileService!";
                return false;
            }
        }
        return _profilesData.UpdateUserPreferences(ref pref, ref result);
    }

    public bool UserPreferencesRequest(ref UserPreferences pref, ref string result)
    {
        if (!_profilesData.GetUserPreferences(ref pref, ref result))
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
                catch
                {
                    _logger.LogError ("[PROFILES SERVICE]: UserAccountService Exception: Could not get user account");
                    result = "UserAccountService settings error in UserProfileService!";
                    return false;
                }
            }
            else
            {
                _logger.LogError ("[PROFILES SERVICE]: UserAccountService: Could not get user account");
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
        OSDArray records = _profilesData.GetUserImageAssets(avatarId);
        return records;
    }
    #endregion Utility

    #region UserData
    public bool RequestUserAppData(ref UserAppData prop, ref string result)
    {
        return _profilesData.GetUserAppData(ref prop, ref result);
    }

    public bool SetUserAppData(UserAppData prop, ref string result)
    {
        return true;
    }
    #endregion UserData
}



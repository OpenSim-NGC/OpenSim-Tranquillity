using System.Collections;
using System.Net;

using Nwc.XmlRpc;

using OpenMetaverse;
using OpenSim.Data.MySQL.MoneyData;
using OpenSim.Framework;
using OpenSim.Server.MoneyServer.Models;

namespace OpenSim.Server.MoneyServer.Controllers;

public class MoneyClientXmlRpcController
{
    private readonly MoneyXmlRpcSettings _settings;
    private readonly IMoneyDBService _moneyDBService;
    private readonly Dictionary<string, string> _sessionDic;
    private readonly Dictionary<string, string> _secureSessionDic;
    private readonly ILogger<MoneyClientXmlRpcController> _logger;

    public MoneyClientXmlRpcController(
        MoneyXmlRpcSettings settings,
        IMoneyDBService moneyDBService,
        MoneySessionStore sessionStore,
        ILogger<MoneyClientXmlRpcController> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _moneyDBService = moneyDBService ?? throw new ArgumentNullException(nameof(moneyDBService));
        if (sessionStore == null) throw new ArgumentNullException(nameof(sessionStore));
        _sessionDic = sessionStore.SessionDic;
        _secureSessionDic = sessionStore.SecureSessionDic;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public XmlRpcResponse HandleClientLogin(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        _logger.LogInformation("[MONEY RPC]: handleClientLogin:");

        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        responseData["success"] = false;
        responseData["clientBalance"] = 0;

        string universalID = string.Empty;
        string clientUUID = string.Empty;
        string sessionID = string.Empty;
        string secureID = string.Empty;
        string simIP = string.Empty;
        string userName = string.Empty;
        int balance = 0;
        int avatarType = (int)AvatarType.UNKNOWN_AVATAR;
        int avatarClass = (int)AvatarType.UNKNOWN_AVATAR;

        if (requestData.ContainsKey("clientUUID")) clientUUID = (string)requestData["clientUUID"];
        if (requestData.ContainsKey("clientSessionID")) sessionID = (string)requestData["clientSessionID"];
        if (requestData.ContainsKey("clientSecureSessionID")) secureID = (string)requestData["clientSecureSessionID"];
        if (requestData.ContainsKey("universalID")) universalID = (string)requestData["universalID"];
        if (requestData.ContainsKey("userName")) userName = (string)requestData["userName"];
        if (requestData.ContainsKey("openSimServIP")) simIP = (string)requestData["openSimServIP"];
        if (requestData.ContainsKey("avatarType")) avatarType = Convert.ToInt32(requestData["avatarType"]);
        if (requestData.ContainsKey("avatarClass")) avatarClass = Convert.ToInt32(requestData["avatarClass"]);

        string firstName = string.Empty;
        string lastName = string.Empty;
        string serverURL = string.Empty;
        string securePsw = string.Empty;

        if (!string.IsNullOrEmpty(universalID))
        {
            UUID uuid;
            Util.ParseUniversalUserIdentifier(universalID, out uuid, out serverURL, out firstName, out lastName, out securePsw);
        }

        if (string.IsNullOrEmpty(userName))
        {
            userName = firstName + " " + lastName;
        }

        UserInfo userInfo = _moneyDBService.FetchUserInfo(clientUUID);
        if (userInfo != null)
        {
            avatarType = userInfo.Type;
            if (avatarType == (int)AvatarType.LOCAL_AVATAR) avatarClass = (int)AvatarType.LOCAL_AVATAR;
            if (avatarClass == (int)AvatarType.UNKNOWN_AVATAR) avatarClass = userInfo.Class;
            if (string.IsNullOrEmpty(userName)) userName = userInfo.Avatar;
        }

        if (avatarType == (int)AvatarType.UNKNOWN_AVATAR) avatarType = avatarClass;
        if (string.IsNullOrEmpty(serverURL)) avatarClass = (int)AvatarType.NPC_AVATAR;

        _logger.LogInformation("[MONEY RPC]: handleClientLogon: Avatar {0} ({1}) is logged on.", userName, clientUUID);
        _logger.LogInformation("[MONEY RPC]: handleClientLogon: Avatar Type is {0} and Avatar Class is {1}", avatarType, avatarClass);

        if (avatarClass == (int)AvatarType.GUEST_AVATAR && !_settings.GstEnable)
        {
            responseData["description"] = "Avatar is a Guest avatar. But this Money Server does not support Guest avatars.";
            _logger.LogInformation("[MONEY RPC]: handleClientLogon: {0}", responseData["description"]);
            return response;
        }
        else if (avatarClass == (int)AvatarType.HG_AVATAR && !_settings.HgEnable)
        {
            responseData["description"] = "Avatar is a HG avatar. But this Money Server does not support HG avatars.";
            _logger.LogInformation("[MONEY RPC]: handleClientLogon: {0}", responseData["description"]);
            return response;
        }
        else if (avatarClass == (int)AvatarType.FOREIGN_AVATAR)
        {
            responseData["description"] = "Avatar is a Foreign avatar.";
            _logger.LogInformation("[MONEY RPC]: handleClientLogon: {0}", responseData["description"]);
            return response;
        }
        else if (avatarClass == (int)AvatarType.UNKNOWN_AVATAR)
        {
            responseData["description"] = "Avatar is a Unknown avatar.";
            _logger.LogInformation("[MONEY RPC]: handleClientLogon: {0}", responseData["description"]);
            return response;
        }
        else if (avatarClass == (int)AvatarType.NPC_AVATAR)
        {
            responseData["success"] = true;
            responseData["clientBalance"] = 0;
            responseData["description"] = "Avatar is a NPC.";
            _logger.LogInformation("[MONEY RPC]: handleClientLogon: {0}", responseData["description"]);
            return response;
        }

        lock (_sessionDic)
        {
            if (!_sessionDic.ContainsKey(clientUUID))
            {
                _sessionDic.Add(clientUUID, sessionID);
            }
            else _sessionDic[clientUUID] = sessionID;
        }
        lock (_secureSessionDic)
        {
            if (!_secureSessionDic.ContainsKey(clientUUID))
            {
                _secureSessionDic.Add(clientUUID, secureID);
            }
            else _secureSessionDic[clientUUID] = secureID;
        }

        try
        {
            if (userInfo == null) userInfo = new UserInfo();
            userInfo.UserID = clientUUID;
            userInfo.SimIP = simIP;
            userInfo.Avatar = userName;
            userInfo.PswHash = UUID.Zero.ToString();
            userInfo.Type = avatarType;
            userInfo.Class = avatarClass;
            userInfo.ServerURL = serverURL;
            if (!string.IsNullOrEmpty(securePsw)) userInfo.PswHash = securePsw;

            if (!_moneyDBService.TryAddUserInfo(userInfo))
            {
                _logger.LogError("[MONEY RPC]: handleClientLogin: Unable to refresh information for user \"{0}\" in DB.", userName);
                responseData["success"] = true;
                responseData["description"] = "Update or add user information to db failed";
                return response;
            }
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleClientLogin: Can't update userinfo for user {0}: {1}", clientUUID, e.ToString());
            responseData["description"] = "Exception occured" + e.ToString();
            return response;
        }

        try
        {
            balance = _moneyDBService.getBalance(clientUUID);

            if (balance == -1)
            {
                int defaultBalance = _settings.DefaultBalance;
                if (avatarClass == (int)AvatarType.HG_AVATAR) defaultBalance = _settings.HgDefaultBalance;
                if (avatarClass == (int)AvatarType.GUEST_AVATAR) defaultBalance = _settings.GstDefaultBalance;

                if (_moneyDBService.addUser(clientUUID, defaultBalance, 0, avatarType))
                {
                    responseData["success"] = true;
                    responseData["description"] = "add user successfully";
                    responseData["clientBalance"] = defaultBalance;
                }
                else
                {
                    responseData["description"] = "add user failed";
                }
            }
            else if (balance >= 0)
            {
                responseData["success"] = true;
                responseData["description"] = "get user balance successfully";
                responseData["clientBalance"] = balance;
            }

            return response;
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleClientLogin: Can't get balance of user {0}: {1}", clientUUID, e.ToString());
            responseData["description"] = "Exception occured" + e.ToString();
        }

        return response;
    }

    public XmlRpcResponse HandleClientLogout(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string clientUUID = string.Empty;
        if (requestData.ContainsKey("clientUUID")) clientUUID = (string)requestData["clientUUID"];

        _logger.LogInformation("[MONEY RPC]: handleClientLogout: User {0} is logging off.", clientUUID);

        try
        {
            lock (_sessionDic)
            {
                if (_sessionDic.ContainsKey(clientUUID))
                {
                    _sessionDic.Remove(clientUUID);
                }
            }

            lock (_secureSessionDic)
            {
                if (_secureSessionDic.ContainsKey(clientUUID))
                {
                    _secureSessionDic.Remove(clientUUID);
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleClientLogout: Failed to delete user session: {0}", e.ToString());
            responseData["success"] = false;
            return response;
        }

        responseData["success"] = true;
        return response;
    }

    public XmlRpcResponse HandleGetBalance(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string clientUUID = string.Empty;
        string sessionID = string.Empty;
        string secureID = string.Empty;
        int balance;

        responseData["success"] = false;

        if (requestData.ContainsKey("clientUUID")) clientUUID = (string)requestData["clientUUID"];
        if (requestData.ContainsKey("clientSessionID")) sessionID = (string)requestData["clientSessionID"];
        if (requestData.ContainsKey("clientSecureSessionID")) secureID = (string)requestData["clientSecureSessionID"];

        _logger.LogInformation("[MONEY RPC]: handleGetBalance: Getting balance for user {0}", clientUUID);

        if (_sessionDic.ContainsKey(clientUUID) && _secureSessionDic.ContainsKey(clientUUID))
        {
            if (_sessionDic[clientUUID] == sessionID && _secureSessionDic[clientUUID] == secureID)
            {
                try
                {
                    balance = _moneyDBService.getBalance(clientUUID);
                    if (balance == -1)
                    {
                        responseData["description"] = "user not found";
                        responseData["clientBalance"] = 0;
                    }
                    else if (balance >= 0)
                    {
                        responseData["success"] = true;
                        responseData["clientBalance"] = balance;
                    }

                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError("[MONEY RPC]: handleGetBalance: Can't get balance for user {0}, Exception {1}", clientUUID, e.ToString());
                }
                return response;
            }
        }

        _logger.LogError("[MONEY RPC]: handleGetBalance: Session authentication failed when getting balance for user {0}", clientUUID);
        responseData["description"] = "Session check failure, please re-login";
        return response;
    }

    public XmlRpcResponse HandleGetTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string clientID = string.Empty;
        string sessionID = string.Empty;
        string secureID = string.Empty;
        string transactionID = string.Empty;
        UUID transactionUUID = UUID.Zero;

        responseData["success"] = false;

        if (requestData.ContainsKey("clientUUID")) clientID = (string)requestData["clientUUID"];
        if (requestData.ContainsKey("clientSessionID")) sessionID = (string)requestData["clientSessionID"];
        if (requestData.ContainsKey("clientSecureSessionID")) secureID = (string)requestData["clientSecureSessionID"];

        if (requestData.ContainsKey("transactionID"))
        {
            transactionID = (string)requestData["transactionID"];
            UUID.TryParse(transactionID, out transactionUUID);
        }

        if (_sessionDic.ContainsKey(clientID) && _secureSessionDic.ContainsKey(clientID))
        {
            if (_sessionDic[clientID] == sessionID && _secureSessionDic[clientID] == secureID)
            {
                if (string.IsNullOrEmpty(transactionID))
                {
                    responseData["description"] = "TransactionID is empty";
                    _logger.LogError("[MONEY RPC]: handleGetTransaction: TransactionID is empty.");
                    return response;
                }

                try
                {
                    TransactionData transaction = _moneyDBService.FetchTransaction(transactionUUID);
                    if (transaction != null)
                    {
                        responseData["success"] = true;
                        responseData["amount"] = transaction.Amount;
                        responseData["time"] = transaction.Time;
                        responseData["type"] = transaction.Type;
                        responseData["sender"] = transaction.Sender.ToString();
                        responseData["receiver"] = transaction.Receiver.ToString();
                        responseData["description"] = transaction.Description;
                    }
                    else
                    {
                        responseData["description"] = "Invalid Transaction UUID";
                    }

                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError("[MONEY RPC]: handleGetTransaction: {0}", e.ToString());
                    _logger.LogError("[MONEY RPC]: handleGetTransaction: Can't get transaction information for {0}", transactionUUID.ToString());
                }
                return response;
            }
        }

        responseData["success"] = false;
        responseData["description"] = "Session check failure, please re-login";
        return response;
    }
}

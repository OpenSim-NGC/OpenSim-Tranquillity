using System.Collections;
using System.Net;

using Nwc.XmlRpc;

using OpenSim.Data.MySQL.MoneyData;
using OpenSim.Server.MoneyServer.Models;

namespace OpenSim.Server.MoneyServer.Controllers;

public class MoneyWebXmlRpcController
{
    private readonly IMoneyDBService _moneyDBService;
    private readonly Dictionary<string, string> _webSessionDic;
    private readonly ILogger<MoneyWebXmlRpcController> _logger;

    public MoneyWebXmlRpcController(
        MoneyXmlRpcSettings settings,
        IMoneyDBService moneyDBService,
        MoneySessionStore sessionStore,
        ILogger<MoneyWebXmlRpcController> logger)
    {
        if (settings == null) throw new ArgumentNullException(nameof(settings));
        _moneyDBService = moneyDBService ?? throw new ArgumentNullException(nameof(moneyDBService));
        if (sessionStore == null) throw new ArgumentNullException(nameof(sessionStore));
        _webSessionDic = sessionStore.WebSessionDic;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public XmlRpcResponse HandleWebLogin(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string userID = string.Empty;
        string webSessionID = string.Empty;

        responseData["success"] = false;

        if (requestData.ContainsKey("userID")) userID = (string)requestData["userID"];
        if (requestData.ContainsKey("sessionID")) webSessionID = (string)requestData["sessionID"];

        if (string.IsNullOrEmpty(userID) || string.IsNullOrEmpty(webSessionID))
        {
            responseData["errorMessage"] = "userID or sessionID can`t be empty, login failed!";
            return response;
        }

        lock (_webSessionDic)
        {
            if (!_webSessionDic.ContainsKey(userID))
            {
                _webSessionDic.Add(userID, webSessionID);
            }
            else _webSessionDic[userID] = webSessionID;
        }

        _logger.LogInformation("[MONEY RPC]: handleWebLogin: User {0} has logged in from web.", userID);
        responseData["success"] = true;
        return response;
    }

    public XmlRpcResponse HandleWebLogout(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string userID = string.Empty;
        string webSessionID = string.Empty;

        responseData["success"] = false;

        if (requestData.ContainsKey("userID")) userID = (string)requestData["userID"];
        if (requestData.ContainsKey("sessionID")) webSessionID = (string)requestData["sessionID"];

        if (string.IsNullOrEmpty(userID) || string.IsNullOrEmpty(webSessionID))
        {
            responseData["errorMessage"] = "userID or sessionID can`t be empty, log out failed!";
            return response;
        }

        lock (_webSessionDic)
        {
            if (_webSessionDic.ContainsKey(userID))
            {
                _webSessionDic.Remove(userID);
            }
        }

        _logger.LogInformation("[MONEY RPC]: handleWebLogout: User {0} has logged out from web.", userID);
        responseData["success"] = true;
        return response;
    }

    public XmlRpcResponse HandleWebGetBalance(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string userID = string.Empty;
        string webSessionID = string.Empty;
        int balance = 0;

        responseData["success"] = false;

        if (requestData.ContainsKey("userID")) userID = (string)requestData["userID"];
        if (requestData.ContainsKey("sessionID")) webSessionID = (string)requestData["sessionID"];

        _logger.LogInformation("[MONEY RPC]: handleWebGetBalance: Getting balance for user {0}", userID);

        if (_webSessionDic.ContainsKey(userID))
        {
            if (_webSessionDic[userID] == webSessionID)
            {
                try
                {
                    balance = _moneyDBService.getBalance(userID);
                    UserInfo user = _moneyDBService.FetchUserInfo(userID);
                    if (user != null)
                    {
                        responseData["userName"] = user.Avatar;
                    }
                    else
                    {
                        responseData["userName"] = "unknown user";
                    }

                    if (balance == -1)
                    {
                        responseData["errorMessage"] = "User not found";
                        responseData["balance"] = 0;
                    }
                    else if (balance >= 0)
                    {
                        responseData["success"] = true;
                        responseData["balance"] = balance;
                    }
                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError("[MONEY RPC]: handleWebGetBalance: Can't get balance for user {0}, Exception {1}", userID, e.ToString());
                    responseData["errorMessage"] = "Exception occurred when getting balance";
                    return response;
                }
            }
        }

        _logger.LogError("[MONEY RPC]: handleWebLogout: Session authentication failed when getting balance for user " + userID);
        responseData["errorMessage"] = "Session check failure, please re-login";
        return response;
    }

    public XmlRpcResponse HandleWebGetTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string userID = string.Empty;
        string webSessionID = string.Empty;
        int lastIndex = -1;
        int startTime = 0;
        int endTime = 0;

        responseData["success"] = false;

        if (requestData.ContainsKey("userID")) userID = (string)requestData["userID"];
        if (requestData.ContainsKey("sessionID")) webSessionID = (string)requestData["sessionID"];
        if (requestData.ContainsKey("startTime")) startTime = (int)requestData["startTime"];
        if (requestData.ContainsKey("endTime")) endTime = (int)requestData["endTime"];
        if (requestData.ContainsKey("lastIndex")) lastIndex = (int)requestData["lastIndex"];

        if (_webSessionDic.ContainsKey(userID))
        {
            if (_webSessionDic[userID] == webSessionID)
            {
                try
                {
                    int total = _moneyDBService.getTransactionNum(userID, startTime, endTime);
                    TransactionData tran = null;
                    _logger.LogInformation("[MONEY RPC]: handleWebGetTransaction: Getting transation[{0}] for user {1}", lastIndex + 1, userID);
                    if (total > lastIndex + 2)
                    {
                        responseData["isEnd"] = false;
                    }
                    else
                    {
                        responseData["isEnd"] = true;
                    }

                    tran = _moneyDBService.FetchTransaction(userID, startTime, endTime, lastIndex);
                    if (tran != null)
                    {
                        UserInfo senderInfo = _moneyDBService.FetchUserInfo(tran.Sender);
                        UserInfo receiverInfo = _moneyDBService.FetchUserInfo(tran.Receiver);
                        if (senderInfo != null && receiverInfo != null)
                        {
                            responseData["senderName"] = senderInfo.Avatar;
                            responseData["receiverName"] = receiverInfo.Avatar;
                        }
                        else
                        {
                            responseData["senderName"] = "unknown user";
                            responseData["receiverName"] = "unknown user";
                        }
                        responseData["success"] = true;
                        responseData["transactionIndex"] = lastIndex + 1;
                        responseData["transactionUUID"] = tran.TransUUID.ToString();
                        responseData["senderID"] = tran.Sender;
                        responseData["receiverID"] = tran.Receiver;
                        responseData["amount"] = tran.Amount;
                        responseData["type"] = tran.Type;
                        responseData["time"] = tran.Time;
                        responseData["status"] = tran.Status;
                        responseData["description"] = tran.Description;
                    }
                    else
                    {
                        responseData["errorMessage"] = string.Format("Unable to fetch transaction data with the index {0}", lastIndex + 1);
                    }
                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError("[MONEY RPC]: handleWebGetTransaction: Can't get transaction for user {0}, Exception {1}", userID, e.ToString());
                    responseData["errorMessage"] = "Exception occurred when getting transaction";
                    return response;
                }
            }
        }

        _logger.LogError("[MONEY RPC]: handleWebGetTransaction: Session authentication failed when getting transaction for user " + userID);
        responseData["errorMessage"] = "Session check failure, please re-login";
        return response;
    }

    public XmlRpcResponse HandleWebGetTransactionNum(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string userID = string.Empty;
        string webSessionID = string.Empty;
        int startTime = 0;
        int endTime = 0;

        responseData["success"] = false;

        if (requestData.ContainsKey("userID")) userID = (string)requestData["userID"];
        if (requestData.ContainsKey("sessionID")) webSessionID = (string)requestData["sessionID"];
        if (requestData.ContainsKey("startTime")) startTime = (int)requestData["startTime"];
        if (requestData.ContainsKey("endTime")) endTime = (int)requestData["endTime"];

        if (_webSessionDic.ContainsKey(userID))
        {
            if (_webSessionDic[userID] == webSessionID)
            {
                int it = _moneyDBService.getTransactionNum(userID, startTime, endTime);
                if (it >= 0)
                {
                    _logger.LogInformation("[MONEY RPC]: handleWebGetTransactionNum: Get {0} transactions for user {1}", it, userID);
                    responseData["success"] = true;
                    responseData["number"] = it;
                }
                return response;
            }
        }

        _logger.LogError("[MONEY RPC]: handleWebGetTransactionNum: Session authentication failed when getting transaction number for user " + userID);
        responseData["errorMessage"] = "Session check failure, please re-login";
        return response;
    }
}

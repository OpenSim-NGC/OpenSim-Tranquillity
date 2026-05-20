using System.Collections;
using System.Net;
using System.Security.Cryptography;
using System.Text;

using Nwc.XmlRpc;

using OpenMetaverse;
using OpenSim.Data.MySQL.MoneyData;
using OpenSim.Region.OptionalModules.World.Currency;
using OpenSim.Server.MoneyServer.Models;

namespace OpenSim.Server.MoneyServer.Controllers;

public class MoneyTransactionsXmlRpcController
{
    private const int MONEYMODULE_REQUEST_TIMEOUT = 30 * 1000;

    private readonly MoneyXmlRpcSettings _settings;
    private readonly IMoneyDBService _moneyDBService;
    private readonly Dictionary<string, string> _sessionDic;
    private readonly Dictionary<string, string> _secureSessionDic;
    private readonly ILogger<MoneyTransactionsXmlRpcController> _logger;
    private readonly long _ticksToEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

    public MoneyTransactionsXmlRpcController(
        MoneyXmlRpcSettings settings,
        IMoneyDBService moneyDBService,
        MoneySessionStore sessionStore,
        ILogger<MoneyTransactionsXmlRpcController> logger)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _moneyDBService = moneyDBService ?? throw new ArgumentNullException(nameof(moneyDBService));
        if (sessionStore == null) throw new ArgumentNullException(nameof(sessionStore));
        _sessionDic = sessionStore.SessionDic;
        _secureSessionDic = sessionStore.SecureSessionDic;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public XmlRpcResponse BuyCurrency(XmlRpcRequest request, IPEndPoint client)
    {
        _logger.LogInformation("[MONEY RPC]: handleClient buyCurrency.");
        throw new NotImplementedException();
    }

    public XmlRpcResponse GetCurrencyQuote(XmlRpcRequest request, IPEndPoint client)
    {
        _logger.LogInformation("[MONEY RPC]: handleClient getCurrencyQuote.");
        throw new NotImplementedException();
    }

    public XmlRpcResponse LandBuyPrep(XmlRpcRequest request, IPEndPoint client)
    {
        _logger.LogInformation("[MONEY RPC]: handleClient buyLandPrep.");
        throw new NotImplementedException();
    }

    public XmlRpcResponse PreflightBuyLandPrep(XmlRpcRequest request, IPEndPoint client)
    {
        _logger.LogInformation("[MONEY RPC]: handleClient preflightBuyLandPrep.");
        throw new NotImplementedException();
    }

    public XmlRpcResponse HandleTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        int amount = 0;
        int transactionType = 0;
        string senderID = string.Empty;
        string receiverID = string.Empty;
        string senderSessionID = string.Empty;
        string senderSecureSessionID = string.Empty;
        string objectID = string.Empty;
        string objectName = string.Empty;
        string regionHandle = string.Empty;
        string regionUUID = string.Empty;
        string description = "Newly added on";

        responseData["success"] = false;
        UUID transactionUUID = UUID.Random();

        if (requestData.ContainsKey("senderID")) senderID = (string)requestData["senderID"];
        if (requestData.ContainsKey("receiverID")) receiverID = (string)requestData["receiverID"];
        if (requestData.ContainsKey("senderSessionID")) senderSessionID = (string)requestData["senderSessionID"];
        if (requestData.ContainsKey("senderSecureSessionID")) senderSecureSessionID = (string)requestData["senderSecureSessionID"];
        if (requestData.ContainsKey("amount")) amount = Convert.ToInt32(requestData["amount"]);
        if (requestData.ContainsKey("objectID")) objectID = (string)requestData["objectID"];
        if (requestData.ContainsKey("objectName")) objectName = (string)requestData["objectName"];
        if (requestData.ContainsKey("regionHandle")) regionHandle = (string)requestData["regionHandle"];
        if (requestData.ContainsKey("regionUUID")) regionUUID = (string)requestData["regionUUID"];
        if (requestData.ContainsKey("transactionType")) transactionType = Convert.ToInt32(requestData["transactionType"]);
        if (requestData.ContainsKey("description")) description = (string)requestData["description"];

        _logger.LogInformation("[MONEY RPC]: handleTransaction: Transfering money from {0} to {1}, Amount = {2}", senderID, receiverID, amount);
        _logger.LogInformation("[MONEY RPC]: handleTransaction: Object ID = {0}, Object Name = {1}", objectID, objectName);

        if (_sessionDic.ContainsKey(senderID) && _secureSessionDic.ContainsKey(senderID))
        {
            if (_sessionDic[senderID] == senderSessionID && _secureSessionDic[senderID] == senderSecureSessionID)
            {
                _logger.LogInformation("[MONEY RPC]: handleTransaction: Transfering money from {0} to {1}", senderID, receiverID);
                int time = (int)((DateTime.UtcNow.Ticks - _ticksToEpoch) / 10000000);
                try
                {
                    TransactionData transaction = new TransactionData();
                    transaction.TransUUID = transactionUUID;
                    transaction.Sender = senderID;
                    transaction.Receiver = receiverID;
                    transaction.Amount = amount;
                    transaction.ObjectUUID = objectID;
                    transaction.ObjectName = objectName;
                    transaction.RegionHandle = regionHandle;
                    transaction.RegionUUID = regionUUID;
                    transaction.Type = transactionType;
                    transaction.Time = time;
                    transaction.SecureCode = UUID.Random().ToString();
                    transaction.Status = (int)Status.PENDING_STATUS;
                    transaction.CommonName = string.Empty;
                    transaction.Description = description + " " + DateTime.UtcNow.ToString();

                    UserInfo rcvr = _moneyDBService.FetchUserInfo(receiverID);
                    if (rcvr == null)
                    {
                        _logger.LogError("[MONEY RPC]: handleTransaction: Receive User is not yet in DB {0}", receiverID);
                        return response;
                    }

                    bool result = _moneyDBService.addTransaction(transaction);
                    if (result)
                    {
                        UserInfo user = _moneyDBService.FetchUserInfo(senderID);
                        if (user != null)
                        {
                            if (amount > 0 || (_settings.EnableAmountZero && amount == 0))
                            {
                                string sndMessage = string.Empty;
                                string rcvMessage = string.Empty;

                                if (transaction.Type == (int)TransactionType.Gift)
                                {
                                    sndMessage = _settings.BalanceMessageSendGift;
                                    rcvMessage = _settings.BalanceMessageReceiveGift;
                                }
                                else if (transaction.Type == (int)TransactionType.LandSale)
                                {
                                    sndMessage = _settings.BalanceMessageLandSale;
                                    rcvMessage = _settings.BalanceMessageRcvLandSale;
                                }
                                else if (transaction.Type == (int)TransactionType.PayObject)
                                {
                                    sndMessage = _settings.BalanceMessageBuyObject;
                                    rcvMessage = _settings.BalanceMessageSellObject;
                                }
                                else if (transaction.Type == (int)TransactionType.ObjectPays)
                                {
                                    rcvMessage = _settings.BalanceMessageGetMoney;
                                }

                                responseData["success"] = NotifyTransfer(transactionUUID, sndMessage, rcvMessage, objectName);
                            }
                            else if (amount == 0)
                            {
                                responseData["success"] = true;
                            }
                            return response;
                        }
                    }
                    else
                    {
                        _logger.LogError("[MONEY RPC]: handleTransaction: Add transaction for user {0} failed.", senderID);
                    }
                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError("[MONEY RPC]: handleTransaction: Exception occurred while adding transaction: " + e.ToString());
                }
                return response;
            }
        }

        _logger.LogError("[MONEY RPC]: handleTransaction: Session authentication failure for sender " + senderID);
        responseData["message"] = "Session check failure, please re-login later!";
        return response;
    }

    public XmlRpcResponse HandleForceTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        int amount = 0;
        int transactionType = 0;
        string senderID = string.Empty;
        string receiverID = string.Empty;
        string objectID = string.Empty;
        string objectName = string.Empty;
        string regionHandle = string.Empty;
        string regionUUID = string.Empty;
        string description = "Newly added on";

        responseData["success"] = false;
        UUID transactionUUID = UUID.Random();

        if (!_settings.ForceTransfer)
        {
            _logger.LogError("[MONEY RPC]: handleForceTransaction: Not allowed force transfer of Money.");
            _logger.LogError("[MONEY RPC]: handleForceTransaction: Set enableForceTransfer at [MoneyServer] to true in MoneyServer.ini");
            responseData["message"] = "not allowed force transfer of Money!";
            return response;
        }

        if (requestData.ContainsKey("senderID")) senderID = (string)requestData["senderID"];
        if (requestData.ContainsKey("receiverID")) receiverID = (string)requestData["receiverID"];
        if (requestData.ContainsKey("amount")) amount = Convert.ToInt32(requestData["amount"]);
        if (requestData.ContainsKey("objectID")) objectID = (string)requestData["objectID"];
        if (requestData.ContainsKey("objectName")) objectName = (string)requestData["objectName"];
        if (requestData.ContainsKey("regionHandle")) regionHandle = (string)requestData["regionHandle"];
        if (requestData.ContainsKey("regionUUID")) regionUUID = (string)requestData["regionUUID"];
        if (requestData.ContainsKey("transactionType")) transactionType = Convert.ToInt32(requestData["transactionType"]);
        if (requestData.ContainsKey("description")) description = (string)requestData["description"];

        _logger.LogInformation("[MONEY RPC]: handleForceTransaction: Force transfering money from {0} to {1}, Amount = {2}", senderID, receiverID, amount);
        _logger.LogInformation("[MONEY RPC]: handleForceTransaction: Object ID = {0}, Object Name = {1}", objectID, objectName);

        int time = (int)((DateTime.UtcNow.Ticks - _ticksToEpoch) / 10000000);

        try
        {
            TransactionData transaction = new TransactionData();
            transaction.TransUUID = transactionUUID;
            transaction.Sender = senderID;
            transaction.Receiver = receiverID;
            transaction.Amount = amount;
            transaction.ObjectUUID = objectID;
            transaction.ObjectName = objectName;
            transaction.RegionHandle = regionHandle;
            transaction.RegionUUID = regionUUID;
            transaction.Type = transactionType;
            transaction.Time = time;
            transaction.SecureCode = UUID.Random().ToString();
            transaction.Status = (int)Status.PENDING_STATUS;
            transaction.CommonName = string.Empty;
            transaction.Description = description + " " + DateTime.UtcNow.ToString();

            UserInfo rcvr = _moneyDBService.FetchUserInfo(receiverID);
            if (rcvr == null)
            {
                _logger.LogError("[MONEY RPC]: handleForceTransaction: Force receive User is not yet in DB {0}", receiverID);
                return response;
            }

            bool result = _moneyDBService.addTransaction(transaction);
            if (result)
            {
                UserInfo user = _moneyDBService.FetchUserInfo(senderID);
                if (user != null)
                {
                    if (amount > 0 || (_settings.EnableAmountZero && amount == 0))
                    {
                        string sndMessage = string.Empty;
                        string rcvMessage = string.Empty;

                        if (transaction.Type == (int)TransactionType.Gift)
                        {
                            sndMessage = _settings.BalanceMessageSendGift;
                            rcvMessage = _settings.BalanceMessageReceiveGift;
                        }
                        else if (transaction.Type == (int)TransactionType.LandSale)
                        {
                            sndMessage = _settings.BalanceMessageLandSale;
                            sndMessage = _settings.BalanceMessageRcvLandSale;
                        }
                        else if (transaction.Type == (int)TransactionType.PayObject)
                        {
                            sndMessage = _settings.BalanceMessageBuyObject;
                            rcvMessage = _settings.BalanceMessageSellObject;
                        }
                        else if (transaction.Type == (int)TransactionType.ObjectPays)
                        {
                            rcvMessage = _settings.BalanceMessageGetMoney;
                        }

                        responseData["success"] = NotifyTransfer(transactionUUID, sndMessage, rcvMessage, objectName);
                    }
                    else if (amount == 0)
                    {
                        responseData["success"] = true;
                    }
                    return response;
                }
            }
            else
            {
                _logger.LogError("[MONEY RPC]: handleForceTransaction: Add force transaction for user {0} failed.", senderID);
            }
            return response;
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleForceTransaction: Exception occurred while adding force transaction: " + e.ToString());
        }
        return response;
    }

    public XmlRpcResponse HandleScriptTransaction(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        int amount = 0;
        int transactionType = 0;
        string senderID = UUID.Zero.ToString();
        string receiverID = UUID.Zero.ToString();
        string clientIP = remoteClient.Address.ToString();
        string secretCode = string.Empty;
        string description = "Scripted Send Money from/to Avatar on";

        responseData["success"] = false;
        UUID transactionUUID = UUID.Random();

        if (!_settings.ScriptSendMoney || _settings.ScriptAccessKey == "")
        {
            _logger.LogError("[MONEY RPC]: handleScriptTransaction: Not allowed send money to avatar!!");
            _logger.LogError("[MONEY RPC]: handleScriptTransaction: Set enableScriptSendMoney and MoneyScriptAccessKey at [MoneyServer] in MoneyServer.ini");
            responseData["message"] = "not allowed set money to avatar!";
            return response;
        }

        if (requestData.ContainsKey("senderID")) senderID = (string)requestData["senderID"];
        if (requestData.ContainsKey("receiverID")) receiverID = (string)requestData["receiverID"];
        if (requestData.ContainsKey("amount")) amount = Convert.ToInt32(requestData["amount"]);
        if (requestData.ContainsKey("transactionType")) transactionType = Convert.ToInt32(requestData["transactionType"]);
        if (requestData.ContainsKey("description")) description = (string)requestData["description"];
        if (requestData.ContainsKey("secretAccessCode")) secretCode = (string)requestData["secretAccessCode"];

        MD5 md5 = MD5.Create();
        byte[] code = md5.ComputeHash(ASCIIEncoding.Default.GetBytes(_settings.ScriptAccessKey + "_" + clientIP));
        string hash = BitConverter.ToString(code).ToLower().Replace("-", "");
        code = md5.ComputeHash(ASCIIEncoding.Default.GetBytes(hash + "_" + _settings.ScriptIPAddress));
        hash = BitConverter.ToString(code).ToLower().Replace("-", "");

        if (secretCode.ToLower() != hash)
        {
            _logger.LogError("[MONEY RPC]: handleScriptTransaction: Not allowed send money to avatar!!");
            _logger.LogError("[MONEY RPC]: handleScriptTransaction: Not match Script Access Key.");
            responseData["message"] = "not allowed send money to avatar! not match Script Key";
            return response;
        }

        _logger.LogInformation("[MONEY RPC]: handleScriptTransaction: Send money from {0} to {1}", senderID, receiverID);
        int time = (int)((DateTime.UtcNow.Ticks - _ticksToEpoch) / 10000000);

        try
        {
            TransactionData transaction = new TransactionData();
            transaction.TransUUID = transactionUUID;
            transaction.Sender = senderID;
            transaction.Receiver = receiverID;
            transaction.Amount = amount;
            transaction.ObjectUUID = UUID.Zero.ToString();
            transaction.RegionHandle = "0";
            transaction.Type = transactionType;
            transaction.Time = time;
            transaction.SecureCode = UUID.Random().ToString();
            transaction.Status = (int)Status.PENDING_STATUS;
            transaction.CommonName = string.Empty;
            transaction.Description = description + " " + DateTime.UtcNow.ToString();

            UserInfo senderInfo = null;
            UserInfo receiverInfo = null;
            if (transaction.Sender != UUID.Zero.ToString()) senderInfo = _moneyDBService.FetchUserInfo(transaction.Sender);
            if (transaction.Receiver != UUID.Zero.ToString()) receiverInfo = _moneyDBService.FetchUserInfo(transaction.Receiver);

            if (senderInfo == null && receiverInfo == null)
            {
                _logger.LogError("[MONEY RPC]: handleScriptTransaction: Sender and Receiver are not yet in DB, or both of them are System: {0}, {1}",
                    transaction.Sender, transaction.Receiver);
                return response;
            }

            bool result = _moneyDBService.addTransaction(transaction);
            if (result)
            {
                if (amount > 0 || (_settings.EnableAmountZero && amount == 0))
                {
                    if (_moneyDBService.DoTransfer(transactionUUID))
                    {
                        transaction = _moneyDBService.FetchTransaction(transactionUUID);
                        if (transaction != null && transaction.Status == (int)Status.SUCCESS_STATUS)
                        {
                            _logger.LogInformation("[MONEY RPC]: handleScriptTransaction: ScriptTransaction money finished successfully, now update balance {0}",
                                transactionUUID.ToString());
                            string message = string.Empty;
                            if (senderInfo != null)
                            {
                                if (receiverInfo == null) message = string.Format(_settings.BalanceMessageSendMoney, amount, "SYSTEM", "");
                                else message = string.Format(_settings.BalanceMessageSendMoney, amount, receiverInfo.Avatar, "");
                                UpdateBalance(transaction.Sender, message);
                                _logger.LogInformation("[MONEY RPC]: handleScriptTransaction: Update balance of {0}. Message = {1}", transaction.Sender, message);
                            }
                            if (receiverInfo != null)
                            {
                                if (senderInfo == null) message = string.Format(_settings.BalanceMessageReceiveMoney, amount, "SYSTEM", "");
                                else message = string.Format(_settings.BalanceMessageReceiveMoney, amount, senderInfo.Avatar, "");
                                UpdateBalance(transaction.Receiver, message);
                                _logger.LogInformation("[MONEY RPC]: handleScriptTransaction: Update balance of {0}. Message = {1}", transaction.Receiver, message);
                            }

                            responseData["success"] = true;
                        }
                    }
                }
                else if (amount == 0)
                {
                    responseData["success"] = true;
                }
                return response;
            }
            else
            {
                _logger.LogError("[MONEY RPC]: handleScriptTransaction: Add force transaction for user {0} failed.", transaction.Sender);
            }
            return response;
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleScriptTransaction: Exception occurred while adding money transaction: " + e.ToString());
        }
        return response;
    }

    public XmlRpcResponse HandleAddBankerMoney(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        int amount = 0;
        int transactionType = 0;
        string senderID = UUID.Zero.ToString();
        string bankerID = string.Empty;
        string regionHandle = "0";
        string regionUUID = UUID.Zero.ToString();
        string description = "Add Money to Avatar on";

        responseData["success"] = false;
        UUID transactionUUID = UUID.Random();

        if (requestData.ContainsKey("bankerID")) bankerID = (string)requestData["bankerID"];
        if (requestData.ContainsKey("amount")) amount = Convert.ToInt32(requestData["amount"]);
        if (requestData.ContainsKey("regionHandle")) regionHandle = (string)requestData["regionHandle"];
        if (requestData.ContainsKey("regionUUID")) regionUUID = (string)requestData["regionUUID"];
        if (requestData.ContainsKey("transactionType")) transactionType = Convert.ToInt32(requestData["transactionType"]);
        if (requestData.ContainsKey("description")) description = (string)requestData["description"];

        if (_settings.BankerAvatar != UUID.Zero.ToString() && _settings.BankerAvatar != bankerID)
        {
            _logger.LogError("[MONEY RPC]: handleAddBankerMoney: Not allowed add money to avatar!!");
            _logger.LogError("[MONEY RPC]: handleAddBankerMoney: Set BankerAvatar at [MoneyServer] in MoneyServer.ini");
            responseData["message"] = "not allowed add money to avatar!";
            responseData["banker"] = false;
            return response;
        }
        responseData["banker"] = true;

        _logger.LogInformation("[MONEY RPC]: handleAddBankerMoney: Add money to avatar {0}", bankerID);
        int time = (int)((DateTime.UtcNow.Ticks - _ticksToEpoch) / 10000000);

        try
        {
            TransactionData transaction = new TransactionData();
            transaction.TransUUID = transactionUUID;
            transaction.Sender = senderID;
            transaction.Receiver = bankerID;
            transaction.Amount = amount;
            transaction.ObjectUUID = UUID.Zero.ToString();
            transaction.RegionHandle = regionHandle;
            transaction.RegionUUID = regionUUID;
            transaction.Type = transactionType;
            transaction.Time = time;
            transaction.SecureCode = UUID.Random().ToString();
            transaction.Status = (int)Status.PENDING_STATUS;
            transaction.CommonName = string.Empty;
            transaction.Description = description + " " + DateTime.UtcNow.ToString();

            UserInfo rcvr = _moneyDBService.FetchUserInfo(bankerID);
            if (rcvr == null)
            {
                _logger.LogError("[MONEY RPC]: handleAddBankerMoney: Avatar is not yet in DB {0}", bankerID);
                return response;
            }

            bool result = _moneyDBService.addTransaction(transaction);
            if (result)
            {
                if (amount > 0 || (_settings.EnableAmountZero && amount == 0))
                {
                    if (_moneyDBService.DoAddMoney(transactionUUID))
                    {
                        transaction = _moneyDBService.FetchTransaction(transactionUUID);
                        if (transaction != null && transaction.Status == (int)Status.SUCCESS_STATUS)
                        {
                            _logger.LogInformation("[MONEY RPC]: handleAddBankerMoney: Adding money finished successfully, now update balance: {0}", transactionUUID.ToString());
                            string message = string.Format(_settings.BalanceMessageBuyMoney, amount, "SYSTEM", "");
                            UpdateBalance(transaction.Receiver, message);
                            responseData["success"] = true;
                        }
                    }
                }
                else if (amount == 0)
                {
                    responseData["success"] = true;
                }
                return response;
            }
            else
            {
                _logger.LogError("[MONEY RPC]: handleAddBankerMoney: Add force transaction for user {0} failed.", senderID);
            }
            return response;
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleAddBankerMoney: Exception occurred while adding money transaction: " + e.ToString());
        }
        return response;
    }

    public XmlRpcResponse HandlePayMoneyCharge(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        int amount = 0;
        int transactionType = 0;
        string senderID = string.Empty;
        string receiverID = UUID.Zero.ToString();
        string senderSessionID = string.Empty;
        string senderSecureSessionID = string.Empty;
        string objectID = UUID.Zero.ToString();
        string objectName = string.Empty;
        string regionHandle = string.Empty;
        string regionUUID = string.Empty;
        string description = "Pay Charge on";

        responseData["success"] = false;
        UUID transactionUUID = UUID.Random();

        if (requestData.ContainsKey("senderID")) senderID = (string)requestData["senderID"];
        if (requestData.ContainsKey("senderSessionID")) senderSessionID = (string)requestData["senderSessionID"];
        if (requestData.ContainsKey("senderSecureSessionID")) senderSecureSessionID = (string)requestData["senderSecureSessionID"];
        if (requestData.ContainsKey("amount")) amount = Convert.ToInt32(requestData["amount"]);
        if (requestData.ContainsKey("regionHandle")) regionHandle = (string)requestData["regionHandle"];
        if (requestData.ContainsKey("regionUUID")) regionUUID = (string)requestData["regionUUID"];
        if (requestData.ContainsKey("transactionType")) transactionType = Convert.ToInt32(requestData["transactionType"]);
        if (requestData.ContainsKey("description")) description = (string)requestData["description"];

        if (requestData.ContainsKey("receiverID")) receiverID = (string)requestData["receiverID"];
        if (requestData.ContainsKey("objectID")) objectID = (string)requestData["objectID"];
        if (requestData.ContainsKey("objectName")) objectName = (string)requestData["objectName"];

        _logger.LogInformation("[MONEY RPC]: handlePayMoneyCharge: Transfering money from {0} to {1}, Amount = {2}", senderID, receiverID, amount);
        _logger.LogInformation("[MONEY RPC]: handlePayMoneyCharge: Object ID = {0}, Object Name = {1}", objectID, objectName);

        if (_sessionDic.ContainsKey(senderID) && _secureSessionDic.ContainsKey(senderID))
        {
            if (_sessionDic[senderID] == senderSessionID && _secureSessionDic[senderID] == senderSecureSessionID)
            {
                _logger.LogInformation("[MONEY RPC]: handlePayMoneyCharge: Pay from {0}", senderID);
                int time = (int)((DateTime.UtcNow.Ticks - _ticksToEpoch) / 10000000);
                try
                {
                    TransactionData transaction = new TransactionData();
                    transaction.TransUUID = transactionUUID;
                    transaction.Sender = senderID;
                    transaction.Receiver = receiverID;
                    transaction.Amount = amount;
                    transaction.ObjectUUID = objectID;
                    transaction.ObjectName = objectName;
                    transaction.RegionHandle = regionHandle;
                    transaction.RegionUUID = regionUUID;
                    transaction.Type = transactionType;
                    transaction.Time = time;
                    transaction.SecureCode = UUID.Random().ToString();
                    transaction.Status = (int)Status.PENDING_STATUS;
                    transaction.CommonName = String.Empty;
                    transaction.Description = description + " " + DateTime.UtcNow.ToString();

                    bool result = _moneyDBService.addTransaction(transaction);
                    if (result)
                    {
                        UserInfo user = _moneyDBService.FetchUserInfo(senderID);
                        if (user != null)
                        {
                            if (amount > 0 || (_settings.EnableAmountZero && amount == 0))
                            {
                                string message = string.Format(_settings.BalanceMessagePayCharge, amount, "SYSTEM", "");
                                responseData["success"] = NotifyTransfer(transactionUUID, message, "", "");
                            }
                            else if (amount == 0)
                            {
                                responseData["success"] = true;
                            }
                            return response;
                        }
                    }
                    else
                    {
                        _logger.LogError("[MONEY RPC]: handlePayMoneyCharge: Pay money transaction for user {0} failed.", senderID);
                    }
                    return response;
                }
                catch (Exception e)
                {
                    _logger.LogError("[MONEY RPC]: handlePayMoneyCharge: Exception occurred while pay money transaction: " + e.ToString());
                }
                return response;
            }
        }

        _logger.LogError("[MONEY RPC]: handlePayMoneyCharge: Session authentication failure for sender " + senderID);
        responseData["message"] = "Session check failure, please re-login later!";
        return response;
    }

    public bool NotifyTransfer(UUID transactionUUID, string msg2sender, string msg2receiver, string objectName)
    {
        try
        {
            if (_moneyDBService.DoTransfer(transactionUUID))
            {
                TransactionData transaction = _moneyDBService.FetchTransaction(transactionUUID);
                if (transaction != null && transaction.Status == (int)Status.SUCCESS_STATUS)
                {
                    _logger.LogInformation("[MONEY RPC]: NotifyTransfer: Transaction Type = {0}", transaction.Type);
                    _logger.LogInformation("[MONEY RPC]: NotifyTransfer: Payment finished successfully, now update balance {0}", transactionUUID.ToString());

                    bool updateSender = true;
                    bool updateReceiv = true;
                    if (transaction.Sender == transaction.Receiver) updateSender = false;
                    if (transaction.Type == (int)TransactionType.UploadCharge) updateReceiv = false;

                    if (updateSender)
                    {
                        UserInfo receiverInfo = _moneyDBService.FetchUserInfo(transaction.Receiver);
                        string receiverName = "unknown user";
                        if (receiverInfo != null) receiverName = receiverInfo.Avatar;
                        string sndMessage = string.Format(msg2sender, transaction.Amount, receiverName, objectName);
                        UpdateBalance(transaction.Sender, sndMessage);
                    }
                    if (updateReceiv)
                    {
                        UserInfo senderInfo = _moneyDBService.FetchUserInfo(transaction.Sender);
                        string senderName = "unknown user";
                        if (senderInfo != null) senderName = senderInfo.Avatar;
                        string rcvMessage = string.Format(msg2receiver, transaction.Amount, senderName, objectName);
                        UpdateBalance(transaction.Receiver, rcvMessage);
                    }

                    if (transaction.Type == (int)TransactionType.PayObject)
                    {
                        _logger.LogInformation("[MONEY RPC]: NotifyTransfer: Now notify opensim to give object to customer {0} ", transaction.Sender);
                        Hashtable requestTable = new Hashtable();
                        requestTable["clientUUID"] = transaction.Sender;
                        requestTable["receiverUUID"] = transaction.Receiver;

                        if (_sessionDic.ContainsKey(transaction.Sender) && _secureSessionDic.ContainsKey(transaction.Sender))
                        {
                            requestTable["clientSessionID"] = _sessionDic[transaction.Sender];
                            requestTable["clientSecureSessionID"] = _secureSessionDic[transaction.Sender];
                        }
                        else
                        {
                            requestTable["clientSessionID"] = UUID.Zero.ToString();
                            requestTable["clientSecureSessionID"] = UUID.Zero.ToString();
                        }
                        requestTable["transactionType"] = transaction.Type;
                        requestTable["amount"] = transaction.Amount;
                        requestTable["objectID"] = transaction.ObjectUUID;
                        requestTable["objectName"] = transaction.ObjectName;
                        requestTable["regionHandle"] = transaction.RegionHandle;

                        UserInfo user = _moneyDBService.FetchUserInfo(transaction.Sender);
                        if (user != null)
                        {
                            Hashtable responseTable = genericCurrencyXMLRPCRequest(requestTable, "OnMoneyTransfered", user.SimIP);

                            if (responseTable != null && responseTable.ContainsKey("success"))
                            {
                                if (!(bool)responseTable["success"])
                                {
                                    _logger.LogError("[MONEY RPC]: NotifyTransfer: User {0} can't get the object, rolling back.", transaction.Sender);
                                    if (RollBackTransaction(transaction))
                                    {
                                        _logger.LogError("[MONEY RPC]: NotifyTransfer: Transaction {0} failed but roll back succeeded.", transactionUUID.ToString());
                                    }
                                    else
                                    {
                                        _logger.LogError("[MONEY RPC]: NotifyTransfer: Transaction {0} failed and roll back failed as well.",
                                            transactionUUID.ToString());
                                    }
                                }
                                else
                                {
                                    _logger.LogInformation("[MONEY RPC]: NotifyTransfer: Transaction {0} finished successfully.", transactionUUID.ToString());
                                    return true;
                                }
                            }
                        }
                        return false;
                    }
                    return true;
                }
            }
            _logger.LogError("[MONEY RPC]: NotifyTransfer: Transaction {0} failed.", transactionUUID.ToString());
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: NotifyTransfer: exception occurred when transaction {0}: {1}", transactionUUID.ToString(), e.ToString());
        }

        return false;
    }

    private Hashtable genericCurrencyXMLRPCRequest(Hashtable reqParams, string method, string uri)
    {
        if (reqParams.Count <= 0 || string.IsNullOrEmpty(method)) return null;

        ArrayList arrayParams = new ArrayList();
        arrayParams.Add(reqParams);
        XmlRpcResponse moneyServResp = null;
        try
        {
            XmlRpcRequest moneyModuleReq = new XmlRpcRequest(method, arrayParams);
            moneyServResp = moneyModuleReq.Send(uri, MONEYMODULE_REQUEST_TIMEOUT);
        }
        catch (Exception ex)
        {
            _logger.LogError("[MONEY RPC]: genericCurrencyXMLRPCRequest: Unable to connect to Region Server {0}", uri);
            _logger.LogError("[MONEY RPC]: genericCurrencyXMLRPCRequest: {0}", ex.ToString());

            Hashtable errorHash = new Hashtable();
            errorHash["success"] = false;
            errorHash["errorMessage"] = "Failed to perform actions on OpenSim Server";
            errorHash["errorURI"] = "";
            return errorHash;
        }

        if (moneyServResp == null || moneyServResp.IsFault)
        {
            Hashtable errorHash = new Hashtable();
            errorHash["success"] = false;
            errorHash["errorMessage"] = "Failed to perform actions on OpenSim Server";
            errorHash["errorURI"] = "";
            return errorHash;
        }

        Hashtable moneyRespData = (Hashtable)moneyServResp.Value;
        return moneyRespData;
    }

    private void UpdateBalance(string userID, string message)
    {
        string sessionID = string.Empty;
        string secureID = string.Empty;

        if (_sessionDic.ContainsKey(userID) && _secureSessionDic.ContainsKey(userID))
        {
            sessionID = _sessionDic[userID];
            secureID = _secureSessionDic[userID];

            Hashtable requestTable = new Hashtable();
            requestTable["clientUUID"] = userID;
            requestTable["clientSessionID"] = sessionID;
            requestTable["clientSecureSessionID"] = secureID;
            requestTable["Balance"] = _moneyDBService.getBalance(userID);
            if (message != "") requestTable["Message"] = message;

            UserInfo user = _moneyDBService.FetchUserInfo(userID);
            if (user != null)
            {
                genericCurrencyXMLRPCRequest(requestTable, "UpdateBalance", user.SimIP);
                _logger.LogInformation("[MONEY RPC]: UpdateBalance: Sended UpdateBalance Request to {0}", user.SimIP.ToString());
            }
        }
    }

    protected bool RollBackTransaction(TransactionData transaction)
    {
        if (_moneyDBService.withdrawMoney(transaction.TransUUID, transaction.Receiver, transaction.Amount))
        {
            if (_moneyDBService.giveMoney(transaction.TransUUID, transaction.Sender, transaction.Amount))
            {
                _logger.LogInformation("[MONEY RPC]: RollBackTransaction: Transaction {0} is successfully.", transaction.TransUUID.ToString());
                _moneyDBService.updateTransactionStatus(transaction.TransUUID, (int)Status.FAILED_STATUS,
                    "The buyer failed to get the object, roll back the transaction");
                UserInfo senderInfo = _moneyDBService.FetchUserInfo(transaction.Sender);
                UserInfo receiverInfo = _moneyDBService.FetchUserInfo(transaction.Receiver);
                string senderName = "unknown user";
                string receiverName = "unknown user";
                if (senderInfo != null) senderName = senderInfo.Avatar;
                if (receiverInfo != null) receiverName = receiverInfo.Avatar;

                string sndMessage = string.Format(_settings.BalanceMessageRollBack, transaction.Amount, receiverName, transaction.ObjectName);
                string rcvMessage = string.Format(_settings.BalanceMessageRollBack, transaction.Amount, senderName, transaction.ObjectName);

                if (transaction.Sender != transaction.Receiver) UpdateBalance(transaction.Sender, sndMessage);
                UpdateBalance(transaction.Receiver, rcvMessage);
                return true;
            }
        }
        return false;
    }

    public XmlRpcResponse HandleCancelTransfer(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        Hashtable requestData = (Hashtable)request.Params[0];
        XmlRpcResponse response = new XmlRpcResponse();
        Hashtable responseData = new Hashtable();
        response.Value = responseData;

        string secureCode = string.Empty;
        string transactionID = string.Empty;
        UUID transactionUUID = UUID.Zero;

        responseData["success"] = false;

        if (requestData.ContainsKey("secureCode")) secureCode = (string)requestData["secureCode"];
        if (requestData.ContainsKey("transactionID"))
        {
            transactionID = (string)requestData["transactionID"];
            UUID.TryParse(transactionID, out transactionUUID);
        }

        if (string.IsNullOrEmpty(secureCode) || string.IsNullOrEmpty(transactionID))
        {
            _logger.LogError("[MONEY RPC]: handleCancelTransfer: secureCode and/or transactionID are empty.");
            return response;
        }

        TransactionData transaction = _moneyDBService.FetchTransaction(transactionUUID);
        UserInfo user = _moneyDBService.FetchUserInfo(transaction.Sender);

        try
        {
            _logger.LogInformation("[MONEY RPC]: handleCancelTransfer: User {0} wanted to cancel the transaction.", user.Avatar);
            if (_moneyDBService.ValidateTransfer(secureCode, transactionUUID))
            {
                _logger.LogInformation("[MONEY RPC]: handleCancelTransfer: User {0} has canceled the transaction {1}", user.Avatar, transactionID);
                _moneyDBService.updateTransactionStatus(transactionUUID, (int)Status.FAILED_STATUS,
                    "User canceled the transaction on " + DateTime.UtcNow.ToString());
                responseData["success"] = true;
            }
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: handleCancelTransfer: Exception occurred when transaction {0}: {1}", transactionID, e.ToString());
        }
        return response;
    }
}

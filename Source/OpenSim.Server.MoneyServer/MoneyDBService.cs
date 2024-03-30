/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *     * Redistributions of source code must retain the above copyright
 *       notice, this list of conditions and the following disclaimer.
 *     * Redistributions in binary form must reproduce the above copyright
 *       notice, this list of conditions and the following disclaimer in the
 *       documentation and/or other materials provided with the distribution.
 *     * Neither the name of the OpenSim Project nor the
 *       names of its contributors may be used to endorse or promote products
 *       derived from this software without specific prior written permission.
 *
 * THIS SOFTWARE IS PROVIDED BY THE DEVELOPERS ``AS IS'' AND ANY
 * EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED
 * WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE
 * DISCLAIMED. IN NO EVENT SHALL THE CONTRIBUTORS BE LIABLE FOR ANY
 * DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES
 * (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES
 * LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND
 * ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT
 * (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS
 * SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.
 */

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

using OpenSim.Data.MySQL.MoneyData;
using OpenSim.Region.OptionalModules.Currency;
using OpenMetaverse;
using MySqlConnector;

namespace OpenSim.Server.MoneyServer
{
    public class MoneyDBService : IMoneyDBService
    { 
        private string m_connect;

        //private MySQLMoneyManager m_moneyManager;
        private long TicksToEpoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;

        // DB manager pool
        protected Dictionary<int, MySQLSuperManager> m_dbconnections = new Dictionary<int, MySQLSuperManager>();	// with Lock
        private int m_maxConnections;

        public int m_lastConnect = 0;

        private readonly IConfiguration _configuration;
        private readonly ILoggerFactory _loggerFactory;
        private readonly ILogger<MoneyDBService> _logger;

        public MoneyDBService(IConfiguration configuration, ILoggerFactory loggerFactory)
        {
            _configuration = configuration;
            _loggerFactory = loggerFactory;

            _logger = _loggerFactory.CreateLogger<MoneyDBService>();
        }

        public void Initialise(string connectionString, int maxDBConnections)
        {
            m_connect = connectionString;
            m_maxConnections = maxDBConnections;
            
            if (connectionString != string.Empty)
            {
                //m_moneyManager = new MySQLMoneyManager(connectionString);
                _logger.LogInformation($"Creating {m_maxConnections} DB connections...");

                for (int i = 0; i < m_maxConnections; i++)
                {
                    //m_log.Info("Connecting to DB... [" + i + "]");
                    MySQLSuperManager msm = new MySQLSuperManager();

                    var managerLogger = _loggerFactory.CreateLogger<MySQLMoneyManager>();
                    msm.Manager = new MySQLMoneyManager(_configuration, managerLogger);
                    msm.Manager.Initialize(connectionString);

                    m_dbconnections.Add(i, msm);
                }
            }
            else
            {
                _logger.LogError("[MONEY DB]: Connection string is null, initialise database failed");
                throw new Exception("Failed to initialise MySql database");
            }
        }


        public void Reconnect()
        {
            for (int i = 0; i < m_maxConnections; i++)
            {
                MySQLSuperManager msm = m_dbconnections[i];
                msm.Manager.Reconnect();
            }
        }


        private MySQLSuperManager GetLockedConnection()
        {
            int lockedCons = 0;
            while (true)
            {
                m_lastConnect++;

                // Overflow protection
                if (m_lastConnect == int.MaxValue) m_lastConnect = 0;

                MySQLSuperManager msm = m_dbconnections[m_lastConnect % m_maxConnections];
                if (!msm.Locked)
                {
                    msm.GetLock();
                    return msm;
                }

                lockedCons++;
                if (lockedCons > m_maxConnections)
                {
                    lockedCons = 0;
                    System.Threading.Thread.Sleep(1000); // Wait some time before searching them again.
                    _logger.LogDebug("WARNING: All threads are in use. Probable cause: Something didnt release a mutex properly, or high volume of requests inbound.");
                }
            }
        }


        public int getBalance(string userID)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.getBalance(userID);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "getBalance() mysql exception, Retrying.");

                dbm.Manager.Reconnect();
                return dbm.Manager.getBalance(userID);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "getBalance() general exception. Aborting.");
                return 0;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool withdrawMoney(UUID transactionID, string senderID, int amount)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.withdrawMoney(transactionID, senderID, amount);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "withdrawMoney() mysql exception, Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.withdrawMoney(transactionID, senderID, amount);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "withdrawMoney() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool giveMoney(UUID transactionID, string receiverID, int amount)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.giveMoney(transactionID, receiverID, amount);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "giveMoney() MySQLException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.giveMoney(transactionID, receiverID, amount);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "giveMoney() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool setTotalSale(MoneyTransactionData transaction)
        {
            if (transaction.Receiver == transaction.Sender) return false;
            if (transaction.Sender == UUID.Zero.ToString()) return false;

            MySQLSuperManager dbm = GetLockedConnection();

            int time = (int)((DateTime.UtcNow.Ticks - TicksToEpoch) / 10000000);
            try
            {
                return dbm.Manager.setTotalSale(transaction.Receiver, transaction.ObjectUUID, transaction.Type, 1, transaction.Amount, time);
            }
            catch (MySqlException)
            {
                dbm.Manager.Reconnect();
                return dbm.Manager.setTotalSale(transaction.Receiver, transaction.ObjectUUID, transaction.Type, 1, transaction.Amount, time);
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool addTransaction(MoneyTransactionData transaction)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.addTransaction(transaction);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "addTransaction() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.addTransaction(transaction);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "addTransaction() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool addUser(string userID, int balance, int status, int type)
        {
            MoneyTransactionData transaction = new MoneyTransactionData();
            transaction.TransUUID = UUID.Random();
            transaction.Sender = UUID.Zero.ToString();
            transaction.Receiver = userID;
            transaction.Amount = balance;
            transaction.ObjectUUID = UUID.Zero.ToString();
            transaction.ObjectName = string.Empty;
            transaction.RegionHandle = string.Empty;
            transaction.Type = (int)TransactionType.BirthGift;
            transaction.Time = (int)((DateTime.UtcNow.Ticks - TicksToEpoch) / 10000000); ;
            transaction.Status = (int)Status.PENDING_STATUS;
            transaction.SecureCode = UUID.Random().ToString();
            transaction.CommonName = string.Empty;
            transaction.Description = "addUser " + DateTime.UtcNow.ToString();

            bool ret = addTransaction(transaction);
            if (!ret) return false;

            //
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                ret = dbm.Manager.addUser(userID, 0, status, type);		// make Balance Table
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "addUser() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                ret = dbm.Manager.addUser(userID, 0, status, type);     // make Balance Table
            }
            catch (Exception e)
            {
                _logger.LogError(e, "addUser() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }

            //
            if (ret) ret = giveMoney(transaction.TransUUID, userID, balance);
            return ret;
        }


        public bool updateTransactionStatus(UUID transactionID, int status, string description)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.updateTransactionStatus(transactionID, status, description);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "updateTransactionStatus() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.updateTransactionStatus(transactionID, status, description);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "updateTransactionStatus() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool SetTransExpired(int deadTime)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.SetTransExpired(deadTime);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "SetTransExpired() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.SetTransExpired(deadTime);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "SetTransExpired() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public bool ValidateTransfer(string secureCode, UUID transactionID)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.ValidateTransfer(secureCode, transactionID);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "ValidateTransfer() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.ValidateTransfer(secureCode, transactionID);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "ValidateTransfer() general exception. Aborting.");
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public MoneyTransactionData FetchTransaction(UUID transactionID)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.FetchTransaction(transactionID);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "FetchTransaction() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.FetchTransaction(transactionID);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "FetchTransaction() general exception. Aborting.");
                return null;
            }
            finally
            {
                dbm.Release();
            }
        }


        public MoneyTransactionData FetchTransaction(string userID, int startTime, int endTime, int lastIndex)
        {
            MySQLSuperManager dbm = GetLockedConnection();
            MoneyTransactionData[] arrTransaction;

            uint index = 0;
            if (lastIndex >= 0) index = Convert.ToUInt32(lastIndex) + 1;

            try
            {
                arrTransaction = dbm.Manager.FetchTransaction(userID, startTime, endTime, index, 1);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "FetchTransaction2() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                arrTransaction = dbm.Manager.FetchTransaction(userID, startTime, endTime, index, 1);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "FetchTransaction2() general exception. Aborting.");
                return null;
            }
            finally
            {
                dbm.Release();
            }

            //
            if (arrTransaction.Length > 0)
            {
                return arrTransaction[0];
            }
            else
            {
                return null;
            }
        }


        public bool DoTransfer(UUID transactionUUID)
        {
            bool do_trans = false;

            MoneyTransactionData transaction = new MoneyTransactionData();
            transaction = FetchTransaction(transactionUUID);

            if (transaction != null && transaction.Status == (int)Status.PENDING_STATUS)
            {
                int balance = getBalance(transaction.Sender);

                //check the amount
                if (transaction.Amount >= 0 && balance >= transaction.Amount)
                {
                    if (withdrawMoney(transactionUUID, transaction.Sender, transaction.Amount))
                    {
                        //If receiver not found, add it to DB.
                        if (getBalance(transaction.Receiver) == -1)
                        {
                            _logger.LogError("[MONEY DB]: DoTransfer: Receiver not found in balances DB. {0}", transaction.Receiver);
                            return false;
                        }

                        if (giveMoney(transactionUUID, transaction.Receiver, transaction.Amount))
                        {
                            do_trans = true;
                        }
                        else
                        {	// give money to receiver failed. Refund Processing
                            _logger.LogError("[MONEY DB]: Give money to receiver {0} failed", transaction.Receiver);
                            //Return money to sender
                            if (giveMoney(transactionUUID, transaction.Sender, transaction.Amount))
                            {
                                _logger.LogError("[MONEY DB]: give money to receiver {0} failed but return it to sender {1} successfully",
                                                        transaction.Receiver, transaction.Sender);
                                updateTransactionStatus(transactionUUID, (int)Status.FAILED_STATUS, "give money to receiver failed but return it to sender successfully");
                            }
                            else
                            {
                                _logger.LogError("[MONEY DB]: FATAL ERROR: Money withdrawn from sender: {0}, but failed to be given to receiver {1}",
                                                        transaction.Sender, transaction.Receiver);
                                updateTransactionStatus(transactionUUID, (int)Status.ERROR_STATUS, "give money to receiver failed, and return it to sender unsuccessfully!!!");
                            }
                        }
                    }
                    else
                    {	// withdraw money failed
                        _logger.LogError("[MONEY DB]: Withdraw money from sender {0} failed", transaction.Sender);
                    }
                }
                else
                {	// not enough balance to finish the transaction
                    _logger.LogError("[MONEY DB]: Not enough balance for user: {0} to apply the transaction.", transaction.Sender);
                }
            }
            else
            {	// Can not fetch the transaction or it has expired
                _logger.LogError("[MONEY DB]: The transaction:{0} has expired", transactionUUID.ToString());
            }

            //
            if (do_trans)
            {
                setTotalSale(transaction);
            }

            return do_trans;
        }


        // by Fumi.Iseki
        public bool DoAddMoney(UUID transactionUUID)
        {
            MoneyTransactionData transaction = new MoneyTransactionData();
            transaction = FetchTransaction(transactionUUID);

            if (transaction != null && transaction.Status == (int)Status.PENDING_STATUS)
            {
                //If receiver not found, add it to DB.
                if (getBalance(transaction.Receiver) == -1)
                {
                    _logger.LogError("[MONEY DB]: DoAddMoney: Receiver not found in balances DB. {0}", transaction.Receiver);
                    return false;
                }
                //
                if (giveMoney(transactionUUID, transaction.Receiver, transaction.Amount))
                {
                    setTotalSale(transaction);
                    return true;
                }
                else
                {	// give money to receiver failed.
                    _logger.LogError("[MONEY DB]: Add money to receiver {0} failed", transaction.Receiver);
                    updateTransactionStatus(transactionUUID, (int)Status.FAILED_STATUS, "add money to receiver failed");
                }
            }
            else
            {	// Can not fetch the transaction or it has expired
                _logger.LogError("[MONEY DB]: The transaction:{0} has expired", transactionUUID.ToString());
            }

            return false;
        }


        ///////////////////////////////////////////////////////////////////////////////////////////////////////
        //
        // userinfo
        //

        public bool TryAddUserInfo(UserInfo user)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            UserInfo userInfo = null;

            try
            {
                userInfo = dbm.Manager.fetchUserInfo(user.UserID);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "TryAddUserInfo() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                userInfo = dbm.Manager.fetchUserInfo(user.UserID);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "TryAddUserInfo() Exception. Aborting.");
                dbm.Release();
                return false;
            }

            try
            {
                if (userInfo != null)
                {
                    //m_log.InfoFormat("[MONEY DB]: Found user \"{0}\", now update information", user.Avatar);
                    if (dbm.Manager.updateUserInfo(user)) return true;
                }
                else if (dbm.Manager.addUserInfo(user))
                {
                    //m_log.InfoFormat("[MONEY DB]: Unable to find user \"{0}\", add it to DB successfully", user.Avatar);
                    return true;
                }
                _logger.LogInformation("[MONEY DB]: WARNNING: TryAddUserInfo: Unable to TryAddUserInfo.");
                return false;
            }
            catch (Exception e)
            {
                _logger.LogError(e.ToString());
                return false;
            }
            finally
            {
                dbm.Release();
            }
        }


        public UserInfo FetchUserInfo(string userID)
        {
            UserInfo userInfo = null;
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                userInfo = dbm.Manager.fetchUserInfo(userID);
                return userInfo;
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "FetchUserInfo() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                userInfo = dbm.Manager.fetchUserInfo(userID);
                return userInfo;
            }
            catch (Exception e)
            {
                _logger.LogError(e, "FetchUserInfo() Exception. Aborting.");
                return null;
            }
            finally
            {
                dbm.Release();
            }
        }


        public int getTransactionNum(string userID, int startTime, int endTime)
        {
            MySQLSuperManager dbm = GetLockedConnection();

            try
            {
                return dbm.Manager.getTransactionNum(userID, startTime, endTime);
            }
            catch (MySqlException e)
            {
                _logger.LogError(e, "getTransactionNum() MySqlException. Retrying.");
                dbm.Manager.Reconnect();
                return dbm.Manager.getTransactionNum(userID, startTime, endTime);
            }
            catch (Exception e)
            {
                _logger.LogError(e, "getTransactionNum() Exception. Aborting.");
                return -1;
            }
            finally
            {
                dbm.Release();
            }
        }
    }
}

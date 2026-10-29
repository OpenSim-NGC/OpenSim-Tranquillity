/*
 * Copyright (c) Contributors, http://opensimulator.org/, http://www.nsl.tuis.ac.jp/
 * See CONTRIBUTORS.TXT for a full list of copyright holders.
 *
 * Redistribution and use in source and binary forms, with or without
 * modification, are permitted provided that the following conditions are met:
 *	 * Redistributions of source code must retain the above copyright
 *	   notice, this list of conditions and the following disclaimer.
 *	 * Redistributions in binary form must reproduce the above copyright
 *	   notice, this list of conditions and the following disclaimer in the
 *	   documentation and/or other materials provided with the distribution.
 *	 * Neither the name of the OpenSim Project nor the
 *	   names of its contributors may be used to endorse or promote products
 *	   derived from this software without specific prior written permission.
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

using System.Net;
using System.Text;
using System.Xml;

using Microsoft.AspNetCore.Mvc;

using Nwc.XmlRpc;

using OpenSim.Framework.Servers.HttpServer;
using OpenSim.Server.MoneyServer.Models;

using Microsoft.Extensions.Logging;

namespace OpenSim.Server.MoneyServer.Controllers;

[ApiController]
[Route("xmlrpc")]
public class MoneyXmlRpcController : ControllerBase
{
    private readonly MoneyClientXmlRpcController _client;
    private readonly MoneyTransactionsXmlRpcController _transactions;
    private readonly MoneyWebXmlRpcController _web;
    private readonly ILogger<MoneyXmlRpcController> _logger;

    public MoneyXmlRpcController(
        IConfiguration configuration,
        ILogger<MoneyXmlRpcController> logger,
        ILoggerFactory loggerFactory,
        IMoneyDBService moneyDBService,
        MoneySessionStore sessionStore)
    {
        if (configuration == null) throw new ArgumentNullException(nameof(configuration));
        if (logger == null) throw new ArgumentNullException(nameof(logger));
        if (loggerFactory == null) throw new ArgumentNullException(nameof(loggerFactory));
        if (moneyDBService == null) throw new ArgumentNullException(nameof(moneyDBService));
        if (sessionStore == null) throw new ArgumentNullException(nameof(sessionStore));

        _logger = logger;

        MoneyXmlRpcSettings settings = MoneyXmlRpcSettings.Load(configuration, logger);

        _client = new MoneyClientXmlRpcController(
            settings,
            moneyDBService,
            sessionStore,
            loggerFactory.CreateLogger<MoneyClientXmlRpcController>());

        _transactions = new MoneyTransactionsXmlRpcController(
            settings,
            moneyDBService,
            sessionStore,
            loggerFactory.CreateLogger<MoneyTransactionsXmlRpcController>());

        _web = new MoneyWebXmlRpcController(
            settings,
            moneyDBService,
            sessionStore,
            loggerFactory.CreateLogger<MoneyWebXmlRpcController>());
    }

    public void RegisterLegacyHandlers(BaseHttpServer httpServer)
    {
        if (httpServer == null) throw new ArgumentNullException(nameof(httpServer));

        httpServer.AddXmlRPCHandler("ClientLogin", _client.HandleClientLogin);
        httpServer.AddXmlRPCHandler("ClientLogout", _client.HandleClientLogout);
        httpServer.AddXmlRPCHandler("GetBalance", _client.HandleGetBalance);
        httpServer.AddXmlRPCHandler("GetTransaction", _client.HandleGetTransaction);

        httpServer.AddXmlRPCHandler("CancelTransfer", _transactions.HandleCancelTransfer);
        httpServer.AddXmlRPCHandler("TransferMoney", _transactions.HandleTransaction);
        httpServer.AddXmlRPCHandler("ForceTransferMoney", _transactions.HandleForceTransaction);
        httpServer.AddXmlRPCHandler("PayMoneyCharge", _transactions.HandlePayMoneyCharge);
        httpServer.AddXmlRPCHandler("AddBankerMoney", _transactions.HandleAddBankerMoney);
        httpServer.AddXmlRPCHandler("SendMoney", _transactions.HandleScriptTransaction);
        httpServer.AddXmlRPCHandler("MoveMoney", _transactions.HandleScriptTransaction);

        httpServer.AddXmlRPCHandler("WebLogin", _web.HandleWebLogin);
        httpServer.AddXmlRPCHandler("WebLogout", _web.HandleWebLogout);
        httpServer.AddXmlRPCHandler("WebGetBalance", _web.HandleWebGetBalance);
        httpServer.AddXmlRPCHandler("WebGetTransaction", _web.HandleWebGetTransaction);
        httpServer.AddXmlRPCHandler("WebGetTransactionNum", _web.HandleWebGetTransactionNum);

        httpServer.AddXmlRPCHandler("preflightBuyLandPrep", _transactions.PreflightBuyLandPrep);
        httpServer.AddXmlRPCHandler("buyLandPrep", _transactions.LandBuyPrep);

        httpServer.AddXmlRPCHandler("getCurrencyQuote", _transactions.GetCurrencyQuote);
        httpServer.AddXmlRPCHandler("buyCurrency", _transactions.BuyCurrency);
    }

    [HttpPost]
    public async Task<IActionResult> Post()
    {
        XmlRpcRequest xmlRpcRequest;
        try
        {
            xmlRpcRequest = await DeserializeXmlRpcRequestAsync(Request.Body);
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: Failed to deserialize XML-RPC request: {0}", e);
            XmlRpcResponse fault = new XmlRpcResponse();
            fault.SetFault(-32700, "Invalid XML-RPC payload");
            string faultXml = SerializeXmlRpcResponse(fault);
            return Content(faultXml, "text/xml", Encoding.UTF8);
        }

        if (xmlRpcRequest == null)
        {
            return BadRequest();
        }

        XmlRpcResponse xmlRpcResponse;
        try
        {
            var remoteClient = BuildRemoteEndPoint();
            xmlRpcResponse = Dispatch(xmlRpcRequest, remoteClient);
        }
        catch (Exception e)
        {
            _logger.LogError("[MONEY RPC]: Unhandled exception while processing XML-RPC request: {0}", e);
            xmlRpcResponse = new XmlRpcResponse();
            xmlRpcResponse.SetFault(-32603, "Server error while processing XML-RPC request");
        }

        string xml = SerializeXmlRpcResponse(xmlRpcResponse);
        return Content(xml, "text/xml", Encoding.UTF8);
    }

    private XmlRpcResponse Dispatch(XmlRpcRequest request, IPEndPoint remoteClient)
    {
        switch (request.MethodName)
        {
            case "ClientLogin":
                return _client.HandleClientLogin(request, remoteClient);
            case "ClientLogout":
                return _client.HandleClientLogout(request, remoteClient);
            case "GetBalance":
                return _client.HandleGetBalance(request, remoteClient);
            case "GetTransaction":
                return _client.HandleGetTransaction(request, remoteClient);
            case "CancelTransfer":
                return _transactions.HandleCancelTransfer(request, remoteClient);
            case "TransferMoney":
                return _transactions.HandleTransaction(request, remoteClient);
            case "ForceTransferMoney":
                return _transactions.HandleForceTransaction(request, remoteClient);
            case "PayMoneyCharge":
                return _transactions.HandlePayMoneyCharge(request, remoteClient);
            case "AddBankerMoney":
                return _transactions.HandleAddBankerMoney(request, remoteClient);
            case "SendMoney":
            case "MoveMoney":
                return _transactions.HandleScriptTransaction(request, remoteClient);
            case "WebLogin":
                return _web.HandleWebLogin(request, remoteClient);
            case "WebLogout":
                return _web.HandleWebLogout(request, remoteClient);
            case "WebGetBalance":
                return _web.HandleWebGetBalance(request, remoteClient);
            case "WebGetTransaction":
                return _web.HandleWebGetTransaction(request, remoteClient);
            case "WebGetTransactionNum":
                return _web.HandleWebGetTransactionNum(request, remoteClient);
            case "preflightBuyLandPrep":
                return _transactions.PreflightBuyLandPrep(request, remoteClient);
            case "buyLandPrep":
                return _transactions.LandBuyPrep(request, remoteClient);
            case "getCurrencyQuote":
                return _transactions.GetCurrencyQuote(request, remoteClient);
            case "buyCurrency":
                return _transactions.BuyCurrency(request, remoteClient);
            default:
                var response = new XmlRpcResponse();
                response.SetFault(XmlRpcErrorCodes.SERVER_ERROR_METHOD,
                    string.Format("Requested method [{0}] not found", request.MethodName));
                return response;
        }
    }

    private static async Task<XmlRpcRequest> DeserializeXmlRpcRequestAsync(Stream stream)
    {
        using StreamReader reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
        var deserializer = new XmlRpcRequestDeserializer();
        return (XmlRpcRequest)await Task.Run(() => deserializer.Deserialize(reader));
    }

    private static string SerializeXmlRpcResponse(XmlRpcResponse response)
    {
        using MemoryStream output = new MemoryStream(64 * 1024);
        using (XmlTextWriter writer = new XmlTextWriter(output, new UTF8Encoding(false)))
        {
            writer.Formatting = Formatting.None;
            var serializer = new XmlRpcResponseSerializer();
            serializer.Serialize(writer, response);
            writer.Flush();
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    private IPEndPoint BuildRemoteEndPoint()
    {
        string xff = Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(xff))
        {
            string first = xff.Split(',')[0].Trim();
            if (IPAddress.TryParse(first, out var forwardedIp))
            {
                return new IPEndPoint(forwardedIp, HttpContext.Connection.RemotePort);
            }
        }

        var ip = HttpContext.Connection.RemoteIpAddress ?? IPAddress.Loopback;
        return new IPEndPoint(ip, HttpContext.Connection.RemotePort);
    }
}

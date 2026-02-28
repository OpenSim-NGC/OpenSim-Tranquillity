using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace OpenSim.Server.MoneyServer.Controllers;

public sealed class MoneyXmlRpcSettings
{
    public int DefaultBalance { get; }
    public bool ForceTransfer { get; }
    public string BankerAvatar { get; }

    public bool ScriptSendMoney { get; }
    public string ScriptAccessKey { get; }
    public string ScriptIPAddress { get; }

    public bool HgEnable { get; }
    public bool GstEnable { get; }
    public int HgDefaultBalance { get; }
    public int GstDefaultBalance { get; }

    public string BalanceMessageLandSale { get; }
    public string BalanceMessageRcvLandSale { get; }
    public string BalanceMessageSendGift { get; }
    public string BalanceMessageReceiveGift { get; }
    public string BalanceMessagePayCharge { get; }
    public string BalanceMessageBuyObject { get; }
    public string BalanceMessageSellObject { get; }
    public string BalanceMessageGetMoney { get; }
    public string BalanceMessageBuyMoney { get; }
    public string BalanceMessageRollBack { get; }
    public string BalanceMessageSendMoney { get; }
    public string BalanceMessageReceiveMoney { get; }

    public bool EnableAmountZero { get; }

    private MoneyXmlRpcSettings(
        int defaultBalance,
        bool forceTransfer,
        string bankerAvatar,
        bool scriptSendMoney,
        string scriptAccessKey,
        string scriptIPAddress,
        bool hgEnable,
        bool gstEnable,
        int hgDefaultBalance,
        int gstDefaultBalance,
        string balanceMessageLandSale,
        string balanceMessageRcvLandSale,
        string balanceMessageSendGift,
        string balanceMessageReceiveGift,
        string balanceMessagePayCharge,
        string balanceMessageBuyObject,
        string balanceMessageSellObject,
        string balanceMessageGetMoney,
        string balanceMessageBuyMoney,
        string balanceMessageRollBack,
        string balanceMessageSendMoney,
        string balanceMessageReceiveMoney,
        bool enableAmountZero)
    {
        DefaultBalance = defaultBalance;
        ForceTransfer = forceTransfer;
        BankerAvatar = bankerAvatar;
        ScriptSendMoney = scriptSendMoney;
        ScriptAccessKey = scriptAccessKey;
        ScriptIPAddress = scriptIPAddress;
        HgEnable = hgEnable;
        GstEnable = gstEnable;
        HgDefaultBalance = hgDefaultBalance;
        GstDefaultBalance = gstDefaultBalance;
        BalanceMessageLandSale = balanceMessageLandSale;
        BalanceMessageRcvLandSale = balanceMessageRcvLandSale;
        BalanceMessageSendGift = balanceMessageSendGift;
        BalanceMessageReceiveGift = balanceMessageReceiveGift;
        BalanceMessagePayCharge = balanceMessagePayCharge;
        BalanceMessageBuyObject = balanceMessageBuyObject;
        BalanceMessageSellObject = balanceMessageSellObject;
        BalanceMessageGetMoney = balanceMessageGetMoney;
        BalanceMessageBuyMoney = balanceMessageBuyMoney;
        BalanceMessageRollBack = balanceMessageRollBack;
        BalanceMessageSendMoney = balanceMessageSendMoney;
        BalanceMessageReceiveMoney = balanceMessageReceiveMoney;
        EnableAmountZero = enableAmountZero;
    }

    public static MoneyXmlRpcSettings Load(IConfiguration configuration, ILogger logger)
    {
        int defaultBalance = 1000;
        bool forceTransfer = false;
        string bankerAvatar = "";

        bool scriptSendMoney = false;
        string scriptAccessKey = "";
        string scriptIPAddress = "127.0.0.1";

        bool hgEnable = false;
        bool gstEnable = false;
        int hgDefaultBalance = 0;
        int gstDefaultBalance = 0;

        string balanceMessageLandSale = "Paid the Money L${0} for Land.";
        string balanceMessageRcvLandSale = "";
        string balanceMessageSendGift = "Sent Gift L${0} to {1}.";
        string balanceMessageReceiveGift = "Received Gift L${0} from {1}.";
        string balanceMessagePayCharge = "";
        string balanceMessageBuyObject = "Bought the Object {2} from {1} by L${0}.";
        string balanceMessageSellObject = "{1} bought the Object {2} by L${0}.";
        string balanceMessageGetMoney = "Got the Money L${0} from {1}.";
        string balanceMessageBuyMoney = "Bought the Money L${0}.";
        string balanceMessageRollBack = "RollBack the Transaction: L${0} from/to {1}.";
        string balanceMessageSendMoney = "Paid the Money L${0} to {1}.";
        string balanceMessageReceiveMoney = "Received L${0} from {1}.";

        bool enableAmountZero = false;

        var serverConfig = configuration.GetSection("MoneyServer");
        if (serverConfig.Exists())
        {
            defaultBalance = serverConfig.GetValue<int>("DefaultBalance", defaultBalance);
            forceTransfer = serverConfig.GetValue<bool>("EnableForceTransfer", forceTransfer);
            bankerAvatar = serverConfig.GetValue<string>("BankerAvatar", bankerAvatar).ToLower();

            enableAmountZero = serverConfig.GetValue<bool>("EnableAmountZero", enableAmountZero);
            scriptSendMoney = serverConfig.GetValue<bool>("EnableScriptSendMoney", scriptSendMoney);
            scriptAccessKey = serverConfig.GetValue<string>("MoneyScriptAccessKey", scriptAccessKey);
            scriptIPAddress = serverConfig.GetValue<string>("MoneyScriptIPaddress", scriptIPAddress);

            hgEnable = serverConfig.GetValue<bool>("EnableHGAvatar", hgEnable);
            gstEnable = serverConfig.GetValue<bool>("EnableGuestAvatar", gstEnable);
            hgDefaultBalance = serverConfig.GetValue<int>("HGAvatarDefaultBalance", hgDefaultBalance);
            gstDefaultBalance = serverConfig.GetValue<int>("GuestAvatarDefaultBalance", gstDefaultBalance);

            balanceMessageLandSale = serverConfig.GetValue<string>("BalanceMessageLandSale", balanceMessageLandSale);
            balanceMessageRcvLandSale = serverConfig.GetValue<string>("BalanceMessageRcvLandSale", balanceMessageRcvLandSale);
            balanceMessageSendGift = serverConfig.GetValue<string>("BalanceMessageSendGift", balanceMessageSendGift);
            balanceMessageReceiveGift = serverConfig.GetValue<string>("BalanceMessageReceiveGift", balanceMessageReceiveGift);
            balanceMessagePayCharge = serverConfig.GetValue<string>("BalanceMessagePayCharge", balanceMessagePayCharge);
            balanceMessageBuyObject = serverConfig.GetValue<string>("BalanceMessageBuyObject", balanceMessageBuyObject);
            balanceMessageSellObject = serverConfig.GetValue<string>("BalanceMessageSellObject", balanceMessageSellObject);
            balanceMessageGetMoney = serverConfig.GetValue<string>("BalanceMessageGetMoney", balanceMessageGetMoney);
            balanceMessageBuyMoney = serverConfig.GetValue<string>("BalanceMessageBuyMoney", balanceMessageBuyMoney);
            balanceMessageRollBack = serverConfig.GetValue<string>("BalanceMessageRollBack", balanceMessageRollBack);
            balanceMessageSendMoney = serverConfig.GetValue<string>("BalanceMessageSendMoney", balanceMessageSendMoney);
            balanceMessageReceiveMoney = serverConfig.GetValue<string>("BalanceMessageReceiveMoney", balanceMessageReceiveMoney);
        }
        else
        {
            logger.LogWarning("[MONEY RPC]: LoadConfiguration: Can't find [MoneyServer] section in config file.");
        }

        return new MoneyXmlRpcSettings(
            defaultBalance,
            forceTransfer,
            bankerAvatar,
            scriptSendMoney,
            scriptAccessKey,
            scriptIPAddress,
            hgEnable,
            gstEnable,
            hgDefaultBalance,
            gstDefaultBalance,
            balanceMessageLandSale,
            balanceMessageRcvLandSale,
            balanceMessageSendGift,
            balanceMessageReceiveGift,
            balanceMessagePayCharge,
            balanceMessageBuyObject,
            balanceMessageSellObject,
            balanceMessageGetMoney,
            balanceMessageBuyMoney,
            balanceMessageRollBack,
            balanceMessageSendMoney,
            balanceMessageReceiveMoney,
            enableAmountZero);
    }
}

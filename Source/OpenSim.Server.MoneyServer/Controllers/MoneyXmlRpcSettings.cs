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
            bankerAvatar = ReadIniString(serverConfig, "BankerAvatar", bankerAvatar).ToLower();

            enableAmountZero = serverConfig.GetValue<bool>("EnableAmountZero", enableAmountZero);
            scriptSendMoney = serverConfig.GetValue<bool>("EnableScriptSendMoney", scriptSendMoney);
            scriptAccessKey = ReadIniString(serverConfig, "MoneyScriptAccessKey", scriptAccessKey);
            scriptIPAddress = ReadIniString(serverConfig, "MoneyScriptIPaddress", scriptIPAddress);

            hgEnable = serverConfig.GetValue<bool>("EnableHGAvatar", hgEnable);
            gstEnable = serverConfig.GetValue<bool>("EnableGuestAvatar", gstEnable);
            hgDefaultBalance = serverConfig.GetValue<int>("HGAvatarDefaultBalance", hgDefaultBalance);
            gstDefaultBalance = serverConfig.GetValue<int>("GuestAvatarDefaultBalance", gstDefaultBalance);

            balanceMessageLandSale = ReadIniString(serverConfig, "BalanceMessageLandSale", balanceMessageLandSale);
            balanceMessageRcvLandSale = ReadIniString(serverConfig, "BalanceMessageRcvLandSale", balanceMessageRcvLandSale);
            balanceMessageSendGift = ReadIniString(serverConfig, "BalanceMessageSendGift", balanceMessageSendGift);
            balanceMessageReceiveGift = ReadIniString(serverConfig, "BalanceMessageReceiveGift", balanceMessageReceiveGift);
            balanceMessagePayCharge = ReadIniString(serverConfig, "BalanceMessagePayCharge", balanceMessagePayCharge);
            balanceMessageBuyObject = ReadIniString(serverConfig, "BalanceMessageBuyObject", balanceMessageBuyObject);
            balanceMessageSellObject = ReadIniString(serverConfig, "BalanceMessageSellObject", balanceMessageSellObject);
            balanceMessageGetMoney = ReadIniString(serverConfig, "BalanceMessageGetMoney", balanceMessageGetMoney);
            balanceMessageBuyMoney = ReadIniString(serverConfig, "BalanceMessageBuyMoney", balanceMessageBuyMoney);
            balanceMessageRollBack = ReadIniString(serverConfig, "BalanceMessageRollBack", balanceMessageRollBack);
            balanceMessageSendMoney = ReadIniString(serverConfig, "BalanceMessageSendMoney", balanceMessageSendMoney);
            balanceMessageReceiveMoney = ReadIniString(serverConfig, "BalanceMessageReceiveMoney", balanceMessageReceiveMoney);
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

    // The ini files use legacy Nini formatting: string values may be wrapped in
    // double quotes and may carry a trailing ';' inline comment. The Microsoft
    // ini provider keeps both as part of the value, so strip them here.
    private static string ReadIniString(IConfiguration config, string key, string fallback)
    {
        string raw = config.GetValue<string>(key, fallback);
        if (raw is null)
            return fallback;

        string value = raw.Trim();
        if (value.Length == 0)
            return value;

        if (value[0] == '"')
        {
            int end = value.IndexOf('"', 1);
            return end > 0 ? value.Substring(1, end - 1) : value.Substring(1);
        }

        int comment = value.IndexOfAny(new[] { ';', '#' });
        if (comment >= 0)
            value = value.Substring(0, comment).TrimEnd();

        return value;
    }
}

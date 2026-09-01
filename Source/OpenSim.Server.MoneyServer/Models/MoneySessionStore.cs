namespace OpenSim.Server.MoneyServer.Models;

public class MoneySessionStore
{
    public Dictionary<string, string> SessionDic { get; } = new Dictionary<string, string>();
    public Dictionary<string, string> SecureSessionDic { get; } = new Dictionary<string, string>();
    public Dictionary<string, string> WebSessionDic { get; } = new Dictionary<string, string>();
}

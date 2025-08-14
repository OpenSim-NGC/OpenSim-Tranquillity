using System.Xml;

namespace OpenSim.Framework;

public delegate void CommandDelegate(string module, string[] cmd);

public interface ICommands
{
    void FromXml(XmlElement root, CommandDelegate fn);

    XmlElement GetXml(XmlDocument doc);
    
    /// <summary>
    /// Get help for the given help string
    /// </summary>
    /// <param name="cmd">Parsed parts of the help string.  If empty then general help is returned.</param>
    /// <returns></returns>
    List<string> GetHelp(string[] cmd);

    /// <summary>
    /// Add a command to those which can be invoked from the console.
    /// </summary>
    /// <param name="module"></param>
    /// <param name="command"></param>
    /// <param name="help"></param>
    /// <param name="longhelp"></param>
    /// <param name="fn"></param>
    void AddCommand(string module, bool shared, string command, string help, string longhelp, CommandDelegate fn);

    /// <summary>
    /// Add a command to those which can be invoked from the console.
    /// </summary>
    /// <param name="module"></param>
    /// <param name="command"></param>
    /// <param name="help"></param>
    /// <param name="longhelp"></param>
    /// <param name="descriptivehelp"></param>
    /// <param name="fn"></param>
    void AddCommand(string module, bool shared, string command,
        string help, string longhelp, string descriptivehelp,
        CommandDelegate fn);

    /// <summary>
    /// Has the given command already been registered?
    /// </summary>
    /// <returns></returns>
    /// <param name="command">Command.</param>
    bool HasCommand(string command);

    string[] FindNextOption(string[] command, bool term);

    string[] Resolve(string[] command);
}

namespace OpenSim.Framework;

public delegate void CommandDelegate (IScene scene, string [] cmd);

public interface ICommands
{
    /// <summary>
    ///     Get help for the given help string
    /// </summary>
    /// <param name="cmd">Parsed parts of the help string.  If empty then general help is returned.</param>
    /// <returns></returns>
    List<string> GetHelp (string [] cmd);

    /// <summary>
    ///     Add a command to those which can be invoked from the console.
    /// </summary>
    /// <param name="command">The string that will make the command execute</param>
    /// <param name="commandHelp">The message that will show the user how to use the command</param>
    /// <param name="infomessage">Any information about how the command works or what it does</param>
    /// <param name="fn"></param>
    /// <param name="requiresAScene">Whether this command requires a scene to be fired</param>
    /// <param name="fireOnceForAllScenes">Whether this command will only be executed once if there is no current scene</param>
    void AddCommand (string command, string commandHelp, string infomessage, CommandDelegate fn, bool requiresAScene, bool fireOnceForAllScenes);

    bool ContainsCommand (string command);
    string [] FindNextOption (string [] cmd);
    string [] Resolve (string [] cmd);
}
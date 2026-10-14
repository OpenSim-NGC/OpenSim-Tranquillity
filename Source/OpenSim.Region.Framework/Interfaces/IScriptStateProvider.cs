using OpenMetaverse;

namespace OpenSim.Region.Framework.Interfaces;

/// <summary>Optional saved-state ownership lookup for a script engine.</summary>
public interface IScriptStateProvider
{
    /// <summary>
    /// Whether this engine has saved state for this item and asset, without consuming it.
    /// An unreadable state store must retain ownership and report the error, not send the script to another engine.
    /// </summary>
    bool HasSavedScriptState(UUID itemID, UUID assetID);
}

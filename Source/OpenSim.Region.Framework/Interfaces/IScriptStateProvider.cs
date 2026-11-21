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

    /// <summary>
    /// Forget the state this engine saved for the item: called when the item's engine changes, so the script starts fresh
    /// wherever it runs next. The script's own instance in this engine, if any, has already been asked to unload.
    /// </summary>
    void DiscardSavedScriptState(UUID itemID) { }
}

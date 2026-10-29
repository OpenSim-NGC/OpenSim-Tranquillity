/*
 * Phlox Script Engine Integration
 */

using OpenMetaverse;
using OpenSim.Region.Framework.Scenes;

namespace Phlox.ScriptEngine
{
    internal class PhloxLoadRequest
    {
        public uint LocalID;
        public UUID ItemID;
        public string ScriptText;
        /// <summary>The asset <see cref="ScriptText"/> came from: the item's asset when the load was posted, the moment the
        /// region read that text. A later save gives the item a new asset; this load still compiles and caches as its own.</summary>
        public UUID AssetId;
        public int StartParam;
        public bool PostOnRez;
        public int StateSource;
        public SceneObjectPart Prim;
        /// <summary>The item's load generation when this request was processed; a compile that finishes
        /// after a newer load or an unload of the same item is stale and does not start.</summary>
        public long Generation;
        /// <summary>Posting order of this item's loads, so GetScriptErrors waits for the one just saved.</summary>
        public long Serial;
    }

    internal class PhloxUnloadRequest
    {
        public uint LocalID;
        public UUID ItemID;
    }
}

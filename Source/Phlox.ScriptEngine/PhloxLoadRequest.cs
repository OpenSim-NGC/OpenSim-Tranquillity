/*
 * Legion Grid — Phlox Script Engine Integration
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
        public int StartParam;
        public bool PostOnRez;
        public int StateSource;
        public SceneObjectPart Prim;
    }

    internal class PhloxUnloadRequest
    {
        public uint LocalID;
        public UUID ItemID;
    }
}

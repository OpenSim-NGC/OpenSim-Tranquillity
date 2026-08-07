using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Specifies the reason a script got unloaded
    /// </summary>
    public enum ScriptUnloadReason
    {
        /// <summary>
        /// THe script was actually removed from the scene and unloaded
        /// </summary>
        Unloaded = 1,

        /// <summary>
        /// The script was globally disabled
        /// </summary>
        GloballyDisabled = 2,

        /// <summary>
        /// The script was locally disabled
        /// </summary>
        LocallyDisabled = 3
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Handles the translation of a system call (syscall instruction) to an API call to
    /// the system API
    /// </summary>
    public interface ISyscallShim
    {
        /// <summary>
        /// Calls the function with the given id
        /// </summary>
        /// <param name="funcid"></param>
        void Call(int funcid);

        /// <summary>
        /// Sets the event flags on an object in which the script runs
        /// </summary>
        void SetScriptEventFlags();

        /// <summary>
        /// Shouts an error on the debug channel
        /// </summary>
        /// <param name="errorText"></param>
        void ShoutError(string errorText);

        /// <summary>
        /// Called by the execution environment when the script is reset
        /// </summary>
        void OnScriptReset();

        /// <summary>
        /// Called by the execution environment when the state changes
        /// </summary>
        void OnStateChange();

        /// <summary>
        /// Called by the execution environment when the script unloads
        /// </summary>
        /// <param name="reason">The reason for the unload</param>
        /// <param name="localFlag">If the reason is ScriptUnloadReason.LocallyDisabled, this parameter specifies the exact type</param>
        void OnScriptUnloaded(ScriptUnloadReason reason, VM.RuntimeState.LocalDisableFlag localFlag);

        /// <summary>
        /// Adds the specified amount of milliseconds to this scripts execution time
        /// </summary>
        /// <param name="time">The amount of runtime to add</param>
        void AddExecutionTime(double ms);

        /// <summary>
        /// Returns the calculated average execution timeslice from an unspecified number of samples (16 actually).
        /// </summary>
        /// <returns></returns>
        float GetAverageScriptTime();

        /// <summary>
        /// Called when a script has been newly injected into the runtime environment
        /// whether thrrough a rez with state information, a prim crossing, etc
        /// </summary>
        void OnScriptInjected(bool fromCrossing);

        /// <summary>
        /// Called after an avatar has completed crossing with a group
        /// </summary>
        void OnGroupCrossedAvatarReady(OpenMetaverse.UUID avatarId);
    }
}

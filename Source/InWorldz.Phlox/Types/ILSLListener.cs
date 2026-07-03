using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Interface for an object that listens for compiler warnings and errors
    /// </summary>
    public interface ILSLListener
    {
        /// <summary>
        /// Report an informational message, not considered to be an error
        /// </summary>
        /// <param name="message">The message to report</param>
        void Info(string message);

        /// <summary>
        /// Report an error 
        /// </summary>
        /// <param name="message">The error message</param>
        void Error(string message);

        /// <summary>
        /// Whether or not this listener has had any errors reported
        /// </summary>
        /// <returns>True if there are errors, false if not</returns>
        bool HasErrors();

        /// <summary>
        /// Called when compilation is finished whether there were errors or not
        /// </summary>
        void CompilationFinished();
    }
}

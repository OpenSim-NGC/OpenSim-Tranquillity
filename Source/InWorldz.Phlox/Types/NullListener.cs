/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Types/NullListener.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    public class NullListener : ILSLListener
    {
        bool _hasErrors = false;

        #region ILSLListener Members
        
        public void Info(string message)
        {
        }

        public void Error(string message)
        {
            _hasErrors = true;
        }

        public bool HasErrors()
        {
            return _hasErrors;
        }

        public void CompilationFinished()
        {
        }

        #endregion
    }
}

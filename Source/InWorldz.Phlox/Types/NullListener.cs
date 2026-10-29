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

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    public class DefaultLSLListener : ILSLListener
    {

        #region ILSLListener Members
        bool _hasErrors = false;

        public void Info(string message)
        {
            Console.WriteLine("info: " + message);
        }

        public void Error(string message)
        {
            _hasErrors = true;
            Console.Error.WriteLine("error: " + message);
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

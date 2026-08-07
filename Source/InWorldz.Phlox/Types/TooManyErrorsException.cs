using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// Exception thrown when too many errors have been reported during parsing.
    /// This prevents an infinite loop situation I have seen in the parser
    /// </summary>
    public class TooManyErrorsException : Exception
    {
        public TooManyErrorsException(string message, Exception innerException)
            : base(message, innerException)
        {

        }
    }
}

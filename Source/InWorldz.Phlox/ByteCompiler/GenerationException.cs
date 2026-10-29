using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.ByteCompiler
{
    public class GenerationException : Exception
    {
        public GenerationException() : base()
        {

        }

        public GenerationException(string message)
            : base(message)
        {

        }
    }
}

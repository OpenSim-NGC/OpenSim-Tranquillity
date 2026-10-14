/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/ByteCompiler/GenerationException.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

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

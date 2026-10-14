/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Types/CheckException.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Types
{
    public class CheckException : Exception
    {
        public CheckException()
            : base()
        {

        }

        public CheckException(string message)
            : base(message)
        {

        }
    }
}

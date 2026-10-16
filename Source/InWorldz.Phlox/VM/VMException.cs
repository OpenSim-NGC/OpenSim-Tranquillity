/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/VM/VMException.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.VM
{
    public class VMException : Exception
    {
        public VMException() : base()
        {

        }

        public VMException(string message)
            : base(message)
        {

        }
    }
}

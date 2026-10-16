/*
 * From HalcyonGrid/phlox (Source/Halcyon.Phlox/Serialization/SerializationException.cs, commit e805a56).
 * Licence: Apache License 2.0, see ThirdPartyLicenses/Phlox.txt.
 * Modified 2026 by Legion Builds.
 */

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.Serialization
{
    public class SerializationException : Exception
    {
        public SerializationException()
            : base()
        {

        }

        public SerializationException(string message)
            : base(message)
        {
        }
    }
}

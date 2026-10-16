/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;

namespace InWorldz.Phlox.Util
{
    /// <summary>
    /// Float to integer as LSL does it. SL wiki: the Typecast page (https://wiki.secondlife.com/wiki/Typecast) says a
    /// cast from float to integer just drops the fractional part; the llFloor page
    /// (https://wiki.secondlife.com/wiki/LlFloor) says the result is -2147483648 (0x80000000) when the arithmetic
    /// result lies outside the valid integer limits (-2147483648 to 2147483647 inclusive). NaN gives -2147483648 too,
    /// as SL and Halcyon (.NET Framework x64 cvttss2si) do. A plain C# cast
    /// no longer does this: from .NET 9 on x64 it saturates (NaN 0, +inf 2147483647).
    /// </summary>
    public static class LslConvert
    {
        public static int FloatToInteger(double f)
        {
            if (double.IsNaN(f) || f >= 2147483648.0 || f <= -2147483649.0)
                return int.MinValue;
            return (int)f;
        }
    }
}

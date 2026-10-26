/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using InWorldz.Phlox.Glue;

namespace Phlox.ScriptEngine
{
    /// <summary>Stub: the interface is filled in by the next commit.</summary>
    public interface ILSLSystemAPI : ISyscallDeferralAdvisor
    {
        float llSin(float f);
        int llStringLength(string str);
        int llGetLocalTime();
        string llGetKey();
    }
}

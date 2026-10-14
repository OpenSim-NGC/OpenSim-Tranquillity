/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// A SLua DetectedEvent passed to LLEvents:on handlers — a lightweight wrapper over a 0-based
    /// detection index. Its methods (getKey/getName/getPos/...) map to the existing llDetected*
    /// syscalls, read while the event's detection context is live (handlers run synchronously during
    /// dispatch). Transient (built during dispatch, never stored across a yield), so no serialization.
    /// </summary>
    public class LuaDetected
    {
        public int Index;
        public LuaDetected() { }
        public LuaDetected(int i) { Index = i; }
    }
}

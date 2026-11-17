/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System.Collections.Generic;
using InWorldz.Phlox.VM;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// Seam B: what LSLSystemAPI needs from the script it serves, so that an engine other than Phlox's interpreter can
    /// host the API. Phlox implements it over its Interpreter. An engine without an Interpreter keeps the same
    /// per-script data in a RuntimeState (the API reads and writes its miscellaneous attributes, enable flags, run
    /// time and experience grant) and lists the events its script handles in a state.
    /// </summary>
    public interface IScriptRuntimeAccess
    {
        /// <summary>The script's runtime state, which the API reads and writes.</summary>
        RuntimeState ScriptState { get; }

        /// <summary>The event types (SupportedEventList.Events values) the script has a handler for in an LSL state.</summary>
        IEnumerable<int> EventTypesHandledIn(int lslState);
    }
}

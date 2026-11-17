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
    /// <summary>Seam B for Phlox: the script's access object is a thin cover over its interpreter.</summary>
    internal sealed class InterpreterAccess : IScriptRuntimeAccess
    {
        public Interpreter Interpreter { get; }

        public InterpreterAccess(Interpreter interpreter) { Interpreter = interpreter; }

        public RuntimeState ScriptState => Interpreter.ScriptState;

        public IEnumerable<int> EventTypesHandledIn(int lslState)
        {
            foreach (var evt in Interpreter.Script.StateEvents[lslState])
                yield return evt.EventType;
        }
    }
}

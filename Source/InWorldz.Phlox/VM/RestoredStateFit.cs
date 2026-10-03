/* Copyright (c) 2026 Legion Builds
 *
 * This Source Code Form is subject to the terms of the Mozilla Public
 * License, v. 2.0. If a copy of the MPL was not distributed with this
 * file, You can obtain one at https://mozilla.org/MPL/2.0/.
 */

using System;
using System.Collections.Generic;

using InWorldz.Phlox.Serialization;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.Util;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// Checks a saved state that came from outside this simulator (inside an object: from inventory, another grid, another
    /// resident) against the compiled script it is loaded for. Every field of such a state is treated as hostile. A state
    /// that does not fit is refused, and the script starts fresh; what cannot be checked without the script's declarations
    /// (a global holding another type than the script declared, an operand of the wrong type) is left to the interpreter,
    /// whose errors stop that one script the way any runtime error does.
    /// </summary>
    public static class RestoredStateFit
    {
        /// <summary>
        /// The most slots a call frame may have: a frame's locals live in the script's memory, at least 4 bytes each, so
        /// no frame of a script within <see cref="MemoryInfo.MAX_MEMORY"/> has more.
        /// </summary>
        public const int MaxFrameSlots = MemoryInfo.MAX_MEMORY / 4;

        /// <summary>How deep values (lists, tables, closures' cells) are walked; deeper is refused.</summary>
        private const int MaxValueDepth = 64;

        /// <summary>
        /// Before the state is built: <see cref="StackFrame"/>'s constructor allocates a frame's locals from two integers of
        /// its <see cref="FunctionInfo"/>, so each saved frame must describe exactly the locals it carries, within
        /// <see cref="MaxFrameSlots"/>. Null when the state may be built, otherwise why not.
        /// </summary>
        public static string CheckSerialized(SerializedRuntimeState s)
        {
            if (s == null) return "no state";
            if (s.Calls == null) return null;
            foreach (SerializedStackFrame f in s.Calls)
            {
                if (f == null) return "a call frame is empty";
                string bad = CheckFunctionInfo(f.FunctionInfo, int.MaxValue);
                if (bad != null) return bad;
                int slots = f.FunctionInfo.NumberOfArguments + f.FunctionInfo.NumberOfLocals;
                if (slots != (f.Locals?.Length ?? 0)) return "a call frame's locals do not match its function";
            }
            return null;
        }

        /// <summary>
        /// After the state is built for <paramref name="script"/>: the state index, the globals, the execution position,
        /// the queued events and the memory in use must fit the script. Memory in use is recomputed from the values the
        /// state holds (never taken from it) and must be within <see cref="MemoryInfo.MAX_MEMORY"/>; the event queue is held
        /// to what <see cref="RuntimeState.QueueEvent"/> accepts. Null when it fits, otherwise why not.
        /// </summary>
        public static string Check(RuntimeState st, CompiledScript script)
        {
            int codeLength = script.ByteCode?.Length ?? 0;
            int states = script.StateEvents?.Length ?? 0;

            if (st.LSLState < 0 || st.LSLState >= states) return $"state {st.LSLState} is not one of the script's {states}";
            if (st.Globals == null || st.Globals.Length != script.NumGlobals)
                return $"{st.Globals?.Length ?? 0} globals where the script has {script.NumGlobals}";
            if (!Enum.IsDefined(typeof(RuntimeState.Status), st.RunState)) return "an unknown run state";

            int frames = st.Calls?.Count ?? 0;
            int operands = st.Operands?.Count ?? 0;
            if (st.IsMidEvent && frames == 0) return "a running event with no call frame";
            if (st.RunState == RuntimeState.Status.Waiting && frames > 0) return "call frames on a waiting script";
            if (operands > Interpreter.MaxOperands) return $"{operands} values on its stack, above the limit of {Interpreter.MaxOperands}";
            if (frames > 0)
            {
                if (st.IP < 0 || st.IP >= codeLength) return "the execution position is outside the script's code";
                foreach (StackFrame f in st.Calls)
                {
                    string bad = CheckFunctionInfo(f.FunctionInfo, codeLength);
                    if (bad != null) return bad;
                    if (f.ReturnAddress < 0 || f.ReturnAddress > codeLength) return "a return address is outside the script's code";
                    if (f.Locals == null) return "a call frame has no locals";
                }
                string badCall = CheckValueCalls(st.Calls.ToArray(), script.ByteCode, operands);
                if (badCall != null) return badCall;
            }
            if (st.RunningEvent != null && !Enum.IsDefined(typeof(SupportedEventList.Events), st.RunningEvent.EventType))
                return "the running event is of an unknown type";

            string badEvent = CheckQueue(st, script, states);
            if (badEvent != null) return badEvent;

            // Every value the script can reach: functions it can call must be in its code, nesting is bounded.
            var walk = new ValueWalk(codeLength);
            walk.All(st.Globals);
            if (st.Operands != null) walk.All(st.Operands);
            if (st.Calls != null)
                foreach (StackFrame f in st.Calls)
                {
                    walk.All(f.Locals);
                    if (f.Closure != null) walk.All(new object[] { f.Closure });
                }
            if (st.RunningEvent?.Args != null) walk.All(st.RunningEvent.Args);
            if (st.EventQueue != null) foreach (PostedEvent e in st.EventQueue) walk.All(e.Args);
            if (walk.Problem != null) return walk.Problem;

            int used = script.CalcBaseMemorySize() + SizeOf(st.Globals);
            if (st.Calls != null)
                foreach (StackFrame f in st.Calls) used += SizeOf(f.Locals) + StackFrame.MemSize;
            if (used > MemoryInfo.MAX_MEMORY) return $"it uses {used} bytes of memory, above the limit of {MemoryInfo.MAX_MEMORY}";
            st.MemInfo = new MemoryInfo { MemoryUsed = used };
            st.PeakMemoryUsed = Math.Clamp(st.PeakMemoryUsed, used, MemoryInfo.MAX_MEMORY);
            if (float.IsNaN(st.OtherRuntime) || float.IsInfinity(st.OtherRuntime) || st.OtherRuntime < 0) st.OtherRuntime = 0;
            return null;
        }

        private static string CheckFunctionInfo(FunctionInfo fn, int codeLength)
        {
            if (fn == null) return "a call frame has no function";
            if (fn.NumberOfArguments < 0 || fn.NumberOfLocals < 0 ||
                (long)fn.NumberOfArguments + fn.NumberOfLocals > MaxFrameSlots)
                return "a function's argument or local count is out of range";
            if (codeLength != int.MaxValue && (fn.Address < 0 || fn.Address >= codeLength))
                return "a function's address is outside the script's code";
            return null;
        }

        /// <summary>
        /// A frame's Wanted and OperandBase drive how many values its return pushes or pops (Op_Ret). Only Op_CallV sets
        /// them, from the "callv argc, wanted" instruction the frame returns past and from the operand stack's depth at
        /// that call; every other frame keeps -1 and 0. So a value-call frame must return just past a callv asking for
        /// exactly its Wanted, and the operand bases of the value calls, bottom frame first, never go down and never pass
        /// the operand stack. <paramref name="topFirst"/> is the call stack as <see cref="Stack{T}.ToArray"/> gives it.
        /// </summary>
        private static string CheckValueCalls(StackFrame[] topFirst, byte[] code, int operands)
        {
            int floor = 0;
            for (int i = topFirst.Length - 1; i >= 0; i--)
            {
                StackFrame f = topFirst[i];
                if (f.Wanted == -1)
                {
                    if (f.OperandBase != 0) return "a named call frame carries an operand base";
                    continue;
                }
                if (f.Wanted < -1) return "a call frame wants a negative number of results";
                int site = f.ReturnAddress - 9;                          // opcode byte, then argc and wanted, 4 bytes each
                if (site < 0 || code[site] != (byte)OpCode.callv || Util.Encoding.GetInt(code, f.ReturnAddress - 4) != f.Wanted)
                    return "a value call's wanted count is not what its call site asks for";
                if (f.OperandBase < floor || f.OperandBase > operands) return "a value call's operand base does not fit the operand stack";
                floor = f.OperandBase;
            }
            return null;
        }

        /// <summary>
        /// Queued events must be of known types, change to one of the script's states, and carry as many arguments as the
        /// script's handler for them takes. The queue keeps what <see cref="RuntimeState.QueueEvent"/> would have let in.
        /// </summary>
        private static string CheckQueue(RuntimeState st, CompiledScript script, int states)
        {
            if (st.EventQueue == null) return null;
            var kept = new C5.LinkedList<PostedEvent>();
            var limit = new RuntimeState { EventQueue = kept };
            foreach (PostedEvent e in st.EventQueue)
            {
                if (e == null) return "a queued event is empty";
                if (!Enum.IsDefined(typeof(SupportedEventList.Events), e.EventType)) return "a queued event is of an unknown type";
                if (e.TransitionToState != PostedEvent.NO_TRANSITION && (e.TransitionToState < 0 || e.TransitionToState >= states))
                    return "a queued state change names a state the script does not have";
                int args = e.Args?.Length ?? 0;
                for (int s = 0; s < states; s++)
                {
                    EventInfo handler = script.StateEvents[s] == null ? null : script.FindEvent(s, (int)e.EventType);
                    if (handler != null && handler.NumberOfArguments != args)
                        return $"a queued {e.EventType} carries {args} arguments where its handler takes {handler.NumberOfArguments}";
                }
                e.Args ??= Array.Empty<object>();
                limit.QueueEvent(e);
            }
            st.EventQueue = kept;
            return null;
        }

        private static int SizeOf(object[] values)
        {
            if (values == null) return 0;
            int sz = 0;
            foreach (object v in values) sz += v is LSLTable t ? t.MemorySize : MemoryCalc.CalcSizeOf(v);
            return sz;
        }

        /// <summary>Walks reachable values: every closure's function must lie in the code, and nesting is bounded.</summary>
        private sealed class ValueWalk
        {
            private readonly int m_CodeLength;
            private readonly HashSet<object> m_Seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
            public string Problem;

            public ValueWalk(int codeLength) { m_CodeLength = codeLength; }

            public void All(IEnumerable<object> values)
            {
                if (values == null) return;
                foreach (object v in values) One(v, 0);
            }

            private void One(object v, int depth)
            {
                if (Problem != null || v == null || v is string || v.GetType().IsValueType) return;
                if (depth > MaxValueDepth) { Problem = "values are nested too deeply"; return; }
                if (!m_Seen.Add(v)) return;
                switch (v)
                {
                    case LSLList list:
                        foreach (object m in list.Members) One(m, depth + 1);
                        break;
                    case LSLTable table:
                        foreach (object k in table.OrderedKeys) { One(k, depth + 1); One(table.Get(k), depth + 1); }
                        if (table.Metatable != null) One(table.Metatable, depth + 1);
                        break;
                    case LuaClosure closure:
                        Problem = CheckFunctionInfo(closure.Fn, m_CodeLength);
                        if (closure.Upvals != null) foreach (UpvalCell c in closure.Upvals) One(c, depth + 1);
                        break;
                    case UpvalCell cell:
                        One(cell.Value, depth + 1);
                        break;
                    case LuaGmatch gm:
                        // Op_GmatchNext scans from Pos to the end of Src; a script's iterator never stands outside it.
                        if (gm.Src == null || gm.Pat == null || gm.Pos < 0 || gm.Pos > gm.Src.Length + 1)
                            Problem = "a string.gmatch iterator's position is outside its string";
                        break;
                    case object[] arr:
                        foreach (object m in arr) One(m, depth + 1);
                        break;
                }
            }
        }
    }
}

/*
 * SLua Tier-1 "back-half" proof harness (ADDITIVE, offline self-test).
 *
 * Purpose: empirically settle the SLua thesis -- that a CompiledScript produced from
 * NON-LSL-originated assembly text runs on the existing Phlox VM AND round-trips through the
 * existing RuntimeState serialization (SerializedRuntimeState / protobuf) with ZERO new
 * serialization code. This is the Phlox equivalent of SL's "Ares" execution-state save; if it
 * holds, the hardest SL SLua subsystem is already solved on Phlox by construction.
 *
 * It does NOT involve any Luau parser/codegen: it hand-writes Phlox assembly text (what a future
 * SLua front-end would emit), feeds it through the existing assembler via
 * CompilerFrontend.AssembleText, runs it to a mid-execution pause, serializes -> deserializes via
 * the existing machinery, resumes on a fresh Interpreter, and checks the result is identical.
 *
 * Invoke from the region console:  phlox sluaproof
 * (registered once by PhloxEngine). No scene/world state is touched -- the test script makes no
 * syscalls, so it needs no ISystemAPI, no SceneObjectPart, nothing but the VM + serializer.
 */

using System;
using System.IO;
using System.Text;

using InWorldz.Phlox.Glue;
using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Serialization;

using ProtoBuf;

namespace Phlox.ScriptEngine
{
    /// <summary>
    /// Minimal no-op ISyscallShim for the offline self-test. The proof script makes no syscalls,
    /// so Call() should never fire; everything else is a harmless stub.
    /// </summary>
    internal sealed class SelfTestSyscallShim : ISyscallShim
    {
        public void Call(int funcid)
        {
            throw new InvalidOperationException(
                "SLua back-half proof script must not make syscalls (funcid=" + funcid + ").");
        }

        public void SetScriptEventFlags() { }
        public void ShoutError(string errorText) { }
        public void OnScriptReset() { }
        public void OnStateChange() { }
        public void OnScriptUnloaded(ScriptUnloadReason reason, RuntimeState.LocalDisableFlag localFlag) { }
        public void AddExecutionTime(double ms) { }
        public float GetAverageScriptTime() { return 0f; }
        public void OnScriptInjected(bool fromCrossing) { }
        public void OnGroupCrossedAvatarReady(OpenMetaverse.UUID avatarId) { }
    }

    public static class SluaBackHalfProof
    {
        // Target count the loop runs to. Large enough that a small tick budget pauses mid-loop.
        private const int LIMIT = 1000;

        // Hand-written Phlox assembly (the output a trivial SLua front-end would emit for a
        // no-syscall counter loop). Uses ONLY a global (gload/gstore) so the counter is observable
        // after a round-trip, and makes NO syscalls so no scene/API is required.
        //
        // Equivalent pseudo-source:
        //   global g = 0
        //   state_entry() { while (g < LIMIT) { g = g + 1; } }
        private static readonly string ASM =
            ".globals 1\n" +
            "\n" +
            ".statedef default\n" +
            "\n" +
            "iconst 0\n" +
            "gstore 0\n" +
            "halt\n" +
            "\n" +
            ".evt default/state_entry: args=0, locals=0\n" +
            "loop:\n" +
            "gload 0\n" +
            "iconst " + LIMIT + "\n" +
            "ilt\n" +
            "brf done\n" +
            "gload 0\n" +
            "iconst 1\n" +
            "iadd\n" +
            "gstore 0\n" +
            "jmp loop\n" +
            "done:\n" +
            "ret\n";

        /// <summary>
        /// Runs the full proof and returns a human-readable report. Never throws (captures and
        /// reports exceptions) so it is safe to call straight from a console command.
        /// </summary>
        public static string Run()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[SLua back-half proof] starting...");
            try
            {
                // 1. Assemble non-LSL assembly text into a CompiledScript via the EXISTING assembler.
                CompilerFrontend fe = new CompilerFrontend(new NullListener(), ".");
                CompiledScript cs = fe.AssembleText(ASM);
                if (cs == null)
                {
                    sb.AppendLine("  FAIL: AssembleText returned null (assembly error).");
                    return sb.ToString();
                }
                sb.AppendLine("  step 1: assembled CompiledScript OK (NumGlobals=" + cs.NumGlobals + ").");

                SelfTestSyscallShim shim = new SelfTestSyscallShim();

                // 2. Fresh interpreter: ctor starts at IP=0 / Running. Run global-init to the halt.
                Interpreter interp = new Interpreter(cs, shim);
                int guard = 0;
                while (interp.ScriptState.RunState == RuntimeState.Status.Running && guard++ < 100000)
                    interp.Tick();
                sb.AppendLine("  step 2: ran global-init, runstate=" + interp.ScriptState.RunState +
                              ", global[0]=" + interp.ScriptState.Globals[0] + " (expected 0).");

                // 3. Dispatch default/state_entry (mirrors the scheduler's event dispatch).
                EventInfo ei = cs.FindEvent(0, (int)SupportedEventList.Events.STATE_ENTRY);
                if (ei == null)
                {
                    sb.AppendLine("  FAIL: default/state_entry handler not found.");
                    return sb.ToString();
                }
                interp.ScriptState.RunState = RuntimeState.Status.Running;
                interp.ScriptState.DoEvent(
                    ei,
                    new PostedEvent { EventType = SupportedEventList.Events.STATE_ENTRY, Args = Array.Empty<object>() },
                    Array.Empty<object>());

                // 4. Tick a SMALL budget so we pause MID-loop (still Running, counter partway).
                for (int i = 0; i < 100 && interp.ScriptState.RunState == RuntimeState.Status.Running; i++)
                    interp.Tick();

                bool pausedMidLoop = interp.ScriptState.RunState == RuntimeState.Status.Running;
                int partial = Convert.ToInt32(interp.ScriptState.Globals[0]);
                sb.AppendLine("  step 3-4: dispatched state_entry, paused after budget. runstate=" +
                              interp.ScriptState.RunState + ", global[0]=" + partial +
                              " (expected 0 < partial < " + LIMIT + ").");

                // 5. Serialize the LIVE mid-execution state via the EXISTING machinery -- no new code.
                byte[] bytes;
                SerializedRuntimeState ser = SerializedRuntimeState.FromRuntimeState(interp.ScriptState);
                using (MemoryStream ms = new MemoryStream())
                {
                    Serializer.Serialize(ms, ser);
                    bytes = ms.ToArray();
                }
                sb.AppendLine("  step 5: serialized RuntimeState via SerializedRuntimeState/protobuf = " +
                              bytes.Length + " bytes.");

                // 6. Deserialize + restore into a NEW interpreter (the resume path).
                SerializedRuntimeState ser2;
                using (MemoryStream ms = new MemoryStream(bytes))
                    ser2 = Serializer.Deserialize<SerializedRuntimeState>(ms);
                RuntimeState restored = ser2.ToRuntimeState();
                Interpreter interp2 = new Interpreter(cs, restored, shim);
                sb.AppendLine("  step 6: deserialized + rebuilt Interpreter from restored state. " +
                              "restored runstate=" + interp2.ScriptState.RunState +
                              ", global[0]=" + interp2.ScriptState.Globals[0] +
                              " (should match partial=" + partial + ").");

                // 7. Resume to completion on the fresh interpreter.
                guard = 0;
                while (interp2.ScriptState.RunState == RuntimeState.Status.Running && guard++ < 10000000)
                    interp2.Tick();
                int final = Convert.ToInt32(interp2.ScriptState.Globals[0]);
                sb.AppendLine("  step 7: resumed to completion. runstate=" + interp2.ScriptState.RunState +
                              ", global[0]=" + final + " (expected " + LIMIT + ").");

                bool pass = pausedMidLoop
                            && partial > 0 && partial < LIMIT
                            && final == LIMIT;
                sb.AppendLine();
                sb.AppendLine("  ============================================================");
                sb.AppendLine("  RESULT: " + (pass
                    ? "PASS -- non-LSL bytecode ran on the existing VM and survived "
                      + "serialize->deserialize->resume via existing machinery, with NO new serialization code."
                    : "FAIL -- see steps above."));
                sb.AppendLine("  ============================================================");
            }
            catch (Exception e)
            {
                sb.AppendLine("  EXCEPTION during proof: " + e);
            }
            return sb.ToString();
        }
    }
}

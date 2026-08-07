using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Types;
using OpenMetaverse;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// An interpreter that can be loaded with a script and current execution state
    /// </summary>
    public partial class Interpreter
    {
        public delegate void StateChgDelegate(Interpreter script, int newState);

        public event StateChgDelegate OnStateChg;

        private CompiledScript _script;
        private RuntimeState _state;
        private System.IO.TextWriter _traceDestination;
        private ISyscallShim _syscallShim;
        private bool _outputFullExecutionTrace = false;
        private System.IO.TextWriter _fullTraceDestination;

        public UUID ItemId;

        public RuntimeState ScriptState
        {
            get
            {
                return _state;
            }
        }

        public CompiledScript Script
        {
            get
            {
                return _script;
            }
        }

        public System.IO.TextWriter TraceDestination
        {
            get
            {
                return _traceDestination;
            }

            set
            {
                _traceDestination = value;
            }
        }

        public bool TraceExecution
        {
            get
            {
                return _outputFullExecutionTrace;
            }

            set
            {
                _outputFullExecutionTrace = value;

                if (_outputFullExecutionTrace)
                {
                    _fullTraceDestination = new System.IO.StreamWriter(ItemId.ToString() + ".trace.txt");
                }
                else
                {
                    if (_fullTraceDestination != null)
                    {
                        _fullTraceDestination.Close();
                        _fullTraceDestination = null;
                    }
                }
            }
        }

        public Interpreter(CompiledScript script, ISyscallShim syscallShim)
        {
            _script = script;
            _state = new RuntimeState(script.NumGlobals);
            _state.MemInfo.UseMemory(script.CalcBaseMemorySize());
            _syscallShim = syscallShim;
        }

        public Interpreter(CompiledScript script, RuntimeState state, ISyscallShim syscallShim)
        {
            _script = script;
            _state = state;
            _syscallShim = syscallShim;
        }

        public void Reset()
        {
            _state.Reset();
            _state.MemInfo.UseMemory(Script.CalcBaseMemorySize());
            _syscallShim.OnScriptReset();
        }

        public void SetScriptEventFlags()
        {
            _syscallShim.SetScriptEventFlags();
        }

        public void ShoutError(string error)
        {
            _syscallShim.ShoutError(error);
        }

        public void OnUnload(ScriptUnloadReason reason, VM.RuntimeState.LocalDisableFlag localFlag)
        {
            _syscallShim.OnScriptUnloaded(reason, localFlag);
        }

        public void AddExecutionTime(double ms)
        {
            _syscallShim.AddExecutionTime(ms);
        }

        public float GetAverageScriptTime()
        {
            return _syscallShim.GetAverageScriptTime();
        }

        public void OnScriptInjected(bool fromCrossing)
        {
            _syscallShim.OnScriptInjected(fromCrossing);
        }

        public void OnGroupCrossedAvatarReady(UUID avatarId)
        {
            _syscallShim.OnGroupCrossedAvatarReady(avatarId);
        }

        private int GetIntOperand()
        {
            int word = Util.Encoding.GetInt(_script.ByteCode, _state.IP);
            _state.IP += 4;
            return word;
        }

        private void DiscardIntOperand()
        {
            _state.IP += 4;
        }

        private void TraceTick()
        {
            _fullTraceDestination.WriteLine();
            _fullTraceDestination.WriteLine();

            _fullTraceDestination.WriteLine(DateTime.Now.ToString());
            _fullTraceDestination.WriteLine("IP: {0}", _state.IP);
            _fullTraceDestination.WriteLine("{0} [{1}]", (OpCode)_script.ByteCode[_state.IP], _script.ByteCode.Length > _state.IP + 4 ? Util.Encoding.GetInt(_script.ByteCode, _state.IP + 1) : 0);
            _fullTraceDestination.WriteLine("Current Event: {0}", _state.RunningEvent != null ? ((Types.SupportedEventList.Events) _state.RunningEvent.EventType).ToString() : "None");
            _fullTraceDestination.WriteLine("Current Function: {0}", _state.TopFrame != null ? _state.TopFrame.FunctionInfo.Name : "None");
            _fullTraceDestination.WriteLine("Operand Stack:");
            foreach (object obj in _state.Operands)
            {
                _fullTraceDestination.WriteLine("  {0}: {1}", obj.GetType().ToString(), obj.ToString());
            }
            _fullTraceDestination.WriteLine("Locals:");

            if (_state.TopFrame != null && _state.TopFrame.Locals != null)
            {
                foreach (object obj in _state.TopFrame.Locals)
                {
                    _fullTraceDestination.WriteLine("  {0}: {1}", obj.GetType().ToString(), obj.ToString());
                }
            }

        }

        /// <summary>
        /// Executes a single instruction for the script that is active in this interpreter
        /// </summary>
        public void Tick()
        {
            OpCode opcode = (OpCode)_script.ByteCode[_state.IP];

            if (_outputFullExecutionTrace)
            {
                TraceTick();
            }

            if (_state.IP < _script.ByteCode.Length)
            {
                _state.IP++;

                switch (opcode)
                {
                    case OpCode.load:
                        Op_Load();
                        break;

                    case OpCode.load_sub:
                        Op_LoadSub();
                        break;

                    case OpCode.store:
                        Op_Store();
                        break;

                    case OpCode.store_sub:
                        Op_StoreSub();
                        break;

                    case OpCode.gload:
                        Op_Gload();
                        break;

                    case OpCode.gload_sub:
                        Op_GloadSub();
                        break;

                    case OpCode.gstore:
                        Op_Gstore();
                        break;

                    case OpCode.gstore_sub:
                        Op_GstoreSub();
                        break;

                    case OpCode.iconst:
                        Op_Iconst();
                        break;

                    case OpCode.fconst:
                        Op_Fconst();
                        break;

                    case OpCode.sconst:
                        Op_Sconst();
                        break;

                    case OpCode.vconst:
                        Op_Vconst();
                        break;

                    case OpCode.rconst:
                        Op_Rconst();
                        break;

                    case OpCode.lconst:
                        Op_Lconst();
                        break;

                    case OpCode.iadd:
                        Op_Iadd();
                        break;

                    case OpCode.isub:
                        Op_Isub();
                        break;

                    case OpCode.imul:
                        Op_Imul();
                        break;

                    case OpCode.idiv:
                        Op_Idiv();
                        break;

                    case OpCode.ibor:
                        Op_Ibor();
                        break;

                    case OpCode.iband:
                        Op_Iband();
                        break;

                    case OpCode.ibxor:
                        Op_Ibxor();
                        break;

                    case OpCode.irsh:
                        Op_Irsh();
                        break;

                    case OpCode.ilsh:
                        Op_Ilsh();
                        break;

                    case OpCode.ipreinc_l:
                        Op_Ipreinc_l();
                        break;

                    case OpCode.ipostinc_l:
                        Op_Ipostinc_l();
                        break;

                    case OpCode.ipredec_l:
                        Op_Ipredec_l();
                        break;

                    case OpCode.ipostdec_l:
                        Op_Ipostdec_l();
                        break;

                    case OpCode.ipreinc_g:
                        Op_Ipreinc_g();
                        break;

                    case OpCode.ipostinc_g:
                        Op_Ipostinc_g();
                        break;

                    case OpCode.ipredec_g:
                        Op_Ipredec_g();
                        break;

                    case OpCode.ipostdec_g:
                        Op_Ipostdec_g();
                        break;

                    case OpCode.fpreinc_l:
                        Op_Fpreinc_l();
                        break;

                    case OpCode.fpostinc_l:
                        Op_Fpostinc_l();
                        break;

                    case OpCode.fpredec_l:
                        Op_Fpredec_l();
                        break;

                    case OpCode.fpostdec_l:
                        Op_Fpostdec_l();
                        break;

                    case OpCode.fpreinc_g:
                        Op_Fpreinc_g();
                        break;

                    case OpCode.fpostinc_g:
                        Op_Fpostinc_g();
                        break;

                    case OpCode.fpredec_g:
                        Op_Fpredec_g();
                        break;

                    case OpCode.fpostdec_g:
                        Op_Fpostdec_g();
                        break;

                    case OpCode.fpreinc_l_sub:
                        Op_Fpreinc_l_sub();
                        break;

                    case OpCode.fpostinc_l_sub:
                        Op_Fpostinc_l_sub();
                        break;

                    case OpCode.fpredec_l_sub:
                        Op_Fpredec_l_sub();
                        break;

                    case OpCode.fpostdec_l_sub:
                        Op_Fpostdec_l_sub();
                        break;

                    case OpCode.fpreinc_g_sub:
                        Op_Fpreinc_g_sub();
                        break;

                    case OpCode.fpostinc_g_sub:
                        Op_Fpostinc_g_sub();
                        break;

                    case OpCode.fpredec_g_sub:
                        Op_Fpredec_g_sub();
                        break;

                    case OpCode.fpostdec_g_sub:
                        Op_Fpostdec_g_sub();
                        break;

                    case OpCode.ineg:
                        Op_Ineg();
                        break;

                    case OpCode.ilnot:
                        Op_Ilnot();
                        break;

                    case OpCode.ilor:
                        Op_Ilor();
                        break;

                    case OpCode.iland:
                        Op_Iland();
                        break;

                    case OpCode.ilt:
                        Op_Ilt();
                        break;

                    case OpCode.igt:
                        Op_Igt();
                        break;

                    case OpCode.ilte:
                        Op_Ilte();
                        break;

                    case OpCode.igte:
                        Op_Igte();
                        break;

                    case OpCode.ieq:
                        Op_Ieq();
                        break;

                    case OpCode.ineq:
                        Op_Ineq();
                        break;

                    case OpCode.fadd:
                        Op_Fadd();
                        break;

                    case OpCode.fsub:
                        Op_Fsub();
                        break;

                    case OpCode.fmul:
                        Op_Fmul();
                        break;

                    case OpCode.fdiv:
                        Op_Fdiv();
                        break;

                    case OpCode.fneg:
                        Op_Fneg();
                        break;

                    case OpCode.flt:
                        Op_Flt();
                        break;

                    case OpCode.fgt:
                        Op_Fgt();
                        break;

                    case OpCode.flte:
                        Op_Flte();
                        break;

                    case OpCode.fgte:
                        Op_Fgte();
                        break;

                    case OpCode.feq:
                        Op_Feq();
                        break;

                    case OpCode.fneq:
                        Op_Fneq();
                        break;

                    case OpCode.vadd:
                        Op_Vadd();
                        break;

                    case OpCode.vsub:
                        Op_Vsub();
                        break;

                    case OpCode.vmul:
                        Op_Vmul();
                        break;

                    case OpCode.vcross:
                        Op_Vcross();
                        break;

                    case OpCode.veq:
                        Op_Veq();
                        break;

                    case OpCode.vneq:
                        Op_Vneq();
                        break;

                    case OpCode.sconcat:
                        Op_Sconcat();
                        break;

                    case OpCode.seq:
                        Op_Seq();
                        break;

                    case OpCode.sneq:
                        Op_Sneq();
                        break;

                    case OpCode.radd:
                        Op_Radd();
                        break;

                    case OpCode.rsub:
                        Op_Rsub();
                        break;

                    case OpCode.rmul:
                        Op_Rmul();
                        break;

                    case OpCode.rdiv:
                        Op_Rdiv();
                        break;

                    case OpCode.req:
                        Op_Req();
                        break;

                    case OpCode.rneq:
                        Op_Rneq();
                        break;

                    case OpCode.vrmul:
                        Op_Vrmul();
                        break;

                    case OpCode.vimul:
                        Op_Vimul();
                        break;

                    case OpCode.vfmul:
                        Op_Vfmul();
                        break;

                    case OpCode.pop:
                        Op_Pop();
                        break;

                    case OpCode.list_prepend:
                        Op_ListPrepend();
                        break;

                    case OpCode.list_append:
                        Op_ListAppend();
                        break;

                    case OpCode.jmp:
                        Op_Jmp();
                        break;

                    case OpCode.call:
                        Op_Call();
                        break;

                    case OpCode.ret:
                        Op_Ret();
                        break;

                    case OpCode.syscall:
                        Op_Syscall();
                        break;

                    case OpCode.halt:
                        Op_Halt();
                        break;

                    case OpCode.icast:
                        Op_Icast();
                        break;

                    case OpCode.fcast:
                        Op_Fcast();
                        break;

                    case OpCode.scast:
                        Op_Scast();
                        break;

                    case OpCode.vcast:
                        Op_Vcast();
                        break;

                    case OpCode.rcast:
                        Op_Rcast();
                        break;

                    case OpCode.lcast:
                        Op_Lcast();
                        break;

                    case OpCode.buildvec:
                        Op_BuildVec();
                        break;

                    case OpCode.buildrot:
                        Op_BuildRot();
                        break;

                    case OpCode.buildlist:
                        Op_BuildList();
                        break;

                    case OpCode.trace:
                        Op_Trace();
                        break;

                    case OpCode.brt:
                        Op_Brt();
                        break;

                    case OpCode.brf:
                        Op_Brf();
                        break;

                    case OpCode.statechg:
                        Op_StateChg();
                        break;

                    case OpCode.vneg:
                        Op_Vneg();
                        break;

                    case OpCode.rneg:
                        Op_Rneg();
                        break;

                    case OpCode.ibunot:
                        Op_Ibunot();
                        break;
                    
                    case OpCode.vidiv:
                        Op_Vidiv();
                        break;

                    case OpCode.vfdiv:
                        Op_Vfdiv();
                        break;

                    case OpCode.vrdiv:
                        Op_Vrdiv();
                        break;

                    case OpCode.leq:
                        Op_Leq();
                        break;

                    case OpCode.iinit_g:
                        Op_Iinit_g();
                        break;

                    case OpCode.finit_g:
                        Op_Finit_g();
                        break;

                    case OpCode.vinit_g:
                        Op_Vinit_g();
                        break;

                    case OpCode.rinit_g:
                        Op_Rinit_g();
                        break;

                    case OpCode.sinit_g:
                        Op_Sinit_g();
                        break;

                    case OpCode.linit_g:
                        Op_Linit_g();
                        break;

                    case OpCode.iinit_l:
                        Op_Iinit_l();
                        break;

                    case OpCode.finit_l:
                        Op_Finit_l();
                        break;

                    case OpCode.vinit_l:
                        Op_Vinit_l();
                        break;

                    case OpCode.rinit_l:
                        Op_Rinit_l();
                        break;

                    case OpCode.sinit_l:
                        Op_Sinit_l();
                        break;

                    case OpCode.linit_l:
                        Op_Linit_l();
                        break;

                    case OpCode.imod:
                        Op_Imod();
                        break;

                    case OpCode.lneq:
                        Op_Lneq();
                        break;

                    case OpCode.kinit_g:
                        Op_Kinit_g();
                        break;

                    case OpCode.kinit_l:
                        Op_Kinit_l();
                        break;
                       
                    case OpCode.booleval:
                        Op_Booleval();
                        break;

                    // ---- SLua Tier-2: tables ----
                    case OpCode.pushnil:
                        Op_PushNil();
                        break;
                    case OpCode.buildtable:
                        Op_BuildTable();
                        break;
                    case OpCode.tabget:
                        Op_TabGet();
                        break;
                    case OpCode.tabset:
                        Op_TabSet();
                        break;
                    case OpCode.tablen:
                        Op_TabLen();
                        break;
                    case OpCode.tabnext:
                        Op_TabNext();
                        break;
                    case OpCode.isnil:
                        Op_IsNil();
                        break;

                    // ---- SLua Tier-2: dynamic typing ----
                    case OpCode.pushtrue:
                        SafeOperandsPush(true);
                        break;
                    case OpCode.pushfalse:
                        SafeOperandsPush(false);
                        break;
                    case OpCode.luatruthy:
                        Op_LuaTruthy();
                        break;
                    case OpCode.lnot:
                        Op_LNot();
                        break;
                    case OpCode.tobool:
                        Op_ToBool();
                        break;
                    case OpCode.luaeq:
                        Op_LuaEq();
                        break;
                    case OpCode.concat:
                        Op_Concat();
                        break;
                    case OpCode.luatype:
                        Op_LuaType();
                        break;
                    case OpCode.luatostr:
                        Op_LuaToStr();
                        break;
                    case OpCode.luatonum:
                        Op_LuaToNum();
                        break;
                    case OpCode.dup:
                        Op_Dup();
                        break;
                    case OpCode.luacall:
                        Op_LuaCall();
                        break;
                    case OpCode.luacallm:
                        Op_LuaCallM();
                        break;
                    case OpCode.adjustm:
                        Op_AdjustM();
                        break;
                    case OpCode.gmatchnext:
                        Op_GmatchNext();
                        break;

                    // ---- SLua Tier-2: closures ----
                    case OpCode.mkcell:
                        Op_MkCell();
                        break;
                    case OpCode.cellget:
                        Op_CellGet();
                        break;
                    case OpCode.cellput:
                        Op_CellPut();
                        break;
                    case OpCode.getupval:
                        Op_GetUpval();
                        break;
                    case OpCode.setupval:
                        Op_SetUpval();
                        break;
                    case OpCode.pushupval:
                        Op_PushUpval();
                        break;
                    case OpCode.mkclosure:
                        Op_MkClosure();
                        break;
                    case OpCode.callv:
                        Op_CallV();
                        break;
                    case OpCode.regevent:
                        Op_RegEvent();
                        break;
                    case OpCode.methcall:
                        Op_MethCall();
                        break;
                    case OpCode.firellevents:
                        Op_FireLLEvents();
                        break;

                    // ---- SLua Tier-2: metatables ----
                    case OpCode.luabinop:
                        Op_LuaBinop();
                        break;
                    case OpCode.luaunm:
                        Op_LuaUnm();
                        break;
                    case OpCode.setmeta:
                        Op_SetMeta();
                        break;
                    case OpCode.getmeta:
                        Op_GetMeta();
                        break;

                    // ---- SLua conformance pass: error handling + table.sort ----
                    case OpCode.luaerror:
                        Op_LuaError();
                        break;
                    case OpCode.luapcall:
                        Op_LuaPcall();
                        break;
                    case OpCode.luasort:
                        Op_LuaSort();
                        break;

                    default:
                        throw new VMException("Unhandled opcode: " + opcode);
                }
            }
        }


    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.Types;
using InWorldz.Phlox.VM;
using InWorldz.Phlox.Util;

using Antlr4.Runtime;
using OpenMetaverse;

namespace InWorldz.Phlox.ByteCompiler
{
    public class BytecodeGenerator
    {
        private Dictionary<string, OpCode> _instructionOpCodeMap = new Dictionary<string, OpCode>();
        private Dictionary<string, LabelSymbol> _labels = new Dictionary<string, LabelSymbol>();
        private Dictionary<string, FunctionSymbol> _functions = new Dictionary<string, FunctionSymbol>();
        private Dictionary<string, EventSymbol> _events = new Dictionary<string, EventSymbol>();
        private Dictionary<string, int> _states = new Dictionary<string, int>();
        private List<object> _constPool = new List<object>();

        private const int INITIAL_BYTECODE_SZ = 1024;
        private int _ip = 0;
        private byte[] _code = new byte[INITIAL_BYTECODE_SZ];
        private int _globalsSize = 0;
        private int _nextStateId = 0;

        public CompiledScript Result
        {
            get
            {
                this.CheckForUnresolvedReferences();

                CompiledScript script = new CompiledScript();
                script.ByteCode = new byte[_ip];
                Array.Copy(_code, script.ByteCode, _ip);
                script.ConstPool = this.FinalizeConstPool();
                script.NumGlobals = _globalsSize;
                script.StateEvents = this.CreateStateEventList();

                return script;
            }
        }

        private EventInfo[][] CreateStateEventList()
        {
            if (_states.Count == 0)
            {
                throw new GenerationException(String.Format("Script must contain a default state"));
            }

            EventInfo[][] outEvents = new EventInfo[_states.Count][];

            for (int i = 0; i < _states.Count; ++i)
            {
                List<EventSymbol> stateEvents = this.FindEventsForState(i);

                if (stateEvents.Count == 0)
                {
                    foreach (var stateKvp in _states)
                    {
                        if (stateKvp.Value == i)
                        {
                            throw new GenerationException(String.Format("State {0} must contain at least 1 event", stateKvp.Key));
                        }
                    }
                }

                outEvents[i] = new EventInfo[stateEvents.Count];

                for (int j = 0; j < stateEvents.Count; ++j)
                {
                    outEvents[i][j] = stateEvents[j].ToEventInfo();
                }
            }

            return outEvents;
        }

        private List<EventSymbol> FindEventsForState(int i)
        {
            List<EventSymbol> events = new List<EventSymbol>();
            foreach (EventSymbol evt in _events.Values)
            {
                if (evt.State == i)
                {
                    events.Add(evt);
                }
            }

            return events;
        }

        private object[] FinalizeConstPool()
        {
            object[] finalConstPool = new object[_constPool.Count];

            for (int i = 0; i < _constPool.Count; ++i)
            {
                object obj = _constPool[i];
                FunctionSymbol fs = obj as FunctionSymbol;
                if (fs != null)
                {
                    finalConstPool[i] = fs.ToFunctionInfo();
                }
                else
                {
                    finalConstPool[i] = obj;
                }
            }

            return finalConstPool;
        }

        public BytecodeGenerator(IEnumerable<FunctionSig> systemMethods)
        {
            foreach (OpCode code in Enum.GetValues(typeof(OpCode)))
            {
                _instructionOpCodeMap.Add(code.ToString().Replace('_', '.'), code);
            }

            foreach (FunctionSig sig in systemMethods)
            {
                _functions.Add(sig.FunctionName, new FunctionSymbol(sig.FunctionName, sig.TableIndex));
            }
        }

        public void Gen(IToken instrToken)
        {
            OpCode code;
            if (!_instructionOpCodeMap.TryGetValue(instrToken.Text, out code))
            {
                throw new GenerationException("Unknown opcode " + instrToken.Text);
            }

            EnsureCapacity(_ip + 1);
            _code[_ip++] = (byte)code;
        }

        public void Gen(IToken instrToken, IToken operandToken)
        {
            Gen(instrToken);
            GenOperand(operandToken);
        }

        public void Gen(IToken instrToken, IToken oToken1, IToken oToken2)
        {
            Gen(instrToken, oToken1);
            GenOperand(oToken2);
        }

        public void Gen(IToken instrToken, IToken oToken1, IToken oToken2, IToken oToken3)
        {
            Gen(instrToken, oToken1, oToken2);
            GenOperand(oToken3);
        }

        public int GetConstantPoolIndex(object obj)
        {
            int index = _constPool.IndexOf(obj);
            if (index == -1)
            {
                _constPool.Add(obj);
                return _constPool.Count - 1;
            }
            else
            {
                return index;
            }
        }

        public int GetFunctionIndex(string funcId)
        {
            FunctionSymbol searchSym;
            if (_functions.TryGetValue(funcId, out searchSym))
            {
                if (searchSym.IsFwdRef)
                {
                    searchSym.AddFwdRef(_ip);
                }

                return searchSym.ConstIndex;
            }
            else
            {
                searchSym = new FunctionSymbol(funcId, _constPool.Count);
                _functions.Add(funcId, searchSym);
                _constPool.Add(searchSym);
                searchSym.AddFwdRef(_ip);
                return searchSym.ConstIndex;
            }
        }

        public int GetLabelAddress(string labelId)
        {
            LabelSymbol label;
            if (_labels.TryGetValue(labelId, out label))
            {
                if (label.IsFwdRef)
                {
                    label.AddFwdRef(_ip);
                }

                return label.Address;
            }
            else
            {
                LabelSymbol newLab = new LabelSymbol(labelId);
                newLab.AddFwdRef(_ip);
                _labels.Add(labelId, newLab);
                return 0;
            }
        }

        public int ConvertToInt(string symbolText)
        {
            try
            {
                if (symbolText.StartsWith("0x") || symbolText.StartsWith("-0x"))
                {
                    string strippedText = symbolText;
                    if (symbolText.StartsWith("-"))
                    {
                        strippedText = symbolText.Substring(1);
                    }

                    int num = Int32.Parse(strippedText.Substring(2), System.Globalization.NumberStyles.HexNumber);
                    if (symbolText.StartsWith("-"))
                    {
                        return -num;
                    }
                    else
                    {
                        return num;
                    }
                }
                else
                {
                    return Int32.Parse(symbolText);
                }
            }
            catch (OverflowException)
            {
                return -1;
            }
        }

        public string UnescapeStringChars(string txt)
        {
            if (string.IsNullOrEmpty(txt)) return txt;

            StringBuilder retval = new StringBuilder(txt.Length);
            for (int ix = 0; ix < txt.Length; )
            {
                int jx = txt.IndexOf('\\', ix);
                if (jx < 0 || jx == txt.Length - 1) jx = txt.Length;

                retval.Append(txt, ix, jx - ix);
                if (jx >= txt.Length) break;

                switch (txt[jx + 1])
                {
                    case 'n': retval.Append('\n'); break;
                    case 'r': retval.Append('\r'); break;
                    case 't': retval.Append("    "); break;
                    case '"': retval.Append('"'); break;
                    case '\\': retval.Append('\\'); break;
                    default:
                        retval.Append('\\').Append(txt[jx + 1]); break;
                }
                ix = jx + 2;
            }

            return retval.ToString();
        }

        public void GenOperand(IToken operand)
        {
            int val = 0;
            switch (operand.Type)
            {
                case AssemblerParser.INT: val = ConvertToInt(operand.Text); break;
                case AssemblerParser.FLOAT: val = GetConstantPoolIndex(Convert.ToSingle(operand.Text)); break;
                case AssemblerParser.STRING: val = GetConstantPoolIndex(UnescapeStringChars(operand.Text)); break;
                case AssemblerParser.VECTOR: val = GetConstantPoolIndex(Vector3.Parse(operand.Text)); break;
                case AssemblerParser.ROTATION: val = GetConstantPoolIndex(Quaternion.Parse(operand.Text)); break;
                case AssemblerParser.FUNC: val = GetFunctionIndex(operand.Text); break;
                case AssemblerParser.ID: val = GetLabelAddress(operand.Text); break;
                case AssemblerParser.STATE_ID: val = GetStateId(operand.Text); break;

                default: throw new GenerationException(String.Format("Invalid operand type {0} in GenOperand()", operand.Type));
            }

            EnsureCapacity(_ip + 4);
            Util.Encoding.WriteInt(_code, _ip, val);
            _ip += 4;
        }

        private int GetStateId(string stateName)
        {
            int id;
            if (_states.TryGetValue(stateName, out id))
            {
                return id;
            }
            else
            {
                throw new GenerationException("Invalid state " + stateName);
            }
        }

        private void EnsureCapacity(int index)
        {
            if (index >= _code.Length)
            {
                int newSize = Math.Max(index, _code.Length) * 2;
                byte[] bigger = new byte[newSize];
                Array.Copy(_code, 0, bigger, 0, _code.Length);
                _code = bigger;
            }
        }

        public void CheckForUnresolvedReferences()
        {
            bool hasUndeffed = false;
            StringBuilder undeffed = new StringBuilder("Undefined references: ");
            foreach (LabelSymbol label in _labels.Values)
            {
                if (label.IsFwdRef)
                {
                    undeffed.Append(label.Name);
                    undeffed.Append(",");
                    hasUndeffed = true;
                }
            }

            foreach (object obj in _constPool.Where(p => p is FunctionSymbol))
            {
                FunctionSymbol sym = (FunctionSymbol)obj;
                if (sym.IsFwdRef)
                {
                    undeffed.Append(sym.Name);
                    undeffed.Append(",");
                    hasUndeffed = true;
                }
            }

            if (hasUndeffed)
            {
                throw new GenerationException(undeffed.ToString());
            }
        }

        public void DefineFunction(IToken idToken, int nargs, int nlocals)
        {
            FunctionSymbol searchSym;
            if (_functions.TryGetValue(idToken.Text, out searchSym))
            {
                if (searchSym.IsFwdRef)
                {
                    searchSym.Define(_ip, nargs, nlocals);
                    searchSym.ResolveFwdRefs(_code);
                }
                else
                {
                    throw new GenerationException(String.Format("Function '{0}' already defined", idToken.Text));
                }
            }
            else
            {
                searchSym = new FunctionSymbol(idToken.Text, _ip, _constPool.Count, nargs, nlocals);
                _functions.Add(idToken.Text, searchSym);
                _constPool.Add(searchSym);
            }
        }

        public void DefineLabel(IToken idToken)
        {
            LabelSymbol label;
            if (_labels.TryGetValue(idToken.Text, out label))
            {
                if (label.IsFwdRef)
                {
                    label.Define(_ip);
                    label.ResolveFwdRefs(_code);
                }
                else
                {
                    throw new GenerationException(String.Format("line {0}:{1} Label '{2}' already defined",
                        idToken.Line, idToken.Column, idToken.Text));
                }
            }
            else
            {
                label = new LabelSymbol(idToken.Text, _ip);
                _labels.Add(label.Name, label);
            }
        }

        public void DefineEventHandler(IToken stateIdToken, IToken idToken, int nargs, int nlocals)
        {
            int stateId;
            if (!_states.TryGetValue(stateIdToken.Text, out stateId))
            {
                throw new GenerationException(String.Format("line {0}:{1} Invalid state {2}",
                    stateIdToken.Line, stateIdToken.Column, stateIdToken.Text));
            }

            if (_events.ContainsKey(stateIdToken.Text + "." + idToken.Text))
            {
                throw new GenerationException(String.Format("line {0}:{1} Event '{2}.{3}' already defined",
                    idToken.Line, idToken.Column, stateIdToken.Text, idToken.Text));
            }
            else
            {
                EventSymbol evt = new EventSymbol(stateId, idToken.Text, _ip, nargs, nlocals);
                _events.Add(stateIdToken.Text + "." + idToken.Text, evt);
            }
        }

        public void DefineState(IToken stateId)
        {
            if (_states.ContainsKey(stateId.Text))
            {
                throw new GenerationException(String.Format("line {0}:{1} State {2} already defined",
                    stateId.Line, stateId.Column, stateId.Text));
            }
            else
            {
                _states.Add(stateId.Text, _nextStateId++);
            }
        }

        public void DefineDataSize(int n)
        {
            _globalsSize = n;
        }
    }
}
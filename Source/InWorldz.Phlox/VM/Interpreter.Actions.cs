using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using OpenMetaverse;
using InWorldz.Phlox.Types;
using System.Text.RegularExpressions;
using System.Globalization;

namespace InWorldz.Phlox.VM
{
    public partial class Interpreter
    {
        public void SafeOperandsPush(object obj)
        {
            if (obj == null)
                throw new VMException("Attempt to push null operand.\n" + DumpState());

            /*if (obj is Sentinel)
                throw new VMException("Attempt to push sentinel operand.\n" + DumpState());*/

            _state.Operands.Push(obj);
        }


        private static int ConvToInt(object o)
        {
            if (o is int i) return i;
            if (o is float f) return (int)f;
            if (o is string s && int.TryParse(s, out int parsed)) return parsed;
            return Convert.ToInt32(o);
        }

        private static float ConvToFloat(object o)
        {
            if (o is float f) return f;
            if (o is int i) return (float)i;
            if (o is string s && float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float parsed)) return parsed;
            return Convert.ToSingle(o);
        }

        private string DumpState()
        {
            StringBuilder stateinfo = new StringBuilder();

            stateinfo.AppendLine(String.Format("IP: {0}", _state.IP));

            string frameinfo = "Top Frame: ";
            string frameinfoLocals = "  Locals: None";
            if (_state.TopFrame != null)
            {
                frameinfo = String.Format("Top Frame: Name: {0}, Addr: {1}, Locals {2}", _state.TopFrame.FunctionInfo.Name,
                        _state.TopFrame.FunctionInfo.Address,
                        _state.TopFrame.FunctionInfo.NumberOfArguments + _state.TopFrame.FunctionInfo.NumberOfLocals);

                StringBuilder locals = new StringBuilder();
                foreach (object obj in _state.TopFrame.Locals)
                {
                    locals.AppendLine(String.Format("  Local: {0}, Type: {1}", obj, obj != null ? obj.GetType().FullName : "null"));
                }

                frameinfoLocals = locals.ToString();
            }

            stateinfo.AppendLine(frameinfo);
            stateinfo.AppendLine(frameinfoLocals);

            string eventinfo = "Running Event: None";
            string eventinfoArgs = "  Args: None";
            if (_state.RunningEvent != null)
            {
                eventinfo = String.Format("Running Event: Name: {0}", _state.RunningEvent.EventType.ToString());

                StringBuilder args = new StringBuilder();
                foreach (object obj in _state.RunningEvent.Args)
                {
                    args.AppendLine(String.Format("  Arg: {0}, Type: {1}", obj, obj != null ? obj.GetType().FullName : "null"));
                }

                eventinfoArgs = args.ToString();
            }

            stateinfo.AppendLine(eventinfo);
            stateinfo.AppendLine(eventinfoArgs);

            return stateinfo.ToString();
        }

        private void _Load(object[] varList)
        {
            int index = this.GetIntOperand();
            object local = varList[index];
            SafeOperandsPush(local);
        }

        private void _LoadSub(object[] varList)
        {
            int index = this.GetIntOperand();
            int subIndex = this.GetIntOperand();

            object local = varList[index];

            if (local is Vector3)
            {
                Vector3 vlocal = (Vector3)local;
                switch (subIndex)
                {
                    case 0:
                        SafeOperandsPush(vlocal.X);
                        break;

                    case 1:
                        SafeOperandsPush(vlocal.Y);
                        break;

                    case 2:
                        SafeOperandsPush(vlocal.Z);
                        break;

                    default:
                        throw new VMException("Op_LoadSub: Invalid subscript index for vector");
                }
            }
            else if (local is Quaternion)
            {
                Quaternion qlocal = (Quaternion)local;
                switch (subIndex)
                {
                    case 0:
                        SafeOperandsPush(qlocal.X);
                        break;

                    case 1:
                        SafeOperandsPush(qlocal.Y);
                        break;

                    case 2:
                        SafeOperandsPush(qlocal.Z);
                        break;

                    case 3:
                        SafeOperandsPush(qlocal.W);
                        break;

                    default:
                        throw new VMException("Op_LoadSub: Invalid subscript index for rotation");
                }
            }
            else
            {
                throw new VMException("Op_LoadSub: Subscript access on non-subscriptable type");
            }
        }

        private void Op_Load()
        {
            this._Load(_state.TopFrame.Locals);
        }

        private void Op_LoadSub()
        {
            this._LoadSub(_state.TopFrame.Locals);
        }

        private void _Store(object[] destList)
        {
            int index = this.GetIntOperand();
            object currLocal = destList[index];
            object newLocal = _state.Operands.Pop();

            destList[index] = newLocal;

            _state.MemInfo.ReplaceStored(currLocal, newLocal);
        }

        private void Op_Store()
        {
            this._Store(_state.TopFrame.Locals);
        }

        enum SubScriptOp
        {
            NONE,
            INC,
            DEC
        }

        /// <summary>
        /// Assigns a new value to a subscript of an existing value
        /// </summary>
        /// <param name="destList">The list to retrieve the value from</param>
        /// <param name="destIndex">The index of the item to change</param>
        /// <param name="subIndex">The subscript index to change</param>
        /// <param name="subScriptValue">The new value for the subscript</param>
        /// <returns>The old subscript value</returns>
        private float _AssignSubscript(object[] destList, int destIndex, int subIndex, object subScriptValue,
            SubScriptOp op)
        {
            object currLocal = destList[destIndex];

            if (currLocal is Vector3)
            {
                Vector3 vlocal = (Vector3)currLocal;

                switch (subIndex)
                {
                    case 0:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = vlocal.X + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = vlocal.X - 1;
                        }

                        destList[destIndex] = new Vector3((float)subScriptValue, vlocal.Y, vlocal.Z);
                        return vlocal.X;

                    case 1:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = vlocal.Y + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = vlocal.Y - 1;
                        }

                        destList[destIndex] = new Vector3(vlocal.X, (float)subScriptValue, vlocal.Z);
                        return vlocal.Y;

                    case 2:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = vlocal.Z + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = vlocal.Z - 1;
                        }

                        destList[destIndex] = new Vector3(vlocal.X, vlocal.Y, (float)subScriptValue);
                        return vlocal.Z;

                    default:
                        throw new VMException("Op_LoadSub: Invalid subscript index for vector");
                }
            }
            else if (currLocal is Quaternion)
            {
                Quaternion qlocal = (Quaternion)currLocal;
                switch (subIndex)
                {
                    case 0:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = qlocal.X + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = qlocal.X - 1;
                        }

                        destList[destIndex] = new Quaternion((float)subScriptValue, qlocal.Y, qlocal.Z, qlocal.W);
                        return qlocal.X;

                    case 1:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = qlocal.Y + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = qlocal.Y - 1;
                        }

                        destList[destIndex] = new Quaternion(qlocal.X, (float)subScriptValue, qlocal.Z, qlocal.W);
                        return qlocal.Y;

                    case 2:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = qlocal.Z + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = qlocal.Z - 1;
                        }

                        destList[destIndex] = new Quaternion(qlocal.X, qlocal.Y, (float)subScriptValue, qlocal.W);
                        return qlocal.Z;

                    case 3:
                        if (op == SubScriptOp.INC)
                        {
                            subScriptValue = qlocal.W + 1;
                        }
                        else if (op == SubScriptOp.DEC)
                        {
                            subScriptValue = qlocal.W - 1;
                        }

                        destList[destIndex] = new Quaternion(qlocal.X, qlocal.Y, qlocal.Z, (float)subScriptValue);
                        return qlocal.W;

                    default:
                        throw new VMException("Op_LoadSub: Invalid subscript index for rotation");
                }
            }
            else
            {
                throw new VMException("Op_LoadSub: Subscript access on non-subscriptable type");
            }
        }

        private void _StoreSub(object[] destList)
        {
            int index = this.GetIntOperand();
            int subIndex = this.GetIntOperand();

            object subScriptValue = _state.Operands.Pop();
            _AssignSubscript(destList, index, subIndex, subScriptValue, SubScriptOp.NONE);
        }

        private void Op_StoreSub()
        {
            this._StoreSub(_state.TopFrame.Locals);
        }

        private void Op_Gload()
        {
            this._Load(_state.Globals);
        }

        private void Op_GloadSub()
        {
            this._LoadSub(_state.Globals);
        }

        private void Op_Gstore()
        {
            this._Store(_state.Globals);
        }

        private void Op_GstoreSub()
        {
            this._StoreSub(_state.Globals);
        }

        private void Op_Iconst()
        {
            int constVal = this.GetIntOperand();
            SafeOperandsPush(constVal);
        }

        private void Op_Fconst()
        {
            int constIndex = this.GetIntOperand();
            SafeOperandsPush(_script.ConstPool[constIndex]);
        }

        private void Op_Sconst()
        {
            int constIndex = this.GetIntOperand();
            SafeOperandsPush((string)_script.ConstPool[constIndex]);
        }

        private void Op_Vconst()
        {
            int constIndex = this.GetIntOperand();
            SafeOperandsPush(_script.ConstPool[constIndex]);
        }

        private void Op_Rconst()
        {
            int constIndex = this.GetIntOperand();
            SafeOperandsPush(_script.ConstPool[constIndex]);
        }

        private void Op_Lconst()
        {
            int constIndex = this.GetIntOperand();
            SafeOperandsPush(_script.ConstPool[constIndex]);
        }

        private void Op_Iadd()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a + b);
        }

        private void Op_Isub()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a - b);
        }

        private void Op_Imul()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a * b);
        }

        private void Op_Idiv()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a / b);
        }

        private void Op_Imod()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a % b);
        }

        private void Op_Ibor()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a | b);
        }

        private void Op_Iband()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a & b);
        }

        private void Op_Ibxor()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a ^ b);
        }

        private void Op_Irsh()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a >> b);
        }

        private void Op_Ilsh()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            SafeOperandsPush(a << b);
        }

        private void _IPreinc(object[] destList)
        {
            int index = this.GetIntOperand();
            int oldVal = (int)destList[index];
            int newVal = oldVal + 1;

            destList[index] = newVal;

            SafeOperandsPush(newVal);
        }

        private void _IPostInc(object[] destList)
        {
            int index = this.GetIntOperand();
            int oldVal = (int)destList[index];
            int newVal = oldVal + 1;

            destList[index] = newVal;

            SafeOperandsPush(oldVal);
        }

        private void _IPreDec(object[] destList)
        {
            int index = this.GetIntOperand();
            int oldVal = (int)destList[index];
            int newVal = oldVal - 1;

            destList[index] = newVal;

            SafeOperandsPush(newVal);
        }

        private void _IPostDec(object[] destList)
        {
            int index = this.GetIntOperand();
            int oldVal = (int)destList[index];
            int newVal = oldVal - 1;

            destList[index] = newVal;

            SafeOperandsPush(oldVal);
        }

        private void _FPreInc(object[] destList)
        {
            int index = this.GetIntOperand();
            float oldVal = (float)destList[index];
            float newVal = oldVal + 1;

            destList[index] = newVal;

            SafeOperandsPush(newVal);
        }

        private void _FPostInc(object[] destList)
        {
            int index = this.GetIntOperand();
            float oldVal = (float)destList[index];
            float newVal = oldVal + 1;

            destList[index] = newVal;

            SafeOperandsPush(oldVal);
        }

        private void _FPreDec(object[] destList)
        {
            int index = this.GetIntOperand();
            float oldVal = (float)destList[index];
            float newVal = oldVal - 1;

            destList[index] = newVal;

            SafeOperandsPush(newVal);
        }

        private void _FPostDec(object[] destList)
        {
            int index = this.GetIntOperand();
            float oldVal = (float)destList[index];
            float newVal = oldVal - 1;

            destList[index] = newVal;

            SafeOperandsPush(oldVal);
        }

        private void Op_Ipreinc_l()
        {
            this._IPreinc(_state.TopFrame.Locals);
        }

        private void Op_Ipostinc_l()
        {
            this._IPostInc(_state.TopFrame.Locals);
        }

        private void Op_Ipredec_l()
        {
            this._IPreDec(_state.TopFrame.Locals);
        }

        private void Op_Ipostdec_l()
        {
            this._IPostDec(_state.TopFrame.Locals);
        }

        private void Op_Ipreinc_g()
        {
            this._IPreinc(_state.Globals);
        }

        private void Op_Ipostinc_g()
        {
            this._IPostInc(_state.Globals);
        }

        private void Op_Ipredec_g()
        {
            this._IPreDec(_state.Globals);
        }

        private void Op_Ipostdec_g()
        {
            this._IPostDec(_state.Globals);
        }

        private void Op_Fpreinc_l()
        {
            this._FPreInc(_state.TopFrame.Locals);
        }

        private void Op_Fpostinc_l()
        {
            this._FPostInc(_state.TopFrame.Locals);
        }

        private void Op_Fpredec_l()
        {
            this._FPreDec(_state.TopFrame.Locals);
        }

        private void Op_Fpostdec_l()
        {
            this._FPostDec(_state.TopFrame.Locals);
        }

        private void Op_Fpreinc_g()
        {
            this._FPreInc(_state.Globals);
        }

        private void Op_Fpostinc_g()
        {
            this._FPostInc(_state.Globals);
        }

        private void Op_Fpredec_g()
        {
            this._FPreDec(_state.Globals);
        }

        private void Op_Fpostdec_g()
        {
            this._FPostDec(_state.Globals);
        }

        private void _FPreIncSub(object[] destList)
        {
            int index = this.GetIntOperand();
            int subIndex = this.GetIntOperand();

            float prevVal = _AssignSubscript(destList, index, subIndex, null, SubScriptOp.INC);
            SafeOperandsPush(prevVal + 1);
        }

        private void _FPostIncSub(object[] destList)
        {
            int index = this.GetIntOperand();
            int subIndex = this.GetIntOperand();

            float prevVal = _AssignSubscript(destList, index, subIndex, null, SubScriptOp.INC);
            SafeOperandsPush(prevVal);
        }

        private void _FPreDecSub(object[] destList)
        {
            int index = this.GetIntOperand();
            int subIndex = this.GetIntOperand();

            float prevVal = _AssignSubscript(destList, index, subIndex, null, SubScriptOp.DEC);
            SafeOperandsPush(prevVal - 1);
        }

        private void _FPostDecSub(object[] destList)
        {
            int index = this.GetIntOperand();
            int subIndex = this.GetIntOperand();

            float prevVal = _AssignSubscript(destList, index, subIndex, null, SubScriptOp.DEC);
            SafeOperandsPush(prevVal);
        }

        private void Op_Fpreinc_l_sub()
        {
            _FPreIncSub(_state.TopFrame.Locals);
        }

        private void Op_Fpostinc_l_sub()
        {
            _FPostIncSub(_state.TopFrame.Locals);
        }

        private void Op_Fpredec_l_sub()
        {
            _FPreDecSub(_state.TopFrame.Locals);
        }

        private void Op_Fpostdec_l_sub()
        {
            _FPostDecSub(_state.TopFrame.Locals);
        }

        private void Op_Fpreinc_g_sub()
        {
            _FPreIncSub(_state.Globals);
        }

        private void Op_Fpostinc_g_sub()
        {
            _FPostIncSub(_state.Globals);
        }

        private void Op_Fpredec_g_sub()
        {
            _FPreDecSub(_state.Globals);
        }

        private void Op_Fpostdec_g_sub()
        {
            _FPostDecSub(_state.Globals);
        }

        private void Op_Ineg()
        {
            int i = ConvToInt(_state.Operands.Pop());
            SafeOperandsPush(-i);
        }

        private void Op_Ilnot()
        {
            int i = ConvToInt(_state.Operands.Pop());
            i = i == 0 ? 1 : 0;

            SafeOperandsPush(i);
        }

        private void Op_Ilor()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (b != 0 || a != 0)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Iland()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (b != 0 && a != 0)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Ilt()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (a < b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Igt()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (a > b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Ilte()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (a <= b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Igte()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (a >= b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Ieq()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (a == b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Ineq()
        {
            int b = ConvToInt(_state.Operands.Pop());
            int a = ConvToInt(_state.Operands.Pop());

            if (a != b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Fadd()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(a + b);
        }

        private void Op_Fsub()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(a - b);
        }

        private void Op_Fmul()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(a * b);
        }

        private void Op_Fdiv()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(a / b);
        }

        private void Op_Fneg()
        {
            float a = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(-a);
        }

        private void Op_Flt()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            if (a < b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Fgt()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            if (a > b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Flte()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            if (a <= b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Fgte()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            if (a >= b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Feq()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            if (a == b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Fneq()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            float a = ConvToFloat(_state.Operands.Pop());

            if (a != b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Vadd()
        {
            Vector3 b = (Vector3)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(a + b);
        }

        private void Op_Vsub()
        {
            Vector3 b = (Vector3)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(a - b);
        }

        private void Op_Vmul()
        {
            Vector3 b = (Vector3)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(Vector3.Dot(a, b));
        }

        private void Op_Vcross()
        {
            Vector3 b = (Vector3)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(a % b);
        }

        private void Op_Veq()
        {
            Vector3 b = (Vector3)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            if (a == b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Vneq()
        {
            Vector3 b = (Vector3)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            if (a != b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Sconcat()
        {
            string b = (string)_state.Operands.Pop();
            string a = (string)_state.Operands.Pop();

            SafeOperandsPush(a + b);
        }

        private void Op_Seq()
        {
            string b = (string)_state.Operands.Pop();
            string a = (string)_state.Operands.Pop();

            if (a == b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Sneq()
        {
            string b = (string)_state.Operands.Pop();
            string a = (string)_state.Operands.Pop();

            if (a != b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Radd()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Quaternion a = (Quaternion)_state.Operands.Pop();

            SafeOperandsPush(a + b);
        }

        private void Op_Rsub()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Quaternion a = (Quaternion)_state.Operands.Pop();

            SafeOperandsPush(a - b);
        }

        private Quaternion _QuatMul(Quaternion b, Quaternion a)
        {
            return Quaternion.Negate(a * b);
        }

        private void Op_Rmul()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Quaternion a = (Quaternion)_state.Operands.Pop();

            SafeOperandsPush(_QuatMul(a, b));
        }

        private void Op_Rdiv()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Quaternion a = (Quaternion)_state.Operands.Pop();

            Quaternion binv = new Quaternion(b.X, b.Y, b.Z, -b.W);
            SafeOperandsPush(Quaternion.Negate(_QuatMul(a, binv)));
        }

        private void Op_Req()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Quaternion a = (Quaternion)_state.Operands.Pop();

            if (a == b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private void Op_Rneq()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Quaternion a = (Quaternion)_state.Operands.Pop();

            if (a != b)
            {
                SafeOperandsPush(1);
            }
            else
            {
                SafeOperandsPush(0);
            }
        }

        private Vector3 _VrMul(Vector3 a, Quaternion b)
        {
            Quaternion vq = new Quaternion(a.X, a.Y, a.Z, 0);
            Quaternion nq = new Quaternion(-b.X, -b.Y, -b.Z, b.W);

            Quaternion result = _QuatMul(nq, _QuatMul(vq, b));

            return new Vector3(result.X, result.Y, result.Z);
        }

        private void Op_Vrmul()
        {
            Quaternion b = (Quaternion)_state.Operands.Pop();
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(_VrMul(a, b));
        }

        private void Op_Vimul()
        {
            int b = ConvToInt(_state.Operands.Pop());
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(a * (float)b);
        }

        private void Op_Vfmul()
        {
            float b = ConvToFloat(_state.Operands.Pop());
            Vector3 a = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(a * b);
        }

        private void Op_Pop()
        {
            _state.Operands.Pop();
        }

        private void Op_ListPrepend()
        {
            LSLList b = (LSLList)_state.Operands.Pop();
            object a = (object)_state.Operands.Pop();

            SafeOperandsPush(b.Prepend(a));
        }

        private void Op_ListAppend()
        {
            object b = _state.Operands.Pop();
            LSLList a = (LSLList)_state.Operands.Pop();

            SafeOperandsPush(a.Append(b));
        }

        private void Op_Jmp()
        {
            int index = this.GetIntOperand();
            _state.IP = index;
        }

        private void _Call(int funcIndex)
        {
            FunctionInfo fi = (FunctionInfo)_script.ConstPool[funcIndex];
            StackFrame f = new StackFrame(fi, _state.IP);

            // push new stack frame for parameters and locals
            if (f == null)
                throw new VMException("Attempt to push null call frame.");

            _state.Calls.Push(f); 

            // move args from operand stack to top frame on call stack
            for (int a = fi.NumberOfArguments - 1; a >= 0; a--) 
            { 
                f.Locals[a] = _state.Operands.Pop(); 
            }

            //tell the memory tracker about this call
            _state.MemInfo.AddCall(f);

            _state.TopFrame = f;
            _state.IP = fi.Address; // branch to function
        }

        private void Op_Call()
        {
            int constIndex = this.GetIntOperand();
            this._Call(constIndex);
        }

        private void Op_Ret()
        {
            StackFrame frame = _state.Calls.Pop();

            if (_state.Calls.Count > 0)
            {
                _state.TopFrame = _state.Calls.Peek();
            }
            else
            {
                _state.TopFrame = null;
            }


            _state.IP = frame.ReturnAddress;

            _state.MemInfo.CompleteCall(frame);

            // Value-call (callv) result adjustment: bring the produced return count to exactly the
            // caller's wanted count. Inert for named-call/event frames (Wanted == -1) -> existing
            // behavior is byte-for-byte unchanged.
            if (frame.Wanted >= 0)
            {
                int produced = _state.Operands.Count - frame.OperandBase;
                if (produced > frame.Wanted)
                    for (int i = 0; i < produced - frame.Wanted; i++) _state.Operands.Pop();
                else
                    for (int i = 0; i < frame.Wanted - produced; i++) SafeOperandsPush(LuaNil.Instance);
            }

            //return address is 0 indicates an event call
            if (frame.ReturnAddress == 0)
            {
                this.Op_Halt();
            }
        }

        private void Op_Syscall()
        {
            int syscallIndex = this.GetIntOperand();
            _syscallShim.Call(syscallIndex);
        }

        private void Op_Halt()
        {
            _state.RunState = RuntimeState.Status.Waiting;
        }

        private int _CastToInt(string s)
        {
            return Util.Encoding.CastToInt(s);
        }

        
        private float _CastToFloat(string s)
        {
            return Util.Encoding.CastToFloat(s);
        }

        private void Op_Icast()
        {
            object a = (object)_state.Operands.Pop();

            //the only valid types for an integer cast are
            //float, string, and integer
            if (a is string)
            {
                SafeOperandsPush(_CastToInt((string)a));
            }
            else if (a is float)
            {
                SafeOperandsPush((int)(float)a);
            }
            else if (a is int)
            {
                SafeOperandsPush((int)a);
            }
            else
            {
                throw new VMException("Invalid integer cast");
            }
        }

        private void Op_Fcast()
        {
            object a = (object)_state.Operands.Pop();

            //the only valid types for an float cast are
            //float, string, and integer
            if (a is string)
            {
                SafeOperandsPush(_CastToFloat((string)a));
            }
            else if (a is int)
            {
                SafeOperandsPush((float)(int)a);
            }
            else if (a is float)
            {
                SafeOperandsPush((float)a);
            }
            else
            {
                throw new VMException("Invalid floating point cast");
            }
        }

        private string _PrimitiveToString(object primitive)
        {
            //all types are valid for a string cast
            if (primitive is int)
            {
                return Convert.ToString((int)primitive);
            }
            else if (primitive is float)
            {
                return Util.Encoding.FloatToStringWith6FractionalDigits((float)primitive);
            }
            else if (primitive is Vector3)
            {
                Vector3 vPrimitive = (Vector3)primitive;
                return Util.Encoding.Vector3ToStringWith5FractionalDigits(vPrimitive);
            }
            else if (primitive is Quaternion)
            {
                Quaternion rPrimitive = (Quaternion)primitive;
                return Util.Encoding.QuaternionToStringWith5FractionalDigits(rPrimitive);
            }
            else if (primitive is string)
            {
                return (string)primitive;
            }
            else
            {
                throw new VMException("Invalid string cast");
            }
        }

        private string _LSLListToString(LSLList list)
        {
            StringBuilder contents = new StringBuilder();
            for (int index = 0; index < list.Data.Length; ++index)
            {
                contents.Append(list.GetLSLStringItem(index));
            }

            return contents.ToString();
        }

        private void Op_Scast()
        {
            object a = (object)_state.Operands.Pop();

            //all types are valid for a string cast
            if (a is int)
            {
                SafeOperandsPush(_PrimitiveToString(a));
            }
            else if (a is float)
            {
                SafeOperandsPush(_PrimitiveToString(a));
            }
            else if (a is Vector3)
            {
                SafeOperandsPush(_PrimitiveToString(a));
            }
            else if (a is Quaternion)
            {
                SafeOperandsPush(_PrimitiveToString(a));
            }
            else if (a is LSLList)
            {
                SafeOperandsPush(_LSLListToString((LSLList)a));
            }
            else if (a is string)
            {
                SafeOperandsPush((string)a);
            }
            else
            {
                throw new VMException("Invalid string cast");
            }
        }

        private void Op_Vcast()
        {
            object a = (object)_state.Operands.Pop();

            //only string and vector are valid for a vector cast
            if (a is string)
            {
                Vector3 ret;
                if (Vector3.TryParse((string)a, out ret))
                {
                    SafeOperandsPush(ret);
                }
                else
                {
                    SafeOperandsPush(Vector3.Zero);
                }
            }
            else if (a is Vector3)
            {
                SafeOperandsPush(a);
            }
            else
            {
                throw new VMException("Invalid vector cast");
            }
        }

        private void Op_Rcast()
        {
            object a = (object)_state.Operands.Pop();

            //only string and rotation are valid for a rotation cast
            if (a is string)
            {
                Quaternion ret;

                if (Quaternion.TryParse((string)a, out ret))
                {
                    SafeOperandsPush(ret);
                }
                else
                {
                    SafeOperandsPush(Quaternion.Identity);
                }
                
            }
            else if (a is Quaternion)
            {
                SafeOperandsPush(a);
            }
            else
            {
                throw new VMException("Invalid rotation cast");
            }
        }

        private void Op_Lcast()
        {
            object a = (object)_state.Operands.Pop();

            //anything can be casted to a list. This creates a new
            //list with a single element in it
            if (!(a is LSLList))
            {
                SafeOperandsPush(new LSLList(a));
            }
            else 
            {
                SafeOperandsPush(a);
            }
        }

        private void Op_BuildVec()
        {
            float z = ConvToFloat(_state.Operands.Pop());
            float y = ConvToFloat(_state.Operands.Pop());
            float x = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(new Vector3(x, y, z));
        }

        private void Op_BuildRot()
        {
            float w = ConvToFloat(_state.Operands.Pop());
            float z = ConvToFloat(_state.Operands.Pop());
            float y = ConvToFloat(_state.Operands.Pop());
            float x = ConvToFloat(_state.Operands.Pop());

            SafeOperandsPush(new Quaternion(x, y, z, w));
        }

        private void Op_BuildList()
        {
            int numMembers = this.GetIntOperand();

            object[] members = new object[numMembers];

            for (int i = numMembers - 1; i >= 0; --i)
            {
                members[i] = _state.Operands.Pop();
            }

            SafeOperandsPush(new LSLList(members));
        }

        // ============================================================
        // SLua Tier-2: table opcodes (additive; do not affect LSL ops)
        // ============================================================
        // SLua nil is the LuaNil sentinel (NOT .NET null, which SafeOperandsPush/_Load forbid).
        // Tables never store nil internally (a nil value removes the key); LuaNil only lives on the
        // stack / in slots.

        private static bool IsNilValue(object v)
        {
            return v == null || v is LuaNil;
        }

        private void Op_PushNil()
        {
            SafeOperandsPush(LuaNil.Instance);
        }

        private void Op_BuildTable()
        {
            int numPairs = this.GetIntOperand();
            int count = numPairs * 2;

            object[] kv = new object[count];
            for (int i = count - 1; i >= 0; --i)
            {
                kv[i] = _state.Operands.Pop();
            }

            LSLTable table = new LSLTable();
            for (int i = 0; i < count; i += 2)
            {
                // kv[i] = key, kv[i+1] = value, in source order; nil value => skip
                if (!IsNilValue(kv[i]) && !IsNilValue(kv[i + 1]))
                    table.Set(kv[i], kv[i + 1]);
            }

            SafeOperandsPush(table);
        }

        private void Op_TabGet()
        {
            object key = _state.Operands.Pop();
            object t = _state.Operands.Pop();

            // SLua vectors/rotations are immutable Luau values: read-only .x/.y/.z (and .s on a rotation).
            if (t is Vector3 vec)
            {
                SafeOperandsPush(VecComponent(vec, key));
                return;
            }
            if (t is Quaternion q)
            {
                SafeOperandsPush(RotComponent(q, key));
                return;
            }

            if (!(t is LSLTable table))
                throw new CheckException("attempt to index a non-table value");

            object v = MetaTableGet(table, key);          // __index fallback (table/function), else raw
            SafeOperandsPush(v ?? (object)LuaNil.Instance); // missing key -> nil
        }

        private static object VecComponent(Vector3 v, object key)
        {
            string k = key as string;
            switch (k)
            {
                case "x": return v.X;
                case "y": return v.Y;
                case "z": return v.Z;
                default: throw new CheckException("vector has no component '" + (k ?? "?") + "' (use .x/.y/.z)");
            }
        }

        private static object RotComponent(Quaternion q, object key)
        {
            string k = key as string;
            switch (k)
            {
                case "x": return q.X;
                case "y": return q.Y;
                case "z": return q.Z;
                case "s": return q.W;   // LSL rotation scalar component is '.s'
                default: throw new CheckException("rotation has no component '" + (k ?? "?") + "' (use .x/.y/.z/.s)");
            }
        }

        private void Op_TabSet()
        {
            object value = _state.Operands.Pop();
            object key = _state.Operands.Pop();
            object t = _state.Operands.Pop();
            if (!(t is LSLTable table))
                throw new CheckException("attempt to index a non-table value");

            MetaTableSet(table, key, IsNilValue(value) ? null : value); // __newindex on absent key, else raw
        }

        private void Op_TabLen()
        {
            object t = _state.Operands.Pop();
            if (t is string s) { SafeOperandsPush((float)s.Length); return; } // Luau: #s = string length
            if (!(t is LSLTable table))
                throw new CheckException("attempt to get length of a non-table value");

            object lh = MetaRaw(table, "__len");
            if (lh is LuaClosure cl) { SafeOperandsPush(InvokeClosureSync(cl, new object[] { table }) ?? (object)LuaNil.Instance); return; }
            SafeOperandsPush(table.Length);
        }

        private void Op_TabNext()
        {
            object key = _state.Operands.Pop();
            object t = _state.Operands.Pop();
            if (!(t is LSLTable table))
                throw new CheckException("attempt to iterate a non-table value");

            object nk, nv;
            table.Next(IsNilValue(key) ? null : key, out nk, out nv);
            // push value then key (key on top); end-of-iteration pushes nil for both
            SafeOperandsPush(nv ?? (object)LuaNil.Instance);
            SafeOperandsPush(nk ?? (object)LuaNil.Instance);
        }

        private void Op_IsNil()
        {
            object v = _state.Operands.Pop();
            SafeOperandsPush(IsNilValue(v) ? 1 : 0);
        }

        // ============================================================
        // SLua Tier-2: dynamic typing (additive; Lua boolean = boxed .NET bool)
        // ============================================================
        // Lua truthiness: ONLY nil and false are falsy; everything else (incl. 0, "", tables) true.
        private static bool LuaIsTruthy(object v)
        {
            if (IsNilValue(v)) return false;
            if (v is bool b) return b;
            return true;
        }

        private static bool IsLuaNumber(object v) { return v is int || v is float; }

        private void Op_LuaTruthy()
        {
            object v = _state.Operands.Pop();
            SafeOperandsPush(LuaIsTruthy(v) ? 1 : 0);   // int for brf/brt
        }

        private void Op_LNot()
        {
            object v = _state.Operands.Pop();
            SafeOperandsPush(!LuaIsTruthy(v));          // boxed bool
        }

        private void Op_ToBool()
        {
            int i = ConvToInt(_state.Operands.Pop());
            SafeOperandsPush(i != 0);                   // relational int result -> boolean
        }

        private void Op_Dup()
        {
            SafeOperandsPush(_state.Operands.Peek());
        }

        private void Op_LuaEq()
        {
            object b = _state.Operands.Pop();
            object a = _state.Operands.Pop();
            // __eq fires only when both are distinct tables (Lua semantics); otherwise raw equality.
            if (a is LSLTable && b is LSLTable && !ReferenceEquals(a, b))
            {
                object h = MetaRaw(a, "__eq") ?? MetaRaw(b, "__eq");
                if (h is LuaClosure cl) { SafeOperandsPush(LuaIsTruthy(InvokeClosureSync(cl, new object[] { a, b }))); return; }
            }
            SafeOperandsPush(LuaEquals(a, b));          // boxed bool
        }

        // Lua ==: different types are never equal; numbers compare by value; tables by identity.
        private static bool LuaEquals(object a, object b)
        {
            bool aNil = IsNilValue(a), bNil = IsNilValue(b);
            if (aNil || bNil) return aNil && bNil;
            if (a is bool ab && b is bool bb) return ab == bb;
            if (IsLuaNumber(a) && IsLuaNumber(b)) return ConvToFloat(a) == ConvToFloat(b);
            if (a is string sa && b is string sb) return sa == sb;
            if (a is Vector3 va && b is Vector3 vb) return va == vb;       // SL: by value
            if (a is Quaternion qa && b is Quaternion qb) return qa == qb; // SL: by value
            if (a is LSLTable && b is LSLTable) return ReferenceEquals(a, b);
            return false;
        }

        private void Op_Concat()
        {
            object b = _state.Operands.Pop();
            object a = _state.Operands.Pop();
            if (a is LSLTable || b is LSLTable)
            {
                object h = MetaRaw(a, "__concat") ?? MetaRaw(b, "__concat");
                if (h is LuaClosure cl) { SafeOperandsPush(InvokeClosureSync(cl, new object[] { a, b }) ?? (object)LuaNil.Instance); return; }
            }
            SafeOperandsPush(ConcatStr(a) + ConcatStr(b));
        }

        // Lua '..' coerces only numbers and strings; other types error.
        private static string ConcatStr(object v)
        {
            if (v is string s) return s;
            if (IsLuaNumber(v)) return LuaNumToStr(v);
            throw new CheckException("attempt to concatenate a " + LuaTypeName(v) + " value");
        }

        private void Op_LuaType()
        {
            SafeOperandsPush(LuaTypeName(_state.Operands.Pop()));
        }

        private static string LuaTypeName(object v)
        {
            if (IsNilValue(v)) return "nil";
            if (v is bool) return "boolean";
            if (IsLuaNumber(v)) return "number";
            if (v is string) return "string";
            if (v is Vector3) return "vector";
            if (v is Quaternion) return "rotation";
            if (v is LSLTable) return "table";
            if (v is FunctionInfo) return "function";
            return "userdata";
        }

        private void Op_LuaToStr()
        {
            object v = _state.Operands.Pop();
            object h = MetaRaw(v, "__tostring");
            if (h is LuaClosure cl)
            {
                object r = InvokeClosureSync(cl, new object[] { v });
                SafeOperandsPush(r is string s ? s : LuaToString(r));
                return;
            }
            SafeOperandsPush(LuaToString(v));
        }

        private static string LuaToString(object v)
        {
            if (IsNilValue(v)) return "nil";
            if (v is bool b) return b ? "true" : "false";
            if (IsLuaNumber(v)) return LuaNumToStr(v);
            if (v is string s) return s;
            // SL string form: <x, y, z> / <x, y, z, s> at 5 fractional digits (matches LSL string-cast).
            if (v is Vector3 vec) return Util.Encoding.Vector3ToStringWith5FractionalDigits(vec);
            if (v is Quaternion q) return Util.Encoding.QuaternionToStringWith5FractionalDigits(q);
            if (v is LSLTable) return "table";
            if (v is FunctionInfo) return "function";
            return v.ToString();
        }

        // Luau prints integral numbers without a fraction (5.0 -> "5", 2.5 -> "2.5").
        private static string LuaNumToStr(object v)
        {
            double d = (v is int i) ? i : (float)v;
            if (d == System.Math.Floor(d) && !double.IsInfinity(d))
                return ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture);
            return d.ToString("0.0###############", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void Op_LuaToNum()
        {
            object v = _state.Operands.Pop();
            if (IsLuaNumber(v)) { SafeOperandsPush(v); return; }
            if (v is string s && float.TryParse(s, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out float f))
            {
                SafeOperandsPush(f); return;
            }
            SafeOperandsPush(LuaNil.Instance); // tonumber() returns nil on failure
        }

        // SLua stdlib dispatch: operands [funcid, argc]; pops argc args, pushes 1 result.
        private void Op_LuaCall()
        {
            int funcId = this.GetIntOperand();
            int argc = this.GetIntOperand();
            object[] args = new object[argc];
            for (int i = argc - 1; i >= 0; --i) args[i] = _state.Operands.Pop();
            object result = SLua.LuaLib.Call(funcId, args);
            SafeOperandsPush(result ?? (object)LuaNil.Instance);
        }

        // Multi-result stdlib (find/match/gsub): pop argc args; push N results then push N (count).
        private void Op_LuaCallM()
        {
            int funcId = this.GetIntOperand();
            int argc = this.GetIntOperand();
            object[] args = new object[argc];
            for (int i = argc - 1; i >= 0; --i) args[i] = _state.Operands.Pop();

            // gsub with a function replacement: invoke the closure per match (re-entrant, safe -
            // gsub is atomic so the VM never serializes mid-callback).
            if (funcId == (int)SLua.LuaLib.Func.StrGsub && argc >= 3 && args[2] is LuaClosure clr)
            {
                string s = LuaToString(args[0]);
                string p = LuaToString(args[1]);
                int maxN = (argc >= 4 && args[3] != null && !(args[3] is LuaNil)) ? ConvToInt(args[3]) : int.MaxValue;
                string result; int count;
                SLua.LuaPattern.GSubFunc(s, p, caps => this.InvokeClosureSync(clr, caps.ToArray()), maxN, out result, out count);
                _state.Operands.Push(result);
                SafeOperandsPush(count);
                SafeOperandsPush(2); // value count (result, count)
                return;
            }

            object[] res = SLua.LuaLib.CallMulti(funcId, args);
            for (int i = 0; i < res.Length; i++) _state.Operands.Push(res[i] ?? (object)LuaNil.Instance);
            SafeOperandsPush(res.Length); // runtime count on top
        }

        // Reconcile a runtime-counted value group to exactly T values. Operand T; pops the count.
        private void Op_AdjustM()
        {
            int target = this.GetIntOperand();
            int k = ConvToInt(_state.Operands.Pop());
            if (k > target) for (int i = 0; i < k - target; i++) _state.Operands.Pop();
            else for (int i = 0; i < target - k; i++) SafeOperandsPush(LuaNil.Instance);
        }

        // gmatch iteration step. Operand K = number of for-loop variables.
        // Pop the LuaGmatch, advance it; on a match push K captures (pad/truncate) then int 1;
        // when exhausted push only int 0.
        private void Op_GmatchNext()
        {
            int k = this.GetIntOperand();
            object o = _state.Operands.Pop();
            if (!(o is LuaGmatch gm))
                throw new CheckException("gmatch iterator expected");

            int pos = gm.Pos;
            var caps = SLua.LuaPattern.GMatchStep(gm.Src, gm.Pat, ref pos);
            gm.Pos = pos; // mutate in place (the slot keeps the same reference)

            if (caps == null) { SafeOperandsPush(0); return; }
            for (int i = 0; i < k; i++)
                _state.Operands.Push(i < caps.Count ? (caps[i] ?? (object)LuaNil.Instance) : LuaNil.Instance);
            SafeOperandsPush(1);
        }

        // ============================================================
        // SLua Tier-2: closures / first-class functions
        // ============================================================
        private void Op_MkCell()
        {
            object v = _state.Operands.Pop();
            SafeOperandsPush(new UpvalCell(v));
        }

        private void Op_CellGet()
        {
            object o = _state.Operands.Pop();
            if (!(o is UpvalCell c)) throw new CheckException("internal: cellget on non-cell");
            _state.Operands.Push(c.Value ?? (object)LuaNil.Instance);
        }

        private void Op_CellPut()
        {
            object v = _state.Operands.Pop();
            object o = _state.Operands.Pop();
            if (!(o is UpvalCell c)) throw new CheckException("internal: cellput on non-cell");
            c.Value = v;
        }

        private void Op_GetUpval()
        {
            int i = this.GetIntOperand();
            LuaClosure cl = _state.TopFrame.Closure;
            if (cl == null || cl.Upvals == null || i >= cl.Upvals.Length)
                throw new CheckException("internal: bad upvalue access");
            _state.Operands.Push(cl.Upvals[i].Value ?? (object)LuaNil.Instance);
        }

        private void Op_SetUpval()
        {
            int i = this.GetIntOperand();
            object v = _state.Operands.Pop();
            _state.TopFrame.Closure.Upvals[i].Value = v;
        }

        private void Op_PushUpval()
        {
            int i = this.GetIntOperand();
            SafeOperandsPush(_state.TopFrame.Closure.Upvals[i]); // the cell (for transitive capture)
        }

        private void Op_MkClosure()
        {
            int funcIndex = this.GetIntOperand();
            int nups = this.GetIntOperand();
            FunctionInfo fi = (FunctionInfo)_script.ConstPool[funcIndex];
            UpvalCell[] ups = new UpvalCell[nups];
            for (int i = nups - 1; i >= 0; --i) ups[i] = (UpvalCell)_state.Operands.Pop();
            SafeOperandsPush(new LuaClosure(fi, ups));
        }

        private void Op_CallV()
        {
            int argc = this.GetIntOperand();
            int wanted = this.GetIntOperand();
            object[] argv = new object[argc];
            for (int i = argc - 1; i >= 0; --i) argv[i] = _state.Operands.Pop();
            object cv = _state.Operands.Pop();
            if (!(cv is LuaClosure cl))
            {
                // __call: a table value is callable if its metatable has __call(self, ...).
                object h = MetaRaw(cv, "__call");
                if (h is LuaClosure ccl)
                {
                    object[] cargs = new object[argc + 1];
                    cargs[0] = cv;
                    for (int i = 0; i < argc; i++) cargs[i + 1] = argv[i];
                    object cr = InvokeClosureSync(ccl, cargs);
                    if (wanted != 0) SafeOperandsPush(cr ?? (object)LuaNil.Instance);
                    return;
                }
                throw new CheckException("attempt to call a non-function value");
            }

            FunctionInfo fi = cl.Fn;
            StackFrame f = new StackFrame(fi, _state.IP);
            f.Closure = cl;
            f.Wanted = wanted;
            f.OperandBase = _state.Operands.Count; // base for return-value count
            _state.Calls.Push(f);
            for (int i = 0; i < fi.NumberOfArguments; i++)
                f.Locals[i] = (i < argc) ? argv[i] : (object)LuaNil.Instance;
            _state.MemInfo.AddCall(f);
            _state.TopFrame = f;
            _state.IP = fi.Address;
        }

        /// <summary>
        /// Synchronously invoke a closure from C# (used by gsub function-replacement). Safe because
        /// gsub runs atomically as a single opcode -- the VM never yields/serializes mid-callback, so
        /// the C# call stack is never captured. Returns the closure's first return value (nil if none).
        /// </summary>
        public object InvokeClosureSync(LuaClosure cl, object[] args)
        {
            int baseDepth = _state.Calls.Count;
            int stackBefore = _state.Operands.Count;
            int savedIP = _state.IP;

            FunctionInfo fi = cl.Fn;
            StackFrame f = new StackFrame(fi, savedIP);
            f.Closure = cl;
            _state.Calls.Push(f);
            for (int i = 0; i < fi.NumberOfArguments; i++)
                f.Locals[i] = (i < args.Length) ? args[i] : (object)LuaNil.Instance;
            _state.MemInfo.AddCall(f);
            _state.TopFrame = f;
            _state.IP = fi.Address;

            int guard = 0;
            while (_state.Calls.Count > baseDepth && guard++ < 50000000)
                Tick();

            _state.IP = savedIP; // resume the outer opcode

            int produced = _state.Operands.Count - stackBefore;
            object result = produced > 0 ? _state.Operands.Pop() : (object)LuaNil.Instance;
            for (int i = 1; i < produced; i++) _state.Operands.Pop(); // discard extra returns
            return result;
        }

        // ============================================================
        // SLua Tier-2: LLEvents:on / DetectedEvent
        // ============================================================
        private static readonly HashSet<string> _detectionEvents = new HashSet<string>
        {
            "touch_start", "touch", "touch_end", "collision_start", "collision", "collision_end", "sensor"
        };
        private static Dictionary<string, int> _detMethods;

        private static int DetectedMethodIndex(string m)
        {
            if (_detMethods == null)
            {
                var map = new Dictionary<string, int>();
                void Add(string method, string ll) { if (Defaults.SystemMethods.TryGetValue(ll, out var s)) map[method] = s.TableIndex; }
                Add("getKey", "llDetectedKey"); Add("getName", "llDetectedName"); Add("getPos", "llDetectedPos");
                Add("getOwner", "llDetectedOwner"); Add("getGroup", "llDetectedGroup"); Add("getType", "llDetectedType");
                Add("getVel", "llDetectedVel"); Add("getRot", "llDetectedRot"); Add("getLinkNumber", "llDetectedLinkNumber");
                _detMethods = map;
            }
            return _detMethods.TryGetValue(m, out var idx) ? idx : -1;
        }

        // LLEvents:on -> append a handler closure to registry[eventName] (a list table).
        private void Op_RegEvent()
        {
            object fn = _state.Operands.Pop();
            object ev = _state.Operands.Pop();
            object reg = _state.Operands.Pop();
            if (!(reg is LSLTable registry)) throw new CheckException("internal: LLEvents registry is not a table");
            string evName = (ev as string) ?? (ev == null ? "" : ev.ToString());
            LSLTable list = registry.Get(evName) as LSLTable;
            if (list == null) { list = new LSLTable(); registry.Set(evName, list); }
            list.Set(list.Length + 1, fn); // append (1-based)
        }

        // obj:method(args). DetectedEvent -> native (llDetected*); LSLTable -> method-as-field closure.
        private void Op_MethCall()
        {
            int methIdx = this.GetIntOperand();
            int argc = this.GetIntOperand();
            string method = (string)_script.ConstPool[methIdx];
            object[] args = new object[argc];
            for (int i = argc - 1; i >= 0; --i) args[i] = _state.Operands.Pop();
            object recv = _state.Operands.Pop();

            if (recv is LuaDetected det)
            {
                int tableIndex = DetectedMethodIndex(method);
                if (tableIndex < 0) throw new CheckException("DetectedEvent has no method '" + method + "' in the Tier-2 subset");
                SafeOperandsPush(det.Index);     // detection index
                _syscallShim.Call(tableIndex);   // shim reads index, calls llDetected*, pushes result
                return;
            }
            if (recv is LSLTable tbl)
            {
                // Method lookup honors __index, so class methods (OOP) resolve through the metatable.
                if (!(MetaTableGet(tbl, method) is LuaClosure cl))
                    throw new CheckException("attempt to call method '" + method + "' (not a function)");
                object[] callArgs = new object[argc + 1];
                callArgs[0] = recv;              // self
                for (int i = 0; i < argc; i++) callArgs[i + 1] = args[i];
                SafeOperandsPush(InvokeClosureSync(cl, callArgs) ?? (object)LuaNil.Instance);
                return;
            }
            throw new CheckException("attempt to call a method on a non-object value");
        }

        // Dispatcher: invoke all LLEvents:on handlers registered for an event. Detection events get
        // an array of DetectedEvent; other events get the raw scalar event args. Runs synchronously
        // during the event (detection context live), so DetectedEvent methods read live data.
        private void Op_FireLLEvents()
        {
            int evIdx = this.GetIntOperand();
            int argc = this.GetIntOperand();
            string evName = (string)_script.ConstPool[evIdx];
            object[] evArgs = new object[argc];
            for (int i = argc - 1; i >= 0; --i) evArgs[i] = _state.Operands.Pop();
            object reg = _state.Operands.Pop();
            if (!(reg is LSLTable registry)) return;
            if (!(registry.Get(evName) is LSLTable list)) return;

            object[] handlerArgs;
            if (_detectionEvents.Contains(evName))
            {
                int num = (evArgs.Length > 0) ? ConvToInt(evArgs[0]) : 0;
                LSLTable arr = new LSLTable();
                for (int i = 0; i < num; i++) arr.Set(i + 1, new LuaDetected(i)); // 1-based DetectedEvent array
                handlerArgs = new object[] { arr };
            }
            else
            {
                handlerArgs = evArgs;
            }

            int n = list.Length;
            for (int i = 1; i <= n; i++)
                if (list.Get(i) is LuaClosure cl) InvokeClosureSync(cl, handlerArgs);
        }

        // ============================================================
        // SLua Tier-2: metatables
        // ============================================================
        // Cheap metamethod read: only tables WITH a metatable can carry one (null = fast path).
        private static object MetaRaw(object v, string mm)
        {
            return (v is LSLTable t && t.Metatable != null) ? t.Metatable.Get(mm) : null;
        }

        // __index lookup: raw hit wins; else follow the metatable's __index (table form chains,
        // function form is invoked). Bounded to defend against cyclic __index tables.
        private object MetaTableGet(LSLTable t, object key)
        {
            for (int depth = 0; depth < 100; depth++)
            {
                object raw = t.Get(key);
                if (raw != null) return raw;
                LSLTable mt = t.Metatable;
                if (mt == null) return null;
                object idx = mt.Get("__index");
                if (idx == null) return null;
                if (idx is LSLTable idxTable) { t = idxTable; continue; }           // chain through table
                if (idx is LuaClosure cl) return InvokeClosureSync(cl, new object[] { t, key });
                return null;
            }
            throw new CheckException("'__index' chain too long (possible loop)");
        }

        // __newindex on assignment to an ABSENT key: table form re-targets, function form is invoked;
        // an existing key (or no __newindex) is a raw set. Bounded like __index.
        private void MetaTableSet(LSLTable t, object key, object value)
        {
            for (int depth = 0; depth < 100; depth++)
            {
                if (t.Get(key) != null) { t.Set(key, value); return; }              // existing key -> raw set
                LSLTable mt = t.Metatable;
                object ni = (mt != null) ? mt.Get("__newindex") : null;
                if (ni == null) { t.Set(key, value); return; }                      // no __newindex -> raw set
                if (ni is LSLTable niTable) { t = niTable; continue; }              // table form -> assign there
                if (ni is LuaClosure cl) { InvokeClosureSync(cl, new object[] { t, key, value }); return; }
                t.Set(key, value); return;
            }
            throw new CheckException("'__newindex' chain too long (possible loop)");
        }

        // Selector -> metamethod name. gt/ge (7/8) reuse __lt/__le with swapped operands (Lua semantics).
        private static readonly string[] _binopMeta =
            { "__add", "__sub", "__mul", "__div", "__mod", "__lt", "__le", "__lt", "__le" };

        private void Op_LuaBinop()
        {
            int sel = this.GetIntOperand();
            object b = _state.Operands.Pop();
            object a = _state.Operands.Pop();

            if (a is LSLTable || b is LSLTable)
            {
                bool swap = (sel == 7 || sel == 8);     // gt/ge -> __lt/__le(b, a)
                object ha = swap ? b : a, hb = swap ? a : b;
                object h = MetaRaw(ha, _binopMeta[sel]) ?? MetaRaw(hb, _binopMeta[sel]);
                if (h is LuaClosure cl)
                {
                    object r = InvokeClosureSync(cl, new object[] { ha, hb });
                    SafeOperandsPush(sel >= 5 ? (object)LuaIsTruthy(r) : (r ?? (object)LuaNil.Instance));
                    return;
                }
                throw new CheckException("attempt to perform arithmetic/comparison on a table value (no metamethod)");
            }

            // SL-faithful vector/rotation arithmetic (matches the existing LSL vector opcodes).
            if (a is Vector3 || b is Vector3 || a is Quaternion || b is Quaternion)
            {
                SafeOperandsPush(VectorBinop(sel, a, b));
                return;
            }

            float fa = ConvToFloat(a), fb = ConvToFloat(b);
            switch (sel)
            {
                case 0: SafeOperandsPush(fa + fb); break;
                case 1: SafeOperandsPush(fa - fb); break;
                case 2: SafeOperandsPush(fa * fb); break;
                case 3: SafeOperandsPush(fa / fb); break;
                case 4: SafeOperandsPush(fa % fb); break;
                case 5: SafeOperandsPush(fa < fb); break;   // boxed bool
                case 6: SafeOperandsPush(fa <= fb); break;
                case 7: SafeOperandsPush(fa > fb); break;
                case 8: SafeOperandsPush(fa >= fb); break;
                default: throw new CheckException("bad luabinop selector " + sel);
            }
        }

        // SL-faithful vector/rotation operators (mirrors the LSL vector opcodes exactly):
        //  v+v / v-v component-wise; v*v = DOT (scalar); v%v = cross; v*scalar / scalar*v / v/scalar scale;
        //  v*rot rotate, v/rot inverse-rotate; rot*rot quat-mul, rot/rot rdiv; == by value.
        private object VectorBinop(int sel, object a, object b)
        {
            switch (sel)
            {
                case 0: // +
                    if (a is Vector3 av0 && b is Vector3 bv0) return av0 + bv0;
                    break;
                case 1: // -
                    if (a is Vector3 av1 && b is Vector3 bv1) return av1 - bv1;
                    break;
                case 2: // *
                    if (a is Vector3 av2)
                    {
                        if (b is Vector3 bv2) return Vector3.Dot(av2, bv2);     // dot -> float
                        if (b is Quaternion bq2) return _VrMul(av2, bq2);       // rotate
                        if (IsLuaNumber(b)) return av2 * ConvToFloat(b);        // scale
                    }
                    else if (a is Quaternion aq2)
                    {
                        if (b is Quaternion bq) return _QuatMul(aq2, bq);       // quat mul
                    }
                    else if (IsLuaNumber(a) && b is Vector3 bvs) return bvs * ConvToFloat(a); // scalar * vector
                    break;
                case 3: // /
                    if (a is Vector3 av3)
                    {
                        if (b is Quaternion bq3) { bq3.W = -bq3.W; return _VrMul(av3, bq3); } // inverse-rotate
                        if (IsLuaNumber(b)) return av3 / ConvToFloat(b);                       // scale
                    }
                    else if (a is Quaternion aq3 && b is Quaternion bq3b)
                    {
                        Quaternion binv = new Quaternion(bq3b.X, bq3b.Y, bq3b.Z, -bq3b.W);
                        return Quaternion.Negate(_QuatMul(aq3, binv));
                    }
                    break;
                case 4: // % -> cross (vectors)
                    if (a is Vector3 av4 && b is Vector3 bv4) return av4 % bv4;
                    break;
            }
            throw new CheckException("unsupported vector/rotation operation");
        }

        private void Op_LuaUnm()
        {
            object a = _state.Operands.Pop();
            if (a is Vector3 vneg) { SafeOperandsPush(-vneg); return; }
            if (a is Quaternion rneg) { SafeOperandsPush(-rneg); return; }
            if (a is LSLTable)
            {
                object h = MetaRaw(a, "__unm");
                if (h is LuaClosure cl) { SafeOperandsPush(InvokeClosureSync(cl, new object[] { a, a }) ?? (object)LuaNil.Instance); return; }
                throw new CheckException("attempt to negate a table value (no __unm)");
            }
            SafeOperandsPush(-ConvToFloat(a));
        }

        private void Op_SetMeta()
        {
            object mt = _state.Operands.Pop();
            object t = _state.Operands.Pop();
            if (!(t is LSLTable table))
                throw new CheckException("setmetatable: first argument must be a table");
            table.Metatable = IsNilValue(mt) ? null : (mt as LSLTable);
            SafeOperandsPush(table); // setmetatable returns the table
        }

        private void Op_GetMeta()
        {
            object t = _state.Operands.Pop();
            if (t is LSLTable table && table.Metatable != null) { SafeOperandsPush(table.Metatable); return; }
            SafeOperandsPush(LuaNil.Instance);
        }

        // ============================================================
        // SLua conformance pass: error handling (error/pcall) + table.sort
        // ============================================================
        private void Op_LuaError()
        {
            object v = _state.Operands.Pop();
            throw new LuaError(IsNilValue(v) ? null : v);
        }

        // pcall(f, ...): run f protected. Pushes exactly two values: (true, firstResult) on success,
        // (false, errValue) on a script error. (First-result only is a documented minor simplification
        // vs Luau's full multi-return -- covers the canonical `local ok, r = pcall(...)`.)
        private void Op_LuaPcall()
        {
            int argc = this.GetIntOperand();
            object[] argv = new object[argc];
            for (int i = argc - 1; i >= 0; --i) argv[i] = _state.Operands.Pop();
            object f = _state.Operands.Pop();

            int baseCalls = _state.Calls.Count;
            int baseStack = _state.Operands.Count;
            int savedIP = _state.IP;
            StackFrame savedTop = _state.TopFrame;

            try
            {
                if (!(f is LuaClosure cl)) throw new LuaError("attempt to call a non-function value");
                object res = InvokeClosureSync(cl, argv);
                SafeOperandsPush(true);
                SafeOperandsPush(res ?? (object)LuaNil.Instance);
            }
            catch (LuaError le)      { PcallUnwind(baseCalls, baseStack, savedIP, savedTop); SafeOperandsPush(false); SafeOperandsPush(le.Value ?? (object)LuaNil.Instance); }
            catch (CheckException ce){ PcallUnwind(baseCalls, baseStack, savedIP, savedTop); SafeOperandsPush(false); SafeOperandsPush(ce.Message ?? "error"); }
            catch (VMException ve)   { PcallUnwind(baseCalls, baseStack, savedIP, savedTop); SafeOperandsPush(false); SafeOperandsPush(ve.Message ?? "error"); }
        }

        // Restore VM state after a trapped error: discard frames/operands created during the protected
        // call (balancing MemInfo as Op_Ret does), and reset IP/TopFrame to the pcall site.
        private void PcallUnwind(int baseCalls, int baseStack, int savedIP, StackFrame savedTop)
        {
            while (_state.Calls.Count > baseCalls) _state.MemInfo.CompleteCall(_state.Calls.Pop());
            while (_state.Operands.Count > baseStack) _state.Operands.Pop();
            _state.IP = savedIP;
            _state.TopFrame = savedTop;
        }

        // table.sort(t [, comp]): in-place ascending sort of the array part; comp(a,b) is a closure
        // returning truthy when a should come before b (else default Lua '<'). Pushes the table back.
        private void Op_LuaSort()
        {
            object cmp = _state.Operands.Pop();
            object t = _state.Operands.Pop();
            if (!(t is LSLTable table)) throw new CheckException("table.sort: table expected");

            int n = table.Length;
            var items = new List<object>(n);
            for (int i = 1; i <= n; i++) items.Add(table.Get(i));

            LuaClosure comp = cmp as LuaClosure;
            Comparison<object> less;
            if (comp != null)
                less = (a, b) =>
                {
                    if (LuaIsTruthy(InvokeClosureSync(comp, new object[] { a, b }))) return -1;
                    if (LuaIsTruthy(InvokeClosureSync(comp, new object[] { b, a }))) return 1;
                    return 0;
                };
            else
                less = (a, b) => DefaultLuaCompare(a, b);

            items.Sort(less);
            for (int i = 0; i < n; i++) table.Set(i + 1, items[i]);
            SafeOperandsPush(table);
        }

        // Default order for table.sort with no comparator: numbers by value, strings lexicographically.
        private static int DefaultLuaCompare(object a, object b)
        {
            if (IsLuaNumber(a) && IsLuaNumber(b)) return ConvToFloat(a).CompareTo(ConvToFloat(b));
            if (a is string sa && b is string sb) return string.CompareOrdinal(sa, sb);
            throw new CheckException("table.sort: attempt to compare incompatible values");
        }

        private void Op_Trace()
        {
            object top = _state.Operands.Pop();

            _traceDestination.WriteLine(top.ToString());
        }

        private void Op_Brt()
        {
            int result = ConvToInt(_state.Operands.Pop());

            if (result != 0)
            {
                int branchAddress = this.GetIntOperand();
                _state.IP = branchAddress;
            }
            else
            {
                this.DiscardIntOperand();
            }
        }

        private void Op_Brf()
        {
            int result = ConvToInt(_state.Operands.Pop());

            if (result == 0)
            {
                int branchAddress = this.GetIntOperand();
                _state.IP = branchAddress;
            }
            else
            {
                this.DiscardIntOperand();
            }
        }

        private void Op_StateChg()
        {
            int stateId = this.GetIntOperand();
            this.OnStateChg(this, stateId);
            _syscallShim.OnStateChange();
        }

        private void Op_Vneg()
        {
            Vector3 v = (Vector3)_state.Operands.Pop();
            SafeOperandsPush(-v);
        }

        private void Op_Rneg()
        {
            Quaternion r = (Quaternion)_state.Operands.Pop();
            SafeOperandsPush(-r);
        }

        private void Op_Ibunot()
        {
            int i = ConvToInt(_state.Operands.Pop());
            SafeOperandsPush(~i);
        }

        private void Op_Vidiv()
        {
            int rhs = ConvToInt(_state.Operands.Pop());
            Vector3 lhs = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(lhs / rhs);
        }

        private void Op_Vfdiv()
        {
            float rhs = ConvToFloat(_state.Operands.Pop());
            Vector3 lhs = (Vector3)_state.Operands.Pop();

            SafeOperandsPush(lhs / rhs);
        }

        private void Op_Vrdiv()
        {
            Quaternion rhs = (Quaternion)_state.Operands.Pop();
            Vector3 lhs = (Vector3)_state.Operands.Pop();

            rhs.W = -rhs.W;

            SafeOperandsPush(_VrMul(lhs, rhs));
        }

        private void Op_Leq()
        {
            LSLList rhs = (LSLList)_state.Operands.Pop();
            LSLList lhs = (LSLList)_state.Operands.Pop();

            SafeOperandsPush(lhs.Members.Count == rhs.Members.Count ? 1 : 0);
        }

        private void Op_Iinit_g()
        {
            int gidx = this.GetIntOperand();
            
            int newVal = 0;
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Finit_g()
        {
            int gidx = this.GetIntOperand();

            float newVal = 0.0f;
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Vinit_g()
        {
            int gidx = this.GetIntOperand();

            Vector3 newVal = Vector3.Zero;
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Rinit_g()
        {
            int gidx = this.GetIntOperand();

            Quaternion newVal = Quaternion.Identity;
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Sinit_g()
        {
            int gidx = this.GetIntOperand();

            string newVal = String.Empty;
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Linit_g()
        {
            int gidx = this.GetIntOperand();

            LSLList newVal = new LSLList();
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Iinit_l()
        {
            int lidx = this.GetIntOperand();

            int newVal = 0;
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Finit_l()
        {
            int lidx = this.GetIntOperand();

            float newVal = 0.0f;
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Vinit_l()
        {
            int lidx = this.GetIntOperand();

            Vector3 newVal = Vector3.Zero;
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Rinit_l()
        {
            int lidx = this.GetIntOperand();

            Quaternion newVal = Quaternion.Identity;
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Sinit_l()
        {
            int lidx = this.GetIntOperand();

            string newVal = String.Empty;
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Linit_l()
        {
            int lidx = this.GetIntOperand();

            LSLList newVal = new LSLList();
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Lneq()
        {
            LSLList rhs = (LSLList)_state.Operands.Pop();
            LSLList lhs = (LSLList)_state.Operands.Pop();

            SafeOperandsPush(lhs.Members.Count == rhs.Members.Count ? 0 : 1);
        }

        private const string ZERO_GUID = "00000000-0000-0000-0000-000000000000";

        private void Op_Kinit_g()
        {
            int gidx = this.GetIntOperand();

            string newVal = ZERO_GUID;
            _state.MemInfo.ReplaceStored(_state.Globals[gidx], newVal);

            _state.Globals[gidx] = newVal;
        }

        private void Op_Kinit_l()
        {
            int lidx = this.GetIntOperand();

            string newVal = ZERO_GUID;
            _state.MemInfo.ReplaceStored(_state.TopFrame.Locals[lidx], newVal);

            _state.TopFrame.Locals[lidx] = newVal;
        }

        private void Op_Booleval()
        {
            object operand = _state.Operands.Pop();

            switch (RuntimeMirror.VarTypeFromRuntimeType(operand.GetType()))
            {
                case VarType.Integer:
                    SafeOperandsPush((((int)operand) != 0) ? 1 : 0);
                    break;

                case VarType.Float:
                    SafeOperandsPush((((float)operand) != 0.0f) ? 1 : 0);
                    break;

                case VarType.List:
                    SafeOperandsPush((((LSLList)operand).Members.Count != 0) ? 1 : 0);
                    break;

                case VarType.Rotation:
                    SafeOperandsPush((((Quaternion)operand) != Quaternion.Identity) ? 1 : 0);
                    break;

                case VarType.String:
                    string sOperand = (string)operand;
                    SafeOperandsPush((sOperand != String.Empty && sOperand != ZERO_GUID) ? 1 : 0);
                    break;

                case VarType.Vector:
                    SafeOperandsPush((((Vector3)operand) != Vector3.Zero) ? 1 : 0);
                    break;

                default:
                    throw new VMException(String.Format("VM was unable to perform boolean evaluation on the given operand '{0}'", operand));
            }
        }
    }
}

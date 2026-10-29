using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using InWorldz.Phlox.VM;

namespace InWorldz.Phlox.ByteCompiler
{
    public class FunctionSymbol
    {
        private string _name;
        public string Name
        {
            get
            {
                return _name;
            }
        }

        private int _address;
        public int Address
        {
            get
            {
                return _address;
            }
        }

        private int _constIndex;
        public int ConstIndex
        {
            get
            {
                return _constIndex;
            }
        }

        private FwdReferenceBehavior _fwdBehavior = new FwdReferenceBehavior();

        private int _numberOfArguments;
        public int NumberOfArguments
        {
            get
            {
                return _numberOfArguments;
            }
        }

        private int _numberOfLocals;
        public int NumberOfLocals
        {
            get
            {
                return _numberOfLocals;
            }
        }

        public bool IsFwdRef
        {
            get
            {
                return _fwdBehavior.IsFwdRef;
            }
        }

        public FunctionSymbol(string name, int constIndex)
        {
            _name = name;
            _constIndex = constIndex;
            _fwdBehavior.IsFwdRef = true;
        }

        public FunctionSymbol(string name, int address, int constIndex, int nargs, int nlocals)
        {
            _name = name;
            _address = address;
            _constIndex = constIndex;
            _numberOfArguments = nargs;
            _numberOfLocals = nlocals;
            _fwdBehavior.IsFwdRef = false;
        }

        public void Define(int address, int nargs, int nlocals)
        {
            _address = address;
            _numberOfArguments = nargs;
            _numberOfLocals = nlocals;
            _fwdBehavior.IsFwdRef = false;
        }

        public void AddFwdRef(int address)
        {
            _fwdBehavior.AddFwdRef(address);
        }

        public void ResolveFwdRefs(IList<byte> byteCode)
        {
            _fwdBehavior.ResolveFwdRefs(byteCode, _constIndex);
        }

        public bool HasUndefinedRefs()
        {
            return _fwdBehavior.HasUndefinedRefs();
        }

        internal object ToFunctionInfo()
        {
            return new FunctionInfo
            {
                Address = _address,
                Name = _name,
                NumberOfArguments = _numberOfArguments,
                NumberOfLocals = _numberOfLocals
            };
        }

        public override bool Equals(object obj)
        {
            FunctionSymbol f = obj as FunctionSymbol;
            if (f != null)
            {
                return f.Name == this.Name;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return this.Name.GetHashCode();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.ByteCompiler
{
    public class LabelSymbol
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

        public bool IsFwdRef
        {
            get
            {
                return _fwdBehavior.IsFwdRef;
            }
        }

        private FwdReferenceBehavior _fwdBehavior = new FwdReferenceBehavior();

        public LabelSymbol(string name, int address)
        {
            _name = name;
            _address = address;
            _fwdBehavior.IsFwdRef = false;
        }

        public LabelSymbol(string name)
        {
            _name = name;
            _fwdBehavior.IsFwdRef = true;
        }

        public void AddFwdRef(int address)
        {
            _fwdBehavior.AddFwdRef(address);
        }

        public void Define(int address)
        {
            _address = address;
            _fwdBehavior.IsFwdRef = false;
        }

        public void ResolveFwdRefs(IList<byte> byteCode)
        {
            _fwdBehavior.ResolveFwdRefs(byteCode, _address);
        }

        public bool HasUndefinedRefs()
        {
            return _fwdBehavior.HasUndefinedRefs();
        }

        public override bool Equals(object obj)
        {
            LabelSymbol l = obj as LabelSymbol;
            if (l != null)
            {
                return l.Name == this.Name;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return this.Name.GetHashCode();
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.ByteCompiler
{
    public class FwdReferenceBehavior
    {
        private List<int> _fwdRefs = new List<int>();
        private bool _isFwdRef;

        public bool IsFwdRef
        {
            get
            {
                return _isFwdRef;
            }

            set
            {
                _isFwdRef = value;
            }
        }

        public FwdReferenceBehavior()
        {
        }

        public void AddFwdRef(int address)
        {
            _fwdRefs.Add(address);
        }

        public void ResolveFwdRefs(IList<byte> byteCode, int ourAddress)
        {
            foreach (int address in _fwdRefs)
            {
                Util.Encoding.WriteInt(byteCode, address, ourAddress);
            }

            _isFwdRef = false;
        }

        public bool HasUndefinedRefs()
        {
            return _isFwdRef;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace InWorldz.Phlox.ByteCompiler
{
    public class EventSymbol
    {
        private static Types.SupportedEventList evtList = new Types.SupportedEventList();

        private int _state;
        public int State
        {
            get
            {
                return _state;
            }
        }

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

        private int _numArguments;
        public int NumberOfArguments
        {
            get
            {
                return _numArguments;
            }
        }

        private int _numLocals;
        public int NumberOfLocals
        {
            get
            {
                return _numLocals;
            }
        }

        public EventSymbol(int stateId, string name, int address, int numArgs, int numLocals)
        {
            _state = stateId;
            _name = name;
            _address = address;
            _numArguments = numArgs;
            _numLocals = numLocals;
        }

        internal VM.EventInfo ToEventInfo()
        {
            return new VM.EventInfo(_state, _name, _numArguments, _numLocals, _address, evtList.GetEventByName(_name).TableIndex);
        }

        public override bool Equals(object obj)
        {
            EventSymbol e = obj as EventSymbol;
            if (e != null)
            {
                return e.Name == this.Name;
            }

            return false;
        }

        public override int GetHashCode()
        {
            return this.Name.GetHashCode();
        }
    }
}

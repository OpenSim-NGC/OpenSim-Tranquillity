using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using OpenMetaverse;
using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.Util
{
    internal class MemoryCalc
    {
        public static int CalcSizeOf(IEnumerable<object> objContainer)
        {
            int sz = 0;
            foreach (object obj in objContainer)
            {
                sz += MemoryCalc.CalcSizeOf(obj);
            }

            return sz;
        }

        public static int CalcSizeOf(object obj)
        {
            if (obj == null || obj is Sentinel)
            {
                return 0;
            }

            switch (RuntimeMirror.VarTypeFromRuntimeType(obj.GetType()))
            {
                case VarType.Integer:
                    return 4;

                case VarType.Float:
                    return 4;

                case VarType.String:
                    return 4 + (((string)obj).Length * 2);

                case VarType.Vector:
                    return 12;

                case VarType.Rotation:
                    return 16;

                case VarType.List:
                    return ((Types.LSLList)obj).MemorySize;
            }

            if (obj is IList<object>)
            {
                return MemoryCalc.CalcSizeOf((IList<object>)obj);
            }

            return 0;
        }
    }
}

using System.Collections.Generic;
using OpenMetaverse;

namespace InWorldz.Phlox.Types
{
    /// <summary>
    /// All the variable types supported by the LSL language implementation
    /// </summary>
    public enum VarType
    {
        Integer = 0,
        Float,
        Vector,
        Rotation,
        List,
        Key,
        String,
        Void
    }

    public class RuntimeMirror
    {
        static Dictionary<System.Type, VarType> _typeDict = new Dictionary<System.Type, VarType>
        {
            {typeof(int),           VarType.Integer},
            {typeof(float),         VarType.Float},
            {typeof(Vector3),       VarType.Vector},
            {typeof(Quaternion),    VarType.Rotation},
            {typeof(LSLList),       VarType.List},
            {typeof(string),        VarType.String}
        };

        public static VarType VarTypeFromRuntimeType(System.Type type)
        {
            VarType outType;
            if (_typeDict.TryGetValue(type, out outType))
            {
                return outType;
            }
            else
            {
                return VarType.Void;
            }
        }
    }
}
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using ProtoBuf;

namespace InWorldz.Phlox.Serialization
{
    /// <summary>
    /// This class is a representation of the LSLList primitive type but can be serialized
    /// </summary>
    [ProtoContract]
    public class SerializedLSLList
    {
        [ProtoMember(1)]
        public List<SerializedLSLPrimitive> ListContents;

        /// <summary>
        /// Create a new Serializable List from the LSLList primitive type 
        /// </summary>
        /// <param name="lSLList"></param>
        /// <returns></returns>
        public static SerializedLSLList FromList(Types.LSLList lSLList)
        {
            SerializedLSLList list = new SerializedLSLList();
            list.ListContents = new List<SerializedLSLPrimitive>();

            foreach (object obj in lSLList.Members)
            {
                SerializedLSLPrimitive primitive = new SerializedLSLPrimitive();
                primitive.Value = obj;
                list.ListContents.Add(primitive);
            }

            return list;
        }


        public SerializedLSLList()
        {
        }

        /// <summary>
        /// Transforms this list into an LSLList primitive type
        /// </summary>
        /// <returns></returns>
        public Types.LSLList ToList()
        {
            object[] members;

            if (ListContents != null)
            {
                members = new object[ListContents.Count];
            }
            else
            {
                members = new object[0];
            }

            for (int i = 0; i < members.Length; i++)
            {
                members[i] = ListContents[i].Value;
            }

            Types.LSLList lslList = new Types.LSLList(members);
            return lslList;
        }
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using OpenMetaverse;
using InWorldz.Phlox.Serialization;

using ProtoBuf;

namespace InWorldz.Phlox.VM
{
    /// <summary>
    /// Variables that are set during some events to provide the script
    /// with access to llDetectedKey, llDetectedPos etc
    /// </summary>
    [ProtoContract]
    public class DetectVariables
    {
        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(1)]
        private string GrabPbuf1Deprecated
        {
            get
            {
                return null; 
            }

            set
            {
                if (value != null)
                {
                    Grab = Vector3.Parse(value);
                }
            }
        }

        [ProtoMember(2)]
        public string Group;

        [ProtoMember(3)]
        public string Key;

        [ProtoMember(4)]
        public int LinkNumber;

        [ProtoMember(5)]
        public string Name;

        [ProtoMember(6)]
        public string Owner;

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(7)]
        private string PosPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    Pos = Vector3.Parse(value);
                }
            }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(8)]
        private string RotPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    Rot = Quaternion.Parse(value);
                }
            }
        }

        [ProtoMember(9)]
        public int Type;

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(10)]
        private string VelPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    Vel = Vector3.Parse(value);
                }
            }
        }


        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(11)]
        private string TouchBinormalPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    TouchBinormal = Vector3.Parse(value);
                }
            }
        }

        [ProtoMember(12)]
        public int TouchFace;

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(13)]
        private string TouchNormalPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    TouchNormal = Vector3.Parse(value);
                }
            }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(14)]
        private string TouchPosPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    TouchPos = Vector3.Parse(value);
                }
            }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(15)]
        private string TouchSTPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    TouchST = Vector3.Parse(value);
                }
            }
        }

        /// <summary>
        /// DEPRECATED DO NOT USE. Field must remain for backwards compat
        /// </summary>
        [Obsolete]
        [ProtoMember(16)]
        private string TouchUVPbuf1Deprecated
        {
            get
            {
                return null;
            }

            set
            {
                if (value != null)
                {
                    TouchUV = Vector3.Parse(value);
                }
            }
        }

 [ProtoMember(17)]
        private SerializedVector3 GrabPbuf2
        {
            get { return new SerializedVector3(Grab); }
            set { if (value != null) Grab = value.ToVector3(); }
        }
        [ProtoMember(18)]
        private SerializedVector3 PosPbuf2
        {
            get { return new SerializedVector3(Pos); }
            set { if (value != null) Pos = value.ToVector3(); }
        }
        [ProtoMember(19)]
        private SerializedQuaternion RotPbuf2
        {
            get { return new SerializedQuaternion(Rot); }
            set { if (value != null) Rot = value.ToQuaternion(); }
        }
        [ProtoMember(20)]
        private SerializedVector3 VelPbuf2
        {
            get { return new SerializedVector3(Vel); }
            set { if (value != null) Vel = value.ToVector3(); }
        }
        [ProtoMember(21)]
        private SerializedVector3 TouchBinormalPbuf2
        {
            get { return new SerializedVector3(TouchBinormal); }
            set { if (value != null) TouchBinormal = value.ToVector3(); }
        }
        [ProtoMember(22)]
        private SerializedVector3 TouchNormalPbuf2
        {
            get { return new SerializedVector3(TouchNormal); }
            set { if (value != null) TouchNormal = value.ToVector3(); }
        }
        [ProtoMember(23)]
        private SerializedVector3 TouchPosPbuf2
        {
            get { return new SerializedVector3(TouchPos); }
            set { if (value != null) TouchPos = value.ToVector3(); }
        }
        [ProtoMember(24)]
        private SerializedVector3 TouchSTPbuf2
        {
            get { return new SerializedVector3(TouchST); }
            set { if (value != null) TouchST = value.ToVector3(); }
        }
        [ProtoMember(25)]
        private SerializedVector3 TouchUVPbuf2
        {
            get { return new SerializedVector3(TouchUV); }
            set { if (value != null) TouchUV = value.ToVector3(); }
        }
        public Vector3 Grab;
        public Vector3 Pos;
        public Quaternion Rot;
        public Vector3 Vel;
        public Vector3 TouchBinormal;
        public Vector3 TouchNormal;
        public Vector3 TouchPos;
        public Vector3 TouchST;
        public Vector3 TouchUV;
        [ProtoMember(26)]
        public string BotID;
    }
}

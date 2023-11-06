using System;
using System.IO;
using System.Text;

namespace OpenSim.Region.Framework.Scenes
{
    public class LinksetDataEntry
    {
        private string value;
        private string pass="";

        public LinksetDataEntry(string value, string pass)
        {
            this.value = value;
            this.pass = pass;
        }
        private LinksetDataEntry(){}

        public string testAndGetValue(string pass)
        {
            if (!IsProtected) return value;
            else if (this.pass == pass) return value;
            else return "";
        }

        public bool IsProtected
        {
            get
            {
                return (pass != "");
            }
        }

        /// <summary>
        /// This is used by the accounting calculator, you should instead use testAndGetValue
        /// </summary>
        public String val
        {
            get
            {
                return value;
            }
        }

        public bool test(string pass)
        {
            if (!IsProtected) return true;
            else if (this.pass == pass) return true;
            else return false;
        }

        public Byte[] serialize()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write(value);
                    bw.Write(pass);
                    return ms.ToArray();
                }
            }
        }

        public static LinksetDataEntry deserialize(Byte[] inf)
        {
            LinksetDataEntry pd = new LinksetDataEntry();
            using (BinaryReader br = new BinaryReader(new MemoryStream(inf)))
            {
                pd.value = br.ReadString();
                pd.pass = br.ReadString();

                return pd;
            }
        }
    }
}
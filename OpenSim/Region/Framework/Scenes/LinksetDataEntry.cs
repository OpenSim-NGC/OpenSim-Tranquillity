using System;
using System.IO;

namespace OpenSim.Region.Framework.Scenes
{
    public class LinksetDataEntry
    {
        public LinksetDataEntry(string value, string pass)
        {
            this.Value = value;
            this.Password = pass;
        }

        private LinksetDataEntry()
        { }

        public bool IsProtected
        {
            get { return (string.IsNullOrEmpty(this.Password) == false); }
        }

        public string Password { get; private set; } = string.Empty;

        public string Value { get; private set; }

        public bool CheckPassword(string pass)
        {
            // A undocumented caveat for LinksetData appears to be that even for unprotected values, if a pass is provided, it is still treated as protected
            if (this.Password == pass)
                return true;
            else
                return false;
        }

        public string CheckPasswordAndGetValue(string pass)
        {
            if (string.IsNullOrEmpty(this.Password) || (this.Password == pass)) 
                return this.Value;
            else 
                return string.Empty;
        }

        public Byte[] Serialize()
        {
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    bw.Write(this.Value);
                    bw.Write(this.Password);
                    return ms.ToArray();
                }
            }
        }

        public static LinksetDataEntry Deserialize(Byte[] inf)
        {
            LinksetDataEntry pd = new LinksetDataEntry();
            using (BinaryReader br = new BinaryReader(new MemoryStream(inf)))
            {
                pd.Value = br.ReadString();
                pd.Password = br.ReadString();

                return pd;
            }
        }
    }
}
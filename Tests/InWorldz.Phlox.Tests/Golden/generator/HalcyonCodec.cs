// Generated from Halcyon InWorldz/InWorldz.Phlox.Engine/LSLSystemAPI.cs lines 16053-16955, verbatim
// except: 'private static class CodecUtil' -> 'public static class CodecUtil'. LSL_List is Halcyon's own
// InWorldz.Phlox.Types.LSLList (LSLSystemAPI.cs:44), from Halcyon's bin/InWorldz.Phlox.dll.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using OpenMetaverse;
using LSL_List = InWorldz.Phlox.Types.LSLList;
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.7.1", FrameworkDisplayName = ".NET Framework 4.7.1")]
namespace HalcyonCodecs {
public partial class Api {
        //Static utility class for use with iwStringCodec
        public static class CodecUtil
        {

            //Returns true if a codec is available for conversions
            public static bool HasCodec(string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                    case "uuid":
                    case "base64":
                    case "base64-safe":
                    case "base4096":
                    case "base4k":
                        return true;
                }
                return false;
            }

            //
            // Encode the input to a specified codec.
            //
            public static string Encode(string str, string codec)
            {
                return Encode(Encoding.Unicode.GetBytes(str), codec);
            }

            //
            // Decode an input string from a specified codec
            //
            public static string DecodeToString(string str, string codec)
            {
                byte[] bytes = null;
                string s;
                if (codec == "uuid")
                {
                    bytes = Decode(str, "base16");
                    if (bytes == null) return null;
                    s = pad_dashes(Encoding.Unicode.GetString(bytes));
                }
                else
                {

                    bytes = Decode(str, codec);
                    if (bytes == null) return null;
                    s = Encoding.Unicode.GetString(bytes);
                }
                bytes = null;
                return s;
            }

            //
            // Encode byte data to a specified codec
            //
            public static string Encode(byte[] bytes, string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                        return EncodeBase16(bytes);
                    case "uuid":
                        string str = EncodeBase16(bytes);
                        if (str.Length == 32) return pad_dashes(str);
                        else return str;
                    case "base64":
                        try
                        {
                            return Convert.ToBase64String(bytes);
                        }
                        catch
                        {
                            return null;
                        }
                    case "base64-safe":
                        try
                        {
                            return Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_");
                            //return HttpServerUtility.UrlTokenEncode(bytes);

                        }
                        catch
                        {
                            return null;
                        }
                    case "base4k":
                    case "base4096":
                        return EncodeBase4k(bytes);
                    default:
                        return null;
                }
            }

            //
            // Decode string data to bytes from a specified codec.
            //
            public static byte[] Decode(string str, string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                        return DecodeBase16(str);
                    case "uuid":
                        return DecodeBase16(str.Replace("-", String.Empty));
                    case "base64":
                        try
                        {
                            return Convert.FromBase64String(str);
                        }
                        catch
                        {
                            return null;
                        }
                    case "base64-safe":
                        try
                        {
                            return Convert.FromBase64String(str.Replace("-", "+").Replace("_", "/"));
                            //return HttpServerUtility.UrlTokenDecode(str);
                        }
                        catch
                        {
                            return null;
                        }
                    case "base4k":
                    case "base4096":
                        return DecodeBase4k(str);
                    default:
                        return null;
                }
            }

            //
            // Returns 1, 0, or -1, depending on whether or not the input string conforms to the specified codec.
            // 1 : Valid
            // 0 : Invalid
            // -1: Codec is not valid or validation is not implemented
            //
            public static int Validate(string str, string codec)
            {
                switch (codec.ToLower())
                {
                    case "base16":
                        return ValidateBase16(str);
                    case "uuid":
                        UUID u = UUID.Zero;
                        return UUID.TryParse(str, out u) ? 1 : 0;
                    case "base64":
                        return ValidateBase64(str, '+', '/');
                    case "base64-safe":
                        return ValidateBase64(str, '-', '_');
                    case "base4k":
                    case "base4096":
                        return ValidateBase4k(str);
                    default:
                        return -1;
                }
            }

            //Inserts the dashes into a UUID.
            private static string pad_dashes(string str)
            {
                return string.Format("{0}-{1}-{2}-{3}-{4}",
                    str.Substring(0, 8),
                    str.Substring(8, 4),
                    str.Substring(12, 4),
                    str.Substring(16, 4),
                    str.Substring(20, 12)
                );
            }


            //Convert a base16 string to byte data.
            private static byte[] DecodeBase16(string str)
            {
                int len = str.Length / 2;
                byte[] bytes = new byte[len];
                using (var sr = new StringReader(str))
                {
                    for (int i = 0; i < len; i++)
                    {
                        bytes[i] = Convert.ToByte(new string(new char[2] { (char)sr.Read(), (char)sr.Read() }), 16);
                    }
                }
                return bytes;
            }

            //Convert byte data to base16 encoding
            private static string EncodeBase16(byte[] bytes)
            {
                StringBuilder output = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                {
                    output.AppendFormat("{0:X2}", b);
                }
                string ret = output.ToString().ToLower();
                output.Clear();
                return ret;
            }

            //Check if the input string is a valid base16 string
            private static int ValidateBase16(string str)
            {
                int i = 0;
                if (str.StartsWith("0x")) i = 2;
                int len = str.Length;
                if (len % 2 == 1) return 0;
                for (; i < len; i++)
                {
                    char c = str[i];
                    if (!Char.IsDigit(c) || (c >= 'a' && c <= 'f'))
                    {
                        return 0;
                    }
                }
                return 1;
            }

            //Check if the input string is a valid base64 string
            private static int ValidateBase64(string str, char token1, char token2)
            {
                int len = str.Length;
                for(int i=0; i< len; i++) {
                    char c = str[i];
                    if (!Char.IsLetterOrDigit(c) && c != token1 && c != token2)
                    {
                        if(len % 4 != 0 && (i==len-1 || i==len-2) && c != '=') return 0;
                    }
                }
                return 1;
            }

            //Convert byte data to base4096 encoding.
            // Adapted from public domain code by Adam Wozniak and Doran Zemlja
            // http://wiki.secondlife.com/w/index.php?title=Key_Compression#Base_4096_Script_.28Reduced_Code_Size.29
            private static string EncodeBase4k(byte[] inBytes)
            {
                StringBuilder ret = new StringBuilder();
                int len = inBytes.Length;
                int[] bytes = new int[len*2];
                for (int i = 0; i < (len*2); i+=2)
                {
                    bytes[i] = (inBytes[i / 2] & 0xf0) >> 4;
                    bytes[i + 1] = inBytes[i / 2] & 0xf;
                }
                len = bytes.Length;
                int extra = 0;
                for (int i = 0; i < len; i+=3)
                {
                    int A = bytes[i];
                    int B = 0;
                    int C = 0;
                    if (i + 1 < len)
                    {
                        B = bytes[i + 1];
                        if (i + 2 < len) C = bytes[i + 2];
                        else extra = 1;
                    }
                    else extra = 2;
                    
                    int D = 0xB;
                    
                    if (A == 0)
                    {
                        A = 0xE;
                        D = 8;
                    }
                    else if (A == 0xD)
                    {
                        A = 0xE;
                        D = 9;
                    }
                    else if (A == 0xF)
                    {
                        A = 0xE;
                        D = 0xA;
                    }

                    ret.Append("%E");
                    ret.Append(A.ToString("X"));
                    ret.Append("%");
                    ret.Append(D.ToString("X"));
                    ret.Append(B.ToString("X"));
                    ret.Append("%B");
                    ret.Append(C.ToString("X"));
                }

                if (extra >= 1) ret.Append("%3D");
                if (extra == 2) ret.Append("%3D");

                string output = Uri.UnescapeDataString(ret.ToString());
                ret = null;
                bytes = null;

                return output;
            }

            //Convert a base4096 string to byte data
            // Adapted from public domain code by Adam Wozniak and Doran Zemlja
            // http://wiki.secondlife.com/w/index.php?title=Key_Compression#Base_4096_Script_.28Reduced_Code_Size.29
            private static byte[] DecodeBase4k(string str)
            {
                int extra = 0;
                if (str.EndsWith("==")) extra = 2;
                else if (str.EndsWith("=")) extra = 1;

                str = Uri.EscapeDataString(str.Replace("=", String.Empty));
                //byte[] bytes = Encoding.Unicode.GetBytes(str.Replace("%", String.Empty).ToLower());
                byte[] inBytes = DecodeBase16(str.Replace("%", String.Empty));
                StringBuilder ret = new StringBuilder();


                int len = inBytes.Length;
                int[] bytes = new int[len*2];
                for (int i = 0; i < len; i++)
                {
                    bytes[i * 2] = (inBytes[i] >> 4) & 0xF;
                    bytes[(i*2)+1] = inBytes[i] & 0xF;
                }

                len *= 2;
                for (int i = 0; i+5 < len; i += 6)
                {
                    int A = bytes[i + 1];
                    int B = bytes[i + 3];
                    int C = bytes[i + 5];
                    int D = bytes[i + 2];
                    if (D == 0x8) A = 0;
                    else if (D == 0x9) A = 0xD;
                    else if (D == 0xA) A = 0xF;

                    ret.Append(A.ToString("X"));
                    ret.Append(B.ToString("X"));
                    ret.Append(C.ToString("X"));
                }
                if (extra > 0) ret.Length -= extra;
                string output = ret.ToString();
                ret = null;
                return DecodeBase16(output);
            }

            //Check if a base4096 string is valid
            //TODO
            private static int ValidateBase4k(string str)
            {
                return -1;
            }

            //Compress string data with gzip compression.
            public static string gzipCompress(string str, string codec = "base4096")
            {
                var bytes = Encoding.Unicode.GetBytes(str);
                using (var msi = new MemoryStream(bytes))
                using (var mso = new MemoryStream())
                {
                    using (var gs = new GZipStream(mso, CompressionMode.Compress))
                    {
                        msi.CopyTo(gs);
                    }
                    return Encode(mso.ToArray(), codec);
                }
            }

            //Decompress string data with gzip compression;
            public static string gzipDecompress(string str, string codec = "base4096")
            {
                var bytes = CodecUtil.Decode(str, codec);
                using (var msi = new MemoryStream(bytes))
                using (var mso = new MemoryStream())
                {
                    using (var gs = new GZipStream(msi, CompressionMode.Decompress))
                    {
                        gs.CopyTo(mso);
                    }
                    return Encoding.Unicode.GetString(mso.ToArray());
                }
            }

            
            //Encrypt Data with AES encryption
            public static byte[] AesEncrypt(byte[] valueBytes, byte[] keyBytes, string _vector)
            {
            //    return AesEncrypt<AesManaged>(valueBytes, keyBytes, _vector);
            //}
            //public static byte[] AesEncrypt<T>(byte[] valueBytes, byte[] keyBytes, string _vector)
            //        where T : SymmetricAlgorithm, new()
            //{
                byte[] vectorBytes = DecodeBase16(_vector);

                byte[] encrypted;
                using (AesManaged cipher = new AesManaged())
                {

                    cipher.Mode = CipherMode.CBC;

                    using (ICryptoTransform encryptor = cipher.CreateEncryptor(keyBytes, vectorBytes))
                    {
                        using (MemoryStream to = new MemoryStream())
                        {
                            using (CryptoStream writer = new CryptoStream(to, encryptor, CryptoStreamMode.Write))
                            {
                                writer.Write(valueBytes, 0, valueBytes.Length);
                                writer.FlushFinalBlock();
                                encrypted = to.ToArray();
                            }
                        }
                    }
                    cipher.Clear();
                }
                vectorBytes = null;
                return encrypted;
            }

            //Decrypt data with AES decryption
            public static string AesDecrypt(byte[] valueBytes, byte[] keyBytes, string _vector)
            {
            //    return AesDecrypt<AesManaged>(value, keyBytes, _vector);
            //}
            //public static string AesDecrypt<T>(byte[] valueBytes, byte[] keyBytes, string _vector) where T : SymmetricAlgorithm, new()
            //{
                byte[] vectorBytes = DecodeBase16(_vector);

                byte[] decrypted;
                int decryptedByteCount = 0;

                using (AesManaged cipher = new AesManaged())
                {

                    cipher.Mode = CipherMode.CBC;

                    try
                    {
                        using (ICryptoTransform decryptor = cipher.CreateDecryptor(keyBytes, vectorBytes))
                        {
                            using (MemoryStream from = new MemoryStream(valueBytes))
                            {
                                using (CryptoStream reader = new CryptoStream(from, decryptor, CryptoStreamMode.Read))
                                {
                                    decrypted = new byte[valueBytes.Length];
                                    decryptedByteCount = reader.Read(decrypted, 0, decrypted.Length);
                                }
                            }
                        }
                    }
                    catch
                    {
                        return String.Empty;
                    }

                    cipher.Clear();
                }
                vectorBytes = null;
                return Encoding.Unicode.GetString(decrypted, 0, decryptedByteCount);
            }

            //Escape non-ascii characters in a string
            public static string StringToAscii(string str)
            {
                StringBuilder sb = new StringBuilder();
                foreach (char c in str)
                {
                    if (c > 127) sb.Append("\\u" + ((int)c).ToString("x4"));
                    else sb.Append(c);
                }

                return sb.ToString();
            }

            //Unescape non-ascii characters from a string
            public static string AsciiToString(string str)
            {
                return Regex.Replace(
                    str,
                    @"\\u(?<Value>[a-zA-Z0-9]{4})",
                    m =>
                    {
                        return ((char)int.Parse(m.Groups["Value"].Value, System.Globalization.NumberStyles.HexNumber)).ToString();
                    });
            }

            //Helper function for Ascii Compression
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static string encode15BitsToChar(int num) {
                if (num < 0 || num >= 0x8000) return "�";
                num += 0x1000;
                return Uri.UnescapeDataString(
                    string.Format("%{0}%{1}%{2}",
                    (0xE0 + (num >> 12)).ToString("X"),
                    (0x80 + ((num >> 6) & 0x3F)).ToString("X"),
                    (0x80 + (num & 0x3F)).ToString("X")
                ));
            }

            //Helper function for Ascii Compression
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static int charToInt(string src, int index) {
                if (index < 0) index = src.Length + index;
                if (Math.Abs(index) >= src.Length) return 0;
                char c = src[index];
                return (int)c;
            }
            
            //Helper function for Ascii Compression
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static int decodeCharTo15Bits(string ch)
            {
                int t = Convert.ToChar(ch);
                return ((((t >> 12) & 0xFF) & 0x1F) << 12) +
                    ((((t >> 6) & 0xFF) & 0x3F) << 6) +
                    ((t & 0xFF) & 0x3F) - 0x1000;
            }

            //Compress an ascii string by encoding two characters into a single 15bit character.
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            public static string AsciiCompress(string str)
            {
                if (String.IsNullOrEmpty(str)) return str;
                str = StringToAscii(str);
                int len = str.Length;
                bool emptyEnd = false;
                if (len % 2 == 1)
                {
                    str += " ";
                    len++;
                    emptyEnd = true;
                }

                StringBuilder encoded = new StringBuilder();
                for (int i = 0; i < len; i += 2)
                {
                    encoded.Append(encode15BitsToChar(
                        charToInt(str, i) << 7 | charToInt(str, i+1)
                    ));
                }

                if (emptyEnd) encoded.Append("=");

                return encoded.ToString();
            }

            //Decompress an ascii string from 15bit encoding.
            // Adapted from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            public static string AsciiDecompress(string str)
            {
                if (String.IsNullOrEmpty(str)) return str;
                int len = str.Length;
                StringBuilder result = new StringBuilder(len * 2);
                for (int i = 0; i < len; i++)
                {
                    if (i == (len - 1) && str.Substring(i) == "=")
                    {
                        result.Length--;
                        break;
                    }
                    else
                    {
                        int cInt15 = decodeCharTo15Bits(str.Substring(i,1));
                        result.Append((char)(cInt15 >> 7));
                        result.Append((char)(cInt15 & 0x7f));
                    }
                }
                return result.ToString();
            }

            //Cryptographic hash providers
            private static MD5CryptoServiceProvider md5util = new MD5CryptoServiceProvider();
            private static SHA1CryptoServiceProvider sha1util = new SHA1CryptoServiceProvider();
            private static SHA256Managed sha2util = new SHA256Managed();
            private static SHA384Managed sha384util = new SHA384Managed();
            private static SHA512Managed sha512util = new SHA512Managed();
            //Return a cryptographic hash
            public static string Hash(string str, string nonce, string inCodec, string outCodec)
            {
                if (!String.IsNullOrEmpty(nonce)) str = str + ":" + nonce;
                byte[] bytes = null;
                byte[] inBytes = Encoding.UTF8.GetBytes(str);
                switch (inCodec)
                {
                    case "md5":
                        lock(md5util)
                            bytes = md5util.ComputeHash(inBytes);
                        break;
                    case "sha1":
                    case "sha128":
                        lock (sha1util)
                            bytes = sha1util.ComputeHash(inBytes);
                        break;
                    case "sha2":
                    case "sha256":
                        lock (sha2util)
                            bytes = sha2util.ComputeHash(inBytes);
                        break;
                    case "sha384":
                        lock (sha384util)
                            bytes = sha384util.ComputeHash(inBytes);
                        break;
                    case "sha512":
                        lock (sha512util)
                            bytes = sha512util.ComputeHash(inBytes);
                        break;
                    default:
                        return String.Empty;
                }
                return Encode(bytes, outCodec);
            }
        }



        public string iwStringCodec(string str, string codec, int operation, LSL_List extraParams)
        {
            const int OP_DECODE = 0;
            const int OP_ENCODE = 1;
            const int OP_VALIDATE = 2;

                 if (str.Length >= 16000)this.ScriptSleep((str.Length / 100) * 20);
            else if (str.Length >=  8000)this.ScriptSleep((str.Length / 100) * 10);
            else if (str.Length >=  1000)this.ScriptSleep((str.Length / 100) *  5);

            int pLen = extraParams.Length;
            byte[] cBytes = null;
            bool useBytes = false;
            codec = codec.ToLower();
            for (int i = 0; i < pLen; i += 2)
            {
                string k = extraParams.GetLSLStringItem(i);
                string v = extraParams.GetLSLStringItem(i+1);
                k = k.ToLower(); v = v.ToLower();
                if (k == "input codec")
                {
                    if (CodecUtil.HasCodec(v) == false)
                    {
                        LSLError("Invalid input codec: " + v);
                        return String.Empty;
                    }
                    cBytes = CodecUtil.Decode(str, v.ToLower());
                    useBytes = true;
                }
            }

            switch (codec)
            {
                case "ascii":
                    if (String.IsNullOrEmpty(str)) return str;
                    if(operation == OP_ENCODE) {
                        return CodecUtil.StringToAscii(str);
                    } else if(operation == OP_DECODE) {
                        try {
                            return CodecUtil.AsciiToString(str);
                        } catch (Exception e) {
                            LSLError("Error in ascii decoding: " + e.Message);
                        }
                    } else if(operation == OP_VALIDATE) {
                        try {
                            CodecUtil.AsciiToString(str);
                            return "VALID";
                        }
                        catch (Exception) {
                            return "INVALID";
                        }
                    }
                    break;
                case "gzip":
                    if (String.IsNullOrEmpty(str)) return str;
                    string outputCodec = "base4096";
                    if (extraParams.Length >= 2 && extraParams.Length % 2 == 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i += 2)
                        {
                            if (extraParams.GetLSLStringItem(i).ToLower() == "output codec")
                            {
                                outputCodec = extraParams.GetLSLStringItem(i + 1);
                                break;
                            }
                        }
                    }
                    if (!CodecUtil.HasCodec(outputCodec))
                    {
                        LSLError("Bad codec for gzip compression: " + outputCodec);
                        return String.Empty;
                    }
                    if (operation == OP_ENCODE) return CodecUtil.gzipCompress(str, outputCodec);
                    else if (operation == OP_DECODE) return CodecUtil.gzipDecompress(str, outputCodec);
                    break;
                case "ascii-zip":
                    if (String.IsNullOrEmpty(str)) return str;
                    if (operation == OP_ENCODE) return CodecUtil.AsciiCompress(str);
                    else if (operation == OP_DECODE) return CodecUtil.AsciiDecompress(str);
                    break;
                case "md5":
                case "sha1":
                case "sha-1":
                case "sha128":
                case "sha-128":
                case "sha2":
                case "sha-2":
                case "sha256":
                case "sha-256":
                case "sha384":
                case "sha-384":
                case "sha512":
                case "sha-512":
                    string outCodec = "base16";
                    string nonce = String.Empty;
                    if (extraParams.Length >= 0 && extraParams.Length % 2 == 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i += 2)
                        {
                            string k = extraParams.GetLSLStringItem(i).ToLower();
                            string val = extraParams.GetLSLStringItem(i + 1);
                            switch (k)
                            {
                                case "output codec":
                                    outCodec = val;
                                    break;
                                case "nonce":
                                    nonce = val;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    if (!CodecUtil.HasCodec(outCodec))
                    {
                        LSLError(string.Format("Error: invalid codec for {0} hash: {1}", codec, outCodec));
                    }
                    return CodecUtil.Hash(str, nonce, codec, outCodec);
                case "aes-key":
                    if (String.IsNullOrEmpty(str))
                    {
                        LSLError(string.Format("Error: using a blank password to generate an AES encryption key is not allowed."));
                        return String.Empty;
                    }
                    byte[] _aesKeySalt = null;
                    string _aesKeyCodec = "base16";
                    int _aesKeyIter = 8;
                    if (extraParams.Length >= 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i += 2)
                        {
                            string k = extraParams.GetLSLStringItem(i).ToLower();
                            string val = extraParams.GetLSLStringItem(i + 1);
                            switch (k)
                            {
                                case "salt":
                                    if(String.IsNullOrEmpty(val)) {
                                        LSLError("Salt for AES encryption key cannot be blank.");
                                        return String.Empty;
                                    }
                                    _aesKeySalt = Encoding.UTF8.GetBytes(val);
                                    break;
                                case "output codec":
                                    if (CodecUtil.HasCodec(val) == false)
                                    {
                                        LSLError("Error: invalid codec for AES encryption key: " + val);
                                        return String.Empty;
                                    }
                                    _aesKeyCodec = val;
                                    break;
                                case "rounds":
                                    int iterTest = Convert.ToInt32(val);
                                    if (iterTest < 1 || iterTest > 1024) {
                                        LSLError("Rounds for AES encryption key cannot be more than 1024 or less than 1");
                                        return String.Empty;
                                    }
                                    _aesKeyIter = iterTest;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    using (var PB = new Rfc2898DeriveBytes(str, _aesKeySalt, _aesKeyIter))
                    {
                        string aesKeyNew = CodecUtil.Encode(PB.GetBytes(32), _aesKeyCodec);
                        this.ScriptSleep(Math.Max(100, _aesKeyIter));
                        return aesKeyNew;
                    }
                case "aes":
                    if (String.IsNullOrEmpty(str)) return str;
                    string aesCodec = "base4096";
                    string aesKeyCodec = "base16";
                    string aesKey = String.Empty;
                    string aesVector = String.Empty;
                    
                    if (extraParams.Length >= 0)
                    {
                        int len = extraParams.Length;
                        for (int i = 0; i < len; i+=2)
                        {
                            string k = extraParams.GetLSLStringItem(i).ToLower();
                            string val = extraParams.GetLSLStringItem(i+1);
                            switch (k)
                            {
                                case "key":
                                    aesKey = val;
                                    break;
                                case "vector":
                                    if (!String.IsNullOrEmpty(val)) aesVector = val.Replace("-",null);
                                    break;
                                case "key codec":
                                    aesKeyCodec = val;
                                    break;
                                case "output codec":
                                    aesCodec = val;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }
                    if (String.IsNullOrEmpty(aesKey) || String.IsNullOrEmpty(aesVector) || String.IsNullOrEmpty(aesCodec))
                    {
                        LSLError("Error: some parameters for AES encryption are blank or missing!");
                        return String.Empty;
                    }
                    if (aesVector.Length != 32)
                    {
                        LSLError("AES vectors require a 32-character hexadecimal string.");
                        return String.Empty;
                    }
                    if (CodecUtil.HasCodec(aesCodec) == false)
                    {
                        LSLError("Error: invalid codec for AES encryption: " + aesCodec);
                        return String.Empty;
                    }
                    if(CodecUtil.HasCodec(aesKeyCodec) == false) {
                        LSLError("Error: invalid input codec for AES encryption key: " + aesKeyCodec);
                        return String.Empty;
                    }
                    byte[] aesKeyBytes = CodecUtil.Decode(aesKey, aesKeyCodec);

                    if (operation == OP_ENCODE)
                    {
                        if (!useBytes)
                        {
                            return CodecUtil.Encode(
                                CodecUtil.AesEncrypt(Encoding.Unicode.GetBytes(str), aesKeyBytes, aesVector),
                                aesCodec
                            );
                        }
                        else
                        {
                            //bytes = CodecUtil.AesEncrypt(cBytes, aesKeyBytes, aesVector);
                            return CodecUtil.Encode(
                                CodecUtil.AesEncrypt(cBytes, aesKeyBytes, aesVector),
                                aesCodec
                            );
                        }
                        //string ret = CodecUtil.Encode(bytes, aesCodec);

                    }
                    else if (operation == OP_DECODE)
                    {
                        if (!useBytes)
                        {
                            return CodecUtil.AesDecrypt(CodecUtil.Decode(str, aesCodec), aesKeyBytes, aesVector);
                        }
                        else
                        {
                            return CodecUtil.AesDecrypt(cBytes, aesKeyBytes, aesVector);
                        }
                        
                    }

                    break;
                case "":
                    LSLError("Error: No codec specified for iwStringCodec!");
                    break;
                default:
                    if (String.IsNullOrEmpty(str)) return str;
                    string ret = null;
                    if (operation == OP_ENCODE)
                    {
                        if (!useBytes) ret = CodecUtil.Encode(str, codec);
                        else ret = CodecUtil.Encode(cBytes, codec);
                    }
                    else if (operation == OP_DECODE) ret = CodecUtil.DecodeToString(str, codec);
                    else if (operation == OP_VALIDATE)
                    {
                        int v = CodecUtil.Validate(str, codec);
                        if (v == 1) return "PASS";
                        else if (v == 0) return "FAIL";
                        else return "INVALID CODEC";
                    }
                    if (ret == null)
                    {
                        LSLError("Error: \"X\" is not a valid codec for iwStringCodec!".Replace("X", codec));
                        return String.Empty;
                    }
                    return ret;
            }
            return String.Empty;
        }
}}

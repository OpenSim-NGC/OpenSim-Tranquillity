/*
 * iwStringCodec, ported from Halcyon: InWorldz/InWorldz.Phlox.Engine/LSLSystemAPI.cs:16053-16662 (CodecUtil)
 * and :16666-16955 (iwStringCodec). The code keeps Halcyon's structure, names,
 * messages, limits and sleeps. Halcyon ran on .NET Framework 4.7.1; where .NET 10 behaves differently the
 * Framework behaviour is reproduced, each place marked "Framework:". Proven byte for byte by the golden
 * vectors in Tests/InWorldz.Phlox.Tests/Golden/, produced by Halcyon's own code on .NET Framework 4.8.1.
 */

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using InWorldz.Phlox.Types;

namespace Phlox.ScriptEngine.Codecs
{
    internal static class HalcyonStringCodec
    {
        /// <summary>
        /// The script-facing side. LSLError is Halcyon's (LSLSystemAPI.cs:14510-14513): a DEBUG_CHANNEL message
        /// "LSL Runtime Error: " + msg through SimChat, which then sleeps 15 ms (:14483-14486, :1041-1045); the call
        /// goes on. ScriptSleep is Halcyon's: a later sleep replaces an earlier one.
        /// </summary>
        public interface IHost
        {
            void LSLError(string msg);
            void ScriptSleep(int delay);
        }

        /// <summary>
        /// Halcyon's iwStringCodec. Its extra parameters are read with LSLList.GetLSLStringItem, the list type Halcyon
        /// used (LSLSystemAPI.cs:44 aliases LSL_List to InWorldz.Phlox.Types.LSLList): an index past the end reads ""
        /// and a number reads as its text. An exception that leaves the call carries .NET Framework's message text,
        /// which is what a Halcyon owner saw when the script stopped. A null result is Halcyon's too (a hash with a bad
        /// output codec); the VM refuses to push it, as Halcyon's did.
        /// </summary>
        public static string iwStringCodec(IHost host, string str, string codec, int operation, LSLList extraParams)
        {
            try
            {
                return Run(host, str, codec, operation, extraParams);
            }
            catch (Exception e)
            {
                Exception framework = FrameworkText.WithFrameworkMessage(e);
                if (ReferenceEquals(framework, e)) throw;
                throw framework;
            }
        }

        // Halcyon LSLSystemAPI.cs:16666-16955
        private static string Run(IHost host, string str, string codec, int operation, LSLList extraParams)
        {
            const int OP_DECODE = 0;
            const int OP_ENCODE = 1;
            const int OP_VALIDATE = 2;

                 if (str.Length >= 16000) host.ScriptSleep((str.Length / 100) * 20);
            else if (str.Length >=  8000) host.ScriptSleep((str.Length / 100) * 10);
            else if (str.Length >=  1000) host.ScriptSleep((str.Length / 100) *  5);

            int pLen = extraParams.Length;
            byte[] cBytes = null;
            bool useBytes = false;
            codec = Lower(codec);
            for (int i = 0; i < pLen; i += 2)
            {
                string k = extraParams.GetLSLStringItem(i);
                string v = extraParams.GetLSLStringItem(i + 1);
                k = Lower(k); v = Lower(v);
                if (k == "input codec")
                {
                    if (CodecUtil.HasCodec(v) == false)
                    {
                        host.LSLError("Invalid input codec: " + v);
                        return String.Empty;
                    }
                    cBytes = CodecUtil.Decode(str, Lower(v));
                    useBytes = true;
                }
            }

            switch (codec)
            {
                case "ascii":
                    if (String.IsNullOrEmpty(str)) return str;
                    if (operation == OP_ENCODE)
                    {
                        return CodecUtil.StringToAscii(str);
                    }
                    else if (operation == OP_DECODE)
                    {
                        try
                        {
                            return CodecUtil.AsciiToString(str);
                        }
                        catch (Exception e)
                        {
                            host.LSLError("Error in ascii decoding: " + FrameworkText.WithFrameworkMessage(e).Message);
                        }
                    }
                    else if (operation == OP_VALIDATE)
                    {
                        try
                        {
                            CodecUtil.AsciiToString(str);
                            return "VALID";
                        }
                        catch (Exception)
                        {
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
                            if (Lower(extraParams.GetLSLStringItem(i)) == "output codec")
                            {
                                outputCodec = extraParams.GetLSLStringItem(i + 1);
                                break;
                            }
                        }
                    }
                    if (!CodecUtil.HasCodec(outputCodec))
                    {
                        host.LSLError("Bad codec for gzip compression: " + outputCodec);
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
                            string k = Lower(extraParams.GetLSLStringItem(i));
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
                        host.LSLError(string.Format("Error: invalid codec for {0} hash: {1}", codec, outCodec));
                    }
                    // Halcyon goes on after this error: Hash returns null for an invalid output codec.
                    return CodecUtil.Hash(str, nonce, codec, outCodec);
                case "aes-key":
                    if (String.IsNullOrEmpty(str))
                    {
                        host.LSLError(string.Format("Error: using a blank password to generate an AES encryption key is not allowed."));
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
                            string k = Lower(extraParams.GetLSLStringItem(i));
                            string val = extraParams.GetLSLStringItem(i + 1);
                            switch (k)
                            {
                                case "salt":
                                    if (String.IsNullOrEmpty(val))
                                    {
                                        host.LSLError("Salt for AES encryption key cannot be blank.");
                                        return String.Empty;
                                    }
                                    _aesKeySalt = Encoding.UTF8.GetBytes(val);
                                    break;
                                case "output codec":
                                    if (CodecUtil.HasCodec(val) == false)
                                    {
                                        host.LSLError("Error: invalid codec for AES encryption key: " + val);
                                        return String.Empty;
                                    }
                                    _aesKeyCodec = val;
                                    break;
                                case "rounds":
                                    int iterTest = Convert.ToInt32(val);
                                    if (iterTest < 1 || iterTest > 1024)
                                    {
                                        host.LSLError("Rounds for AES encryption key cannot be more than 1024 or less than 1");
                                        return String.Empty;
                                    }
                                    _aesKeyIter = iterTest;
                                    break;
                                default:
                                    break;
                            }
                        }
                    }

                    // Halcyon: new Rfc2898DeriveBytes(str, salt, rounds).GetBytes(32) - PBKDF2-HMAC-SHA1 over the password's
                    // UTF-8. Framework checked the salt in that constructor's Salt setter (parameter "value") and refused
                    // one under 8 bytes; .NET 10's (obsolete) constructor names it "salt" and accepts a short one.
                    if (_aesKeySalt == null) throw new ArgumentNullException("value");
                    if (_aesKeySalt.Length < 8)
                        throw new ArgumentException("Salt is not at least eight bytes.");
                    {
                        byte[] derived = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(str), _aesKeySalt, _aesKeyIter, HashAlgorithmName.SHA1, 32);
                        string aesKeyNew = CodecUtil.Encode(derived, _aesKeyCodec);
                        host.ScriptSleep(Math.Max(100, _aesKeyIter));
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
                        for (int i = 0; i < len; i += 2)
                        {
                            string k = Lower(extraParams.GetLSLStringItem(i));
                            string val = extraParams.GetLSLStringItem(i + 1);
                            switch (k)
                            {
                                case "key":
                                    aesKey = val;
                                    break;
                                case "vector":
                                    if (!String.IsNullOrEmpty(val)) aesVector = val.Replace("-", null);
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
                        host.LSLError("Error: some parameters for AES encryption are blank or missing!");
                        return String.Empty;
                    }
                    if (aesVector.Length != 32)
                    {
                        host.LSLError("AES vectors require a 32-character hexadecimal string.");
                        return String.Empty;
                    }
                    if (CodecUtil.HasCodec(aesCodec) == false)
                    {
                        host.LSLError("Error: invalid codec for AES encryption: " + aesCodec);
                        return String.Empty;
                    }
                    if (CodecUtil.HasCodec(aesKeyCodec) == false)
                    {
                        host.LSLError("Error: invalid input codec for AES encryption key: " + aesKeyCodec);
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
                            return CodecUtil.Encode(
                                CodecUtil.AesEncrypt(cBytes, aesKeyBytes, aesVector),
                                aesCodec
                            );
                        }
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
                    host.LSLError("Error: No codec specified for iwStringCodec!");
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
                        host.LSLError("Error: \"X\" is not a valid codec for iwStringCodec!".Replace("X", codec));
                        return String.Empty;
                    }
                    return ret;
            }
            return String.Empty;
        }

        // Halcyon ran with the process culture en-US (OpenSim/Framework/Culture.cs:36); its string.ToLower()
        // there lowers exactly as the invariant culture does.
        private static string Lower(string s) { return s.ToLowerInvariant(); }

        // Halcyon LSLSystemAPI.cs:16053-16662
        internal static class CodecUtil
        {
            public static bool HasCodec(string codec)
            {
                switch (Lower(codec))
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

            public static string Encode(string str, string codec)
            {
                return Encode(Encoding.Unicode.GetBytes(str), codec);
            }

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
                return s;
            }

            public static string Encode(byte[] bytes, string codec)
            {
                switch (Lower(codec))
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

            public static byte[] Decode(string str, string codec)
            {
                switch (Lower(codec))
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

            public static int Validate(string str, string codec)
            {
                switch (Lower(codec))
                {
                    case "base16":
                        return ValidateBase16(str);
                    case "uuid":
                        // Halcyon: LibreMetaverse 1.0.2's UUID.TryParse, which took only 32 or 36 characters and then
                        // parsed them as a Guid (no spaces, no braces; Guid's own "0x"/"+" quirks). Today's OMV trims
                        // spaces first and so passes " <uuid>". Checked on the golden vectors ("uuidv").
                        return (str.Length == 32 || str.Length == 36) && Guid.TryParse(str, out Guid _) ? 1 : 0;
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

            private static string EncodeBase16(byte[] bytes)
            {
                StringBuilder output = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes)
                {
                    output.AppendFormat("{0:X2}", b);
                }
                return output.ToString().ToLowerInvariant();
            }

            // As in Halcyon, a letter anywhere fails ("!IsDigit(c) || (c >= 'a' && c <= 'f')").
            // Framework: Char.IsDigit from .NET Framework's tables.
            private static int ValidateBase16(string str)
            {
                int i = 0;
                if (str.StartsWith("0x", StringComparison.Ordinal)) i = 2;
                int len = str.Length;
                if (len % 2 == 1) return 0;
                for (; i < len; i++)
                {
                    char c = str[i];
                    if (!FrameworkText.IsDigit(c) || (c >= 'a' && c <= 'f'))
                    {
                        return 0;
                    }
                }
                return 1;
            }

            // Framework: Char.IsLetterOrDigit from .NET Framework's tables.
            private static int ValidateBase64(string str, char token1, char token2)
            {
                int len = str.Length;
                for (int i = 0; i < len; i++)
                {
                    char c = str[i];
                    if (!FrameworkText.IsLetterOrDigit(c) && c != token1 && c != token2)
                    {
                        if (len % 4 != 0 && (i == len - 1 || i == len - 2) && c != '=') return 0;
                    }
                }
                return 1;
            }

            // Adapted by Halcyon from public domain code by Adam Wozniak and Doran Zemlja
            // http://wiki.secondlife.com/w/index.php?title=Key_Compression#Base_4096_Script_.28Reduced_Code_Size.29
            private static string EncodeBase4k(byte[] inBytes)
            {
                StringBuilder ret = new StringBuilder();
                int len = inBytes.Length;
                int[] bytes = new int[len * 2];
                for (int i = 0; i < (len * 2); i += 2)
                {
                    bytes[i] = (inBytes[i / 2] & 0xf0) >> 4;
                    bytes[i + 1] = inBytes[i / 2] & 0xf;
                }
                len = bytes.Length;
                int extra = 0;
                for (int i = 0; i < len; i += 3)
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

                return Uri.UnescapeDataString(ret.ToString());
            }

            // Framework: Uri.EscapeDataString as .NET Framework ran it (length limit, lone high surrogates).
            private static byte[] DecodeBase4k(string str)
            {
                int extra = 0;
                if (str.EndsWith("==", StringComparison.Ordinal)) extra = 2;
                else if (str.EndsWith("=", StringComparison.Ordinal)) extra = 1;

                str = FrameworkText.EscapeDataString(str.Replace("=", String.Empty));
                byte[] inBytes = DecodeBase16(str.Replace("%", String.Empty));
                StringBuilder ret = new StringBuilder();

                int len = inBytes.Length;
                int[] bytes = new int[len * 2];
                for (int i = 0; i < len; i++)
                {
                    bytes[i * 2] = (inBytes[i] >> 4) & 0xF;
                    bytes[(i * 2) + 1] = inBytes[i] & 0xF;
                }

                len *= 2;
                for (int i = 0; i + 5 < len; i += 6)
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
                return DecodeBase16(ret.ToString());
            }

            //TODO (Halcyon)
            private static int ValidateBase4k(string str)
            {
                return -1;
            }

            // Framework: GZipStream's output and reading (FrameworkGzip).
            public static string gzipCompress(string str, string codec = "base4096")
            {
                var bytes = Encoding.Unicode.GetBytes(str);
                return Encode(FrameworkGzip.Compress(bytes), codec);
            }

            public static string gzipDecompress(string str, string codec = "base4096")
            {
                var bytes = CodecUtil.Decode(str, codec);
                if (bytes == null) throw new ArgumentNullException("buffer", "Buffer cannot be null.");   // new MemoryStream(null) in Halcyon
                return Encoding.Unicode.GetString(FrameworkGzip.Decompress(bytes));
            }

            // AesManaged in Halcyon: CBC, PKCS7.
            public static byte[] AesEncrypt(byte[] valueBytes, byte[] keyBytes, string _vector)
            {
                byte[] vectorBytes = DecodeBase16(_vector);
                using (Aes cipher = Aes.Create())
                {
                    cipher.Mode = CipherMode.CBC;
                    cipher.Padding = PaddingMode.PKCS7;
                    using (ICryptoTransform encryptor = CreateTransform(cipher, keyBytes, vectorBytes, true))
                    {
                        return encryptor.TransformFinalBlock(valueBytes, 0, valueBytes.Length);
                    }
                }
            }

            // Framework: CryptoStream.Read filled the whole buffer; .NET 10's may return part of it, which cut
            // decrypted text short. Any failure (bad key size, bad padding, no input bytes) gives "" as in Halcyon.
            public static string AesDecrypt(byte[] valueBytes, byte[] keyBytes, string _vector)
            {
                byte[] vectorBytes = DecodeBase16(_vector);
                byte[] decrypted;
                try
                {
                    if (valueBytes == null) throw new ArgumentNullException("buffer");
                    if (valueBytes.Length == 0) return String.Empty;   // Read(buffer, 0, 0) decrypted nothing
                    using (Aes cipher = Aes.Create())
                    {
                        cipher.Mode = CipherMode.CBC;
                        cipher.Padding = PaddingMode.PKCS7;
                        using (ICryptoTransform decryptor = CreateTransform(cipher, keyBytes, vectorBytes, false))
                        {
                            decrypted = decryptor.TransformFinalBlock(valueBytes, 0, valueBytes.Length);
                        }
                    }
                }
                catch
                {
                    return String.Empty;
                }
                return Encoding.Unicode.GetString(decrypted);
            }

            // Framework: AesManaged.CreateEncryptor's argument checks (null key, key size), same exception types.
            private static ICryptoTransform CreateTransform(Aes cipher, byte[] key, byte[] iv, bool encrypt)
            {
                if (key == null) throw new ArgumentNullException("key");
                if (key.Length != 16 && key.Length != 24 && key.Length != 32)
                    throw new ArgumentException("The specified key is not a valid size for this algorithm.", "key");
                return encrypt ? cipher.CreateEncryptor(key, iv) : cipher.CreateDecryptor(key, iv);
            }

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

            // Adapted by Halcyon from public domain code by Becky Pippen
            // http://wiki.secondlife.com/wiki/User:Becky_Pippen/Text_Storage
            private static string encode15BitsToChar(int num)
            {
                if (num < 0 || num >= 0x8000) return "�";
                num += 0x1000;
                return Uri.UnescapeDataString(
                    string.Format("%{0}%{1}%{2}",
                    (0xE0 + (num >> 12)).ToString("X"),
                    (0x80 + ((num >> 6) & 0x3F)).ToString("X"),
                    (0x80 + (num & 0x3F)).ToString("X")
                ));
            }

            private static int charToInt(string src, int index)
            {
                if (index < 0) index = src.Length + index;
                if (Math.Abs(index) >= src.Length) return 0;
                char c = src[index];
                return (int)c;
            }

            private static int decodeCharTo15Bits(string ch)
            {
                int t = Convert.ToChar(ch);
                return ((((t >> 12) & 0xFF) & 0x1F) << 12) +
                    ((((t >> 6) & 0xFF) & 0x3F) << 6) +
                    ((t & 0xFF) & 0x3F) - 0x1000;
            }

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
                        charToInt(str, i) << 7 | charToInt(str, i + 1)
                    ));
                }

                if (emptyEnd) encoded.Append("=");

                return encoded.ToString();
            }

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
                        int cInt15 = decodeCharTo15Bits(str.Substring(i, 1));
                        result.Append((char)(cInt15 >> 7));
                        result.Append((char)(cInt15 & 0x7f));
                    }
                }
                return result.ToString();
            }

            public static string Hash(string str, string nonce, string inCodec, string outCodec)
            {
                if (!String.IsNullOrEmpty(nonce)) str = str + ":" + nonce;
                byte[] bytes = null;
                byte[] inBytes = Encoding.UTF8.GetBytes(str);
                switch (inCodec)
                {
                    case "md5":
                        bytes = MD5.HashData(inBytes);
                        break;
                    case "sha1":
                    case "sha128":
                        bytes = SHA1.HashData(inBytes);
                        break;
                    case "sha2":
                    case "sha256":
                        bytes = SHA256.HashData(inBytes);
                        break;
                    case "sha384":
                        bytes = SHA384.HashData(inBytes);
                        break;
                    case "sha512":
                        bytes = SHA512.HashData(inBytes);
                        break;
                    default:
                        return String.Empty;
                }
                return Encode(bytes, outCodec);
            }
        }
    }
}

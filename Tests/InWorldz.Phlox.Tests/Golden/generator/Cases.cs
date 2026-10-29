// The golden-vector case list (C# 5). Every case runs Halcyon's own iwStringCodec.
using System;
using System.Text;

namespace HalcyonCodecs
{
    public static class Cases
    {
        static readonly string[] Texts = {
            "Hello, World!", "a", "ab", "héllo wörld ✓ 日本語", "😀 emoji",
            "lone \ud800 surrogate", "tab\tnl\ncr\r", "nul\u0000byte", "abcdefgh", " ", "=", "\\u00e9 literal",
        };
        static readonly string[] Encoders = { "base16", "uuid", "base64", "base64-safe", "base4096", "base4k" };
        static readonly string[] Hashes = { "md5", "sha1", "sha-1", "sha128", "sha-128", "sha2", "sha-2", "sha256", "sha-256", "sha384", "sha-384", "sha512", "sha-512" };
        static readonly int[] Sizes = { 999, 1000, 7999, 8000, 15999, 16000, 32766, 32767, 65535, 65536 };
        const string Vec = "000102030405060708090a0b0c0d0e0f";

        static string L(string g, string s, string c, int op, params object[] e) { return Program.L(g, s, c, op, e); }
        static object[] P(params object[] e) { return e; }

        public static void All()
        {
            // --- encoding codecs (the default branch) ---
            foreach (string c in Encoders)
            {
                foreach (string t in Texts)
                {
                    string enc = L("enc", t, c, 1);
                    if (enc != null) { L("enc", enc, c, 0); L("enc", enc, c, 2); }
                }
                L("enc", "", c, 0); L("enc", "", c, 1); L("enc", "", c, 2);
                L("enc", "Hello", c, 3); L("enc", "Hello", c, -1);
            }
            L("enc", "Hello", "BASE64", 1); L("enc", "SABlAGwAbABvAA==", "Base64", 0);
            L("enc", "Hello", "rot13", 0); L("enc", "Hello", "rot13", 1); L("enc", "Hello", "rot13", 2);
            L("enc", "Hello", "", 0); L("enc", "Hello", "", 1); L("enc", "", "", 1); L("enc", "Hello", " base64", 1);

            // base16 bad and odd input
            foreach (string s in new[] { "zz", "abc", "0x41", "0x", " 4", "-1", "ABCD", "4100", "41", "4100420", "g0", "4100 ", "٤١" })
            { L("b16", s, "base16", 0); L("b16", s, "base16", 2); }
            foreach (string s in new[] { "0x12", "12", "ab", "123", "0123456789", "١٢" }) L("b16", s, "base16", 2);
            // uuid
            foreach (string s in new[] { "61006200630064006500660067006800", "61006200-6300-6400-6500-660067006800",
                "00000000-0000-0000-0000-000000000000", "00000000000000000000000000000000", "{00000000-0000-0000-0000-000000000000}",
                "not-a-uuid", "4100", "6100620063006400650066006700680", "0000000000000000000000000000000z" })
            { L("uuid", s, "uuid", 0); L("uuid", s, "uuid", 2); }
            L("uuid", "abcdefg", "uuid", 1); L("uuid", "abcdefghi", "uuid", 1);
            foreach (string s in new[] { "0F8B4C9A-1234-5678-9ABC-DEF012345678", "(00000000-0000-0000-0000-000000000000)",
                " 00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-000000000000 ", "0000000000000000-0000-0000-00000000",
                "00000000-0000-0000-0000-00000000000", "00000000-0000-0000-0000-0000000000000", "000000000000000000000000000000000",
                "0000000000000000000000000000000", "urn:uuid:00000000-0000-0000-0000-000000000000", "0x00000000000000000000000000000000",
                "{0x00000000,0x0000,0x0000,{0x00,0x00,0x00,0x00,0x00,0x00,0x00,0x00}}", "00000000-0000-0000-0000-00000000000g",
                "٠٠٠٠٠٠٠٠-0000-0000-0000-000000000000", "0000000-00000-0000-0000-000000000000",
                "00000000-0000-0000-0000-000000000000x", "{00000000-0000-0000-0000-000000000000", "00000000-0000-0000-0000-00000000000}",
                "a", "-", "--------------------------------", "g0000000000000000000000000000000", "00000000 0000 0000 0000 000000000000",
                "0000000000000000000000000000000 ", " 0000000000000000000000000000000", "00000000-0000-0000-0000-00000000000 ",
                " 0000000-0000-0000-0000-000000000000", "{00000000-0000-0000-0000-00000000}", "0X000000000000000000000000000000",
                "0x000000-0000-0000-0000-000000000000", "+0000000-0000-0000-0000-000000000000", "-0000000-0000-0000-0000-000000000000",
                "00000000-+000-0000-0000-000000000000", "00000000-0000-0000-0000-+00000000000", "abcdefABCDEF0123456789abcdefABCD",
                "٠000000000000000000000000000000", "0000000000000000000000000000000٠", "00000000-0000-0000-0000_000000000000",
                "00000000000000000000000000000000".Replace('0', '０'), "{0000000000000000000000000000000}" + "000", "00000000-0000-0000-0000-0000\t0000000" })
                L("uuidv", s, "uuid", 2);
            // base64 / base64-safe
            foreach (string s in new[] { "@@@@", "Zm9v", "Zg=", "Zg", "Zg==", " Zm9v\n", "QQA=", "QQA", "QQ-_", "QQ+/", "Zm9v=", "Zm9vYg===", "====", "Z", "abc$", "ab=c" })
            { L("b64", s, "base64", 0); L("b64", s, "base64", 2); L("b64", s, "base64-safe", 0); L("b64", s, "base64-safe", 2); }
            foreach (string s in new[] { "abc", "ab=", "a$bc", "abcd", "ab$=", "a$", "$$", "éé", "abcde", "abcd$", "١٢٣٤" })
            { L("b64v", s, "base64", 2); L("b64v", s, "base64-safe", 2); }
            // base4096 bad input
            foreach (string s in new[] { "hello", "=", "==", "က", "ကက", "日本", "😀", "\ud800", "=", "a=", "ሴ噸骼", "￿", "\u0080" })
            { L("b4k", s, "base4096", 0); L("b4k", s, "base4096", 2); }

            // input codec
            L("inp", "48656c6c6f", "base64", 1, "input codec", "base16");
            L("inp", "48656c6c6f", "base64", 1, "Input Codec", "BASE16");
            L("inp", "48656c6c6f", "base4096", 1, "input codec", "base16");
            L("inp", "SGVsbG8=", "base16", 1, "input codec", "base64");
            L("inp", "SGVsbG8=", "base16", 0, "input codec", "base64");
            L("inp", "SGVsbG8=", "base16", 2, "input codec", "base64");
            L("inp", "48656c6c6f", "base64", 1, "input codec", "nope");
            L("inp", "zz", "base64", 1, "input codec", "base16");
            L("inp", "@@@", "base16", 1, "input codec", "base64");
            L("inp", "48656c6c6f", "base64", 1, "input codec");
            L("inp", "48656c6c6f", "base64", 1, "input codec", 5);
            L("inp", "48656c6c6f", "base64", 1, 5, "base16");
            L("inp", "48656c6c6f", "base64", 1, "other", "x", "input codec", "base16");
            L("inp", "", "base64", 1, "input codec", "base16");
            L("inp", "Hello", "md5", 1, "input codec", "base16");
            L("inp", "Hello", "md5", 1, "input codec", "nope");

            // --- ascii ---
            foreach (string t in Texts)
            {
                string e = L("ascii", t, "ascii", 1);
                if (e != null) L("ascii", e, "ascii", 0);
                L("ascii", t, "ascii", 2);
            }
            foreach (string s in new[] { "h\\u00e9llo", "\\u00E9", "\\uzzzz", "\\u12", "\\u12345", "\\\\u0041", "\\ud83d\\ude00", "\\ud800", "\\u0000", "\\u00g1", "\\U0041" })
            { L("ascii", s, "ascii", 0); L("ascii", s, "ascii", 2); }
            L("ascii", "", "ascii", 0); L("ascii", "", "ascii", 1); L("ascii", "", "ascii", 2); L("ascii", "xé", "ascii", 3);
            L("ascii", "x", "ASCII", 1);

            // --- gzip ---
            foreach (string oc in new[] { null, "base64", "base16", "base64-safe", "uuid", "base4096", "BASE64" })
            {
                foreach (string t in Texts)
                {
                    object[] p = oc == null ? P() : P("output codec", oc);
                    string e = L("gzip", t, "gzip", 1, p);
                    if (e != null) L("gzip", e, "gzip", 0, p);
                }
            }
            L("gzip", "Hello", "gzip", 1, "output codec", "rot13");
            L("gzip", "Hello", "gzip", 0, "output codec", "rot13");
            L("gzip", "Hello", "gzip", 2);
            L("gzip", "Hello", "gzip", 3);
            L("gzip", "", "gzip", 1); L("gzip", "", "gzip", 0);
            L("gzip", "SGVsbG8=", "gzip", 0, "output codec", "base64");
            L("gzip", "@@@", "gzip", 0, "output codec", "base64");
            L("gzip", "hello", "gzip", 0);
            L("gzip", "Hello", "gzip", 1, "Output Codec", "base64", "output codec", "base16");
            L("gzip", "Hello", "gzip", 1, "nonce", "x", "output codec", "base64");
            L("gzip", "Hello", "gzip", 1, "output codec", "base64", "x");
            L("gzip", "Hello", "gzip", 1, "output codec");
            {
                // Streams other gzip writers produce, and damaged ones (hex, output codec base16).
                string a = CodecUtilAccess.Gz("first", "base16"), b = CodecUtilAccess.Gz("second", "base16");
                L("gzipx", a + b, "gzip", 0, "output codec", "base16");                     // two members
                L("gzipx", a.Substring(0, a.Length - 16), "gzip", 0, "output codec", "base16"); // no trailer
                L("gzipx", a.Substring(0, a.Length - 8), "gzip", 0, "output codec", "base16");  // half trailer
                string badCrc = a.Substring(0, a.Length - 16) + "00000000" + a.Substring(a.Length - 8);
                L("gzipx", badCrc, "gzip", 0, "output codec", "base16");
                string badLen = a.Substring(0, a.Length - 8) + "00000000";
                L("gzipx", badLen, "gzip", 0, "output codec", "base16");
                L("gzipx", a + "00", "gzip", 0, "output codec", "base16");                  // trailing garbage
                L("gzipx", a.Substring(0, 20), "gzip", 0, "output codec", "base16");       // header only
                // The same member with FLG.FNAME, an mtime and OS=3 in the header (as gzip(1) writes it).
                string fname = "1f8b0808" + "7b000000" + "00" + "03" + "612e74787400" + a.Substring(20);
                L("gzipx", fname, "gzip", 0, "output codec", "base16");
                // A member holding no data (Halcyon's own compressor on "").
                L("gzipx", CodecUtilAccess.Gz("", "base16"), "gzip", 0, "output codec", "base16");
                L("gzipx", a, "gzip", 0, "output codec", "base16");
            }

            // Streams crafted per RFC 1952/1951 (craft.py), decoded by Halcyon's gzipDecompress.
            foreach (string line in System.IO.File.ReadAllLines(Program.CraftedPath))
            {
                string[] parts = line.Split(' ');
                L("gzipc:" + parts[0], parts[1], "gzip", 0, "output codec", "base16");
            }
            // Deflate coverage: pseudo-random and low-entropy texts (Program.Gen "rnd", "ab", "runs").
            foreach (string k in new[] { "rnd", "ab", "runs", "words" })
                foreach (int n in new[] { 1, 2, 3, 5, 100, 1000, 5000, 16384, 20000, 40000, 65536 })
                    Program.GD("deflate", k, n, "gzip", P("output codec", "base64"), "gzip", P("output codec", "base64"));

            // --- ascii-zip ---
            foreach (string t in Texts)
            {
                string e = L("azip", t, "ascii-zip", 1);
                if (e != null) L("azip", e, "ascii-zip", 0);
                L("azip", t, "ascii-zip", 2);
            }
            foreach (string s in new[] { "=", "A", "AB=", "日本語", "\ud800", "က", "迿", "退", "￿", "x=", "==", "\u0000" })
                L("azip", s, "ascii-zip", 0);
            L("azip", "", "ascii-zip", 0); L("azip", "", "ascii-zip", 1); L("azip", "abc", "ascii-zip", 3);

            // --- hashes ---
            foreach (string h in Hashes)
            {
                L("hash", "Hello, World!", h, 1);
                L("hash", "Hello, World!", h, 0);
                L("hash", "Hello, World!", h, 1, "nonce", "n0nce");
            }
            foreach (string t in Texts) { L("hash", t, "sha256", 1); L("hash", t, "md5", 1, "nonce", "é"); }
            L("hash", "", "md5", 1); L("hash", "", "sha512", 1); L("hash", "", "md5", 1, "nonce", "x");
            L("hash", "abc", "md5", 1, "nonce", "");
            L("hash", "abc", "MD5", 1); L("hash", "abc", "Sha256", 7);
            foreach (string oc in new[] { "base64", "base64-safe", "uuid", "base4096", "base16", "BASE64", "rot13", "" })
            { L("hash", "abc", "sha256", 1, "output codec", oc); L("hash", "abc", "md5", 1, "output codec", oc); }
            L("hash", "abc", "sha1", 1, "Output Codec", "base64", "NONCE", "x");
            L("hash", "abc", "sha1", 1, "nonce");
            L("hash", "abc", "sha1", 1, "nonce", "a", "nonce", "b");
            L("hash", "abc", "sha1", 1, "nonce", 5);

            // --- aes-key ---
            L("aeskey", "password", "aes-key", 1, "salt", "saltsalt");
            L("aeskey", "password", "aes-key", 0, "salt", "saltsalt");
            L("aeskey", "password", "aes-key", 2, "salt", "saltsalt");
            L("aeskey", "password", "AES-KEY", 1, "SALT", "saltsalt", "Rounds", "16");
            foreach (string r in new[] { "1", "8", "99", "100", "101", "1024", "1025", "0", "-1", "abc", " 16", "+5", "16.5", "", "0x10", "2147483648" })
                L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "rounds", r);
            foreach (string oc in new[] { "base64", "uuid", "base4096", "base64-safe", "rot13", "BASE64", "" })
                L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "output codec", oc);
            L("aeskey", "password", "aes-key", 1, "salt", "");
            L("aeskey", "password", "aes-key", 1);
            L("aeskey", "password", "aes-key", 1, "salt", "abc");
            L("aeskey", "password", "aes-key", 1, "salt", "1234567");
            L("aeskey", "password", "aes-key", 1, "salt", "éééé");
            L("aeskey", "pässwörd ✓", "aes-key", 1, "salt", "sält sält");
            L("aeskey", "lone \ud800", "aes-key", 1, "salt", "saltsalt");
            L("aeskey", "", "aes-key", 1, "salt", "saltsalt");
            L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "rounds");
            L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "rounds", 16);
            L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "rounds", 16.0f);
            L("aeskey", "password", "aes-key", 1, "salt", 12345678);
            L("aeskey", "password", "aes-key", 1, "salt", 1.5f);
            L("hash", "abc", "md5", 1, "nonce", 7);
            L("hash", "abc", "md5", 1, "nonce", -0.25f);
            L("inp", "48656c6c6f", "base64", 1, "input codec", "base16", "x");
            L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "rounds", "2000", "salt", "");
            L("aeskey", "password", "aes-key", 1, "salt", "", "rounds", "2000");
            L("aeskey", "password", "aes-key", 1, "salt", "saltsalt", "rounds", "4", "rounds", "500");

            // --- aes ---
            string key = L("aes", "password", "aes-key", 1, "salt", "saltsalt");
            string key64 = L("aes", "password", "aes-key", 1, "salt", "saltsalt", "output codec", "base64");
            string k16 = "000102030405060708090a0b0c0d0e0f";
            foreach (string oc in new[] { null, "base64", "base16", "base64-safe", "uuid", "base4096", "BASE64" })
            {
                foreach (string t in Texts)
                {
                    object[] p = oc == null ? P("key", key, "vector", Vec) : P("key", key, "vector", Vec, "output codec", oc);
                    string e = L("aes", t, "aes", 1, p);
                    if (e != null) L("aes", e, "aes", 0, p);
                }
            }
            L("aes", "Hello", "aes", 1, "key", key64, "key codec", "base64", "vector", Vec, "output codec", "base64");
            L("aes", "Hello", "aes", 1, "key", k16, "vector", Vec, "output codec", "base64");
            string c192 = L("aes", "Hello", "aes", 1, "key", k16 + "1011121314151617", "vector", Vec, "output codec", "base64");
            L("aes", c192, "aes", 0, "key", k16 + "1011121314151617", "vector", Vec, "output codec", "base64");
            L("aes", "Hello", "aes", 1, "key", "0001020304050607", "vector", Vec, "output codec", "base64");
            L("aes", "SGVsbG8gV29ybGQhISEhISEhIQ==", "aes", 0, "key", "0001020304050607", "vector", Vec, "output codec", "base64");
            L("aes", "Hello", "aes", 1, "key", key, "vector", "00010203-0405-0607-0809-0a0b0c0d0e0f", "output codec", "base64");
            L("aes", "Hello", "aes", 1, "key", key, "vector", "000102030405060708090a0b0c0d0e0", "output codec", "base64");
            L("aes", "Hello", "aes", 1, "key", key, "vector", "zz0102030405060708090a0b0c0d0e0f", "output codec", "base64");
            L("aes", "SGVsbG8=", "aes", 0, "key", key, "vector", "zz0102030405060708090a0b0c0d0e0f", "output codec", "base64");
            L("aes", "Hello", "aes", 1, "vector", Vec);
            L("aes", "Hello", "aes", 1, "key", key);
            L("aes", "Hello", "aes", 1, "key", key, "vector", Vec, "output codec", "");
            L("aes", "Hello", "aes", 1, "key", key, "vector", Vec, "output codec", "rot13");
            L("aes", "Hello", "aes", 1, "key", key, "vector", Vec, "key codec", "rot13");
            L("aes", "Hello", "aes", 1, "key", "@@@", "vector", Vec, "key codec", "base64");
            L("aes", "Hello", "aes", 1, "key", "zz", "vector", Vec);
            L("aes", "Hello", "aes", 2, "key", key, "vector", Vec);
            L("aes", "Hello", "aes", 3, "key", key, "vector", Vec);
            L("aes", "", "aes", 1, "key", key, "vector", Vec);
            L("aes", "", "aes", 0, "key", key, "vector", Vec);
            L("aes", "Hello", "AES", 1, "KEY", key, "VECTOR", Vec, "Output Codec", "base64");
            {
                string good = L("aes", "Secret text", "aes", 1, "key", key, "vector", Vec, "output codec", "base64");
                L("aes", good, "aes", 0, "key", k16 + k16, "vector", Vec, "output codec", "base64");       // wrong key
                L("aes", good, "aes", 0, "key", key, "vector", "0f0e0d0c0b0a09080706050403020100", "output codec", "base64"); // wrong vector
                L("aes", "@@@@", "aes", 0, "key", key, "vector", Vec, "output codec", "base64");          // bad base64
                L("aes", "QUJD", "aes", 0, "key", key, "vector", Vec, "output codec", "base64");          // not a block
                L("aes", "", "aes", 0, "key", key, "vector", Vec, "output codec", "base64");
                L("aes", good.Substring(0, good.Length - 4), "aes", 0, "key", key, "vector", Vec, "output codec", "base64");
            }
            // input codec (bytes in)
            {
                string b = L("aes", "48656c6c6f", "aes", 1, "key", key, "vector", Vec, "output codec", "base64", "input codec", "base16");
                if (b != null) L("aes", b, "aes", 0, "key", key, "vector", Vec, "output codec", "base64", "input codec", "base64");
                string u = L("aes", "Hello", "aes", 1, "key", key, "vector", Vec, "output codec", "base16");
                if (u != null) L("aes", u, "aes", 0, "key", key, "vector", Vec, "output codec", "base64", "input codec", "base16");
                L("aes", "zz", "aes", 1, "key", key, "vector", Vec, "input codec", "base16");
            }
            L("aes", "Hello", "aes", 1, "key", key, "vector", Vec, "output codec");

            // --- large inputs (sleeps, limits) ---
            foreach (int n in Sizes)
            {
                Program.G("size", "ascii", n, "base16", 1);
                Program.GD("size", "ascii", n, "base16", P(), "base16", P());
                Program.GD("size", "ascii", n, "base64", P(), "base64", P());
                Program.GD("size", "ascii", n, "base4096", P(), "base4096", P());
                Program.GD("size", "ascii", n, "gzip", P(), "gzip", P());
                Program.GD("size", "ascii", n, "gzip", P("output codec", "base64"), "gzip", P("output codec", "base64"));
                Program.GD("size", "ascii", n, "ascii-zip", P(), "ascii-zip", P());
                Program.G("size", "ascii", n, "sha256", 1);
                Program.G("size", "ascii", n, "ascii", 1);
                Program.G("size", "ascii", n, "base64", 2);
                Program.G("size", "ascii", n, "aes-key", 1, "salt", "saltsalt");
                Program.GD("size", "ascii", n, "aes", P("key", key, "vector", Vec), "aes", P("key", key, "vector", Vec));
                Program.GD("size", "uni", n, "base64", P(), "base64", P());
                Program.GD("size", "uni", n, "base4096", P(), "base4096", P());
                Program.GD("size", "uni", n, "gzip", P(), "gzip", P());
                Program.G("size", "uni", n, "ascii", 1);
                Program.GD("size", "uni", n, "ascii-zip", P(), "ascii-zip", P());
                Program.G("size", "uni", n, "md5", 1);
            }
        }
    }

    public static class CodecUtilAccess
    {
        public static string Gz(string s, string codec) { return Api.CodecUtil.gzipCompress(s, codec); }
    }
}

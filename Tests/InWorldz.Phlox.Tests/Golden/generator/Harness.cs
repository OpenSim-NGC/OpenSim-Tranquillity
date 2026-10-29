// Scratch harness around Halcyon's iwStringCodec/CodecUtil (HalcyonCodec.cs, extracted verbatim).
// Runs on .NET Framework 4.8.1 (the entry assembly declares v4.7.1, Halcyon's TargetFrameworkVersion).
// Writes golden vectors as JSON lines. C# 5 (the in-box csc).
using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace HalcyonCodecs
{
    public partial class Api
    {
        public List<string> Errors = new List<string>();
        public List<int> Sleeps = new List<int>();
        // Halcyon LSLSystemAPI.cs:14510-14513: LSLError -> ScriptShoutError("LSL Runtime Error: " + msg) (:14483-14486)
        // -> SimChat(DEBUG_CHANNEL, msg, Direct, owner) (:1041-1045), which chats and then ScriptSleep(15).
        internal void LSLError(string msg) { Errors.Add("LSL Runtime Error: " + msg); ScriptSleep(15); }
        // Halcyon LSLSystemAPI.cs:144-155: ScriptSleep sets NextWakeup = now + delay (a later call replaces an earlier one).
        protected void ScriptSleep(int delay) { Sleeps.Add(delay); }
    }

    public static class Program
    {
        static StreamWriter W;
        static int N;

        public static string Gen(string kind, int n)
        {
            string pat;
            if (kind == "ascii") pat = "The quick brown fox jumps over the lazy dog 0123456789. ";
            else if (kind == "uni") pat = "héllo wörld ✓ 日本語 ";
            else if (kind == "rnd" || kind == "ab" || kind == "runs" || kind == "words")
            {
                // Deterministic LCG (the tests rebuild the same text): x = x * 1103515245 + 12345 mod 2^31.
                uint x = 12345;
                StringBuilder r = new StringBuilder(n);
                string[] words = { "the ", "quick ", "brown ", "fox ", "jumps ", "over ", "lazy ", "dog ", "été ", "日本 " };
                while (r.Length < n)
                {
                    x = (x * 1103515245u + 12345u) & 0x7fffffffu;
                    uint v = x >> 8;
                    if (kind == "rnd") r.Append((char)(0x20 + v % 0xD000));
                    else if (kind == "ab") r.Append(v % 2 == 0 ? 'a' : 'b');
                    else if (kind == "runs") r.Append((char)('a' + v % 3), (int)(1 + (v >> 4) % 300));
                    else r.Append(words[v % (uint)words.Length]);
                }
                return r.ToString(0, n);
            }
            else throw new Exception("gen " + kind);
            StringBuilder sb = new StringBuilder(n + pat.Length);
            while (sb.Length < n) sb.Append(pat);
            return sb.ToString(0, n);
        }

        public static string J(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in s)
            {
                if (c == '"') sb.Append("\\\"");
                else if (c == '\\') sb.Append("\\\\");
                else if (c < 0x20 || c > 0x7e) sb.Append("\\u" + ((int)c).ToString("x4"));
                else sb.Append(c);
            }
            return sb.Append('"').ToString();
        }

        public static string Sha(string s)
        {
            using (SHA256 h = SHA256.Create())
            {
                byte[] b = h.ComputeHash(Encoding.Unicode.GetBytes(s));
                StringBuilder sb = new StringBuilder();
                foreach (byte x in b) sb.Append(x.ToString("x2"));
                return sb.ToString();
            }
        }

        // One case. inputJson is how the input is written (a literal, or a generator spec).
        static string Case(string group, string str, string inputJson, string codec, int op, object[] extra)
        {
            Api api = new Api();
            string ret = null; string exType = null; string exMsg = null;
            try { ret = api.iwStringCodec(str, codec, op, new InWorldz.Phlox.Types.LSLList(extra)); }
            catch (Exception e) { exType = e.GetType().FullName; exMsg = e.Message; }
            StringBuilder sb = new StringBuilder();
            sb.Append("{\"id\":").Append(N++).Append(",\"group\":").Append(J(group));
            sb.Append(",\"codec\":").Append(J(codec)).Append(",\"op\":").Append(op);
            sb.Append(",\"input\":").Append(inputJson);
            sb.Append(",\"params\":[");
            for (int i = 0; i < extra.Length; i++)
            {
                if (i > 0) sb.Append(',');
                if (extra[i] is string) sb.Append(J((string)extra[i])); else if (extra[i] is float) sb.Append("{\"float\":" + ((float)extra[i]).ToString("R", System.Globalization.CultureInfo.InvariantCulture) + "}"); else sb.Append(extra[i].ToString());
            }
            sb.Append("]");
            if (exType != null) sb.Append(",\"exception\":").Append(J(exType)).Append(",\"exceptionMessage\":").Append(J(exMsg));
            else if (ret == null) sb.Append(",\"ret\":null");
            else if (ret.Length > 400) sb.Append(",\"retLen\":").Append(ret.Length).Append(",\"retSha256\":").Append(J(Sha(ret))).Append(",\"retHead\":").Append(J(ret.Substring(0, 40)));
            else sb.Append(",\"ret\":").Append(J(ret));
            sb.Append(",\"errors\":[");
            for (int i = 0; i < api.Errors.Count; i++) { if (i > 0) sb.Append(','); sb.Append(J(api.Errors[i])); }
            sb.Append("],\"sleeps\":[");
            for (int i = 0; i < api.Sleeps.Count; i++) { if (i > 0) sb.Append(','); sb.Append(api.Sleeps[i]); }
            sb.Append("]}");
            W.WriteLine(sb.ToString());
            return exType != null ? null : ret;
        }

        static string Lit(string group, string str, string codec, int op, params object[] extra)
        {
            return Case(group, str, J(str), codec, op, extra);
        }

        static string GenCase(string group, string kind, int n, string codec, int op, params object[] extra)
        {
            return Case(group, Gen(kind, n), "{\"gen\":" + J(kind) + ",\"n\":" + n + "}", codec, op, extra);
        }

        // Decode a large encoded text: the input is the Halcyon encoding of Gen(kind,n) with encCodec (and encParams),
        // recorded by reference to that encode case's id; the test rebuilds it and checks its hash first.
        static void GenDecode(string group, string kind, int n, string encCodec, object[] encParams, string codec, object[] extra)
        {
            int encId = N;
            string enc = Case(group, Gen(kind, n), "{\"gen\":" + J(kind) + ",\"n\":" + n + "}", encCodec, 1, encParams);
            if (enc == null) return;
            Case(group, enc, "{\"fromCase\":" + encId + "}", codec, 0, extra);
        }

        public static int Main(string[] args)
        {
            CraftedPath = args[1];
            W = new StreamWriter(args[0], false, new UTF8Encoding(false));
            W.NewLine = "\n";
            Console.WriteLine("runtime " + Environment.Version + " " + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
            Cases.All();
            W.Close();
            Console.WriteLine("cases " + N);
            UriVectors(args[2]);
            return 0;
        }

        public static string CraftedPath;

        // Framework's Uri.EscapeDataString (called by Halcyon's DecodeBase4k) on short strings built from a
        // deterministic LCG over an alphabet of ASCII, non-ASCII BMP, high and low surrogates; plus runs that
        // cross 32 to 48 non-ASCII chars. JSON lines: {"s":..., "r":... | "ex":...}.
        static void UriVectors(string path)
        {
            char[] alpha = { 'a', 'Z', '0', '-', '~', ' ', '%', 'é', '日', '\ud800', '\udbff', '\udc00', '\udfff', '￿', '\u0080' };
            uint x = 777;
            using (StreamWriter w = new StreamWriter(path, false, new UTF8Encoding(false)))
            {
                w.NewLine = "\n";
                List<string> inputs = new List<string>();
                for (int i = 0; i < 3000; i++)
                {
                    x = (x * 1103515245u + 12345u) & 0x7fffffffu;
                    int len = (int)((x >> 8) % 8) + 1;
                    StringBuilder sb = new StringBuilder();
                    for (int j = 0; j < len; j++) { x = (x * 1103515245u + 12345u) & 0x7fffffffu; sb.Append(alpha[(x >> 8) % (uint)alpha.Length]); }
                    inputs.Add(sb.ToString());
                }
                string[] tails = { "\ud800𐀀", "𐀀", "\ud800x", "\ud800\ud800x", "\udc00", "𐀀𐀀", "\ud800" };
                for (int n = 30; n <= 50; n++)
                    foreach (string t in tails)
                    {
                        inputs.Add(new string('é', n) + t);
                        inputs.Add(new string('é', n) + t + "tail");
                        inputs.Add("ab" + new string('日', n) + t + "é");
                    }
                foreach (string s in inputs)
                {
                    string r = null, ex = null;
                    try { r = Uri.EscapeDataString(s); } catch (Exception e) { ex = e.GetType().FullName + ": " + e.Message; }
                    w.WriteLine("{\"s\":" + J(s) + (ex == null ? ",\"r\":" + J(r) : ",\"ex\":" + J(ex)) + "}");
                }
                Console.WriteLine("uri vectors " + inputs.Count);
            }
        }

        // Exposed for Cases.cs
        public static string L(string g, string s, string c, int op, params object[] e) { return Lit(g, s, c, op, e); }
        public static string G(string g, string k, int n, string c, int op, params object[] e) { return GenCase(g, k, n, c, op, e); }
        public static void GD(string g, string k, int n, string ec, object[] ep, string c, object[] e) { GenDecode(g, k, n, ec, ep, c, e); }
    }
}

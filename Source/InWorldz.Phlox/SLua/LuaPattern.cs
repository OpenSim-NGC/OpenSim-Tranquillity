using System;
using System.Collections.Generic;
using System.Text;

using InWorldz.Phlox.Types;

namespace InWorldz.Phlox.SLua
{
    /// <summary>
    /// Faithful hand-written Lua pattern matcher (a port of Lua 5.x lstrlib.c), NOT a regex engine.
    /// Lua patterns are their own language; delegating to System.Text.RegularExpressions would
    /// silently mismatch. Implements: character classes %a %d %l %u %s %w %p %c %x (and uppercase
    /// negations), sets [..]/[^..]/ranges/classes-in-sets, '.', %-escapes, quantifiers * + - ?
    /// (greedy/lazy with backtracking), anchors ^ $, captures (...) incl. position captures (),
    /// back-references %1..%9, and %b (balanced). %f (frontier) is supported. Backs string.find/
    /// match/gmatch/gsub in LuaLib.
    /// </summary>
    public static class LuaPattern
    {
        private const char L_ESC = '%';
        private const int CAP_UNFINISHED = -1;
        private const int CAP_POSITION = -2;
        private const int MAXCAPTURES = 32;
        private const int MAXCCALLS = 200;

        private sealed class MatchState
        {
            public string src;
            public string p;
            public int level;
            public readonly int[] capStart = new int[MAXCAPTURES];
            public readonly int[] capLen = new int[MAXCAPTURES]; // len, or CAP_UNFINISHED / CAP_POSITION
            public int matchdepth = MAXCCALLS;
        }

        private static bool ClassMatch(char c, char cl)
        {
            bool res;
            char lower = char.ToLowerInvariant(cl);
            switch (lower)
            {
                case 'a': res = char.IsLetter(c); break;
                case 'd': res = (c >= '0' && c <= '9'); break;
                case 'l': res = (c >= 'a' && c <= 'z'); break;
                case 'u': res = (c >= 'A' && c <= 'Z'); break;
                case 's': res = (c == ' ' || c == '\t' || c == '\n' || c == '\r' || c == '\v' || c == '\f'); break;
                case 'w': res = char.IsLetterOrDigit(c); break;
                case 'c': res = char.IsControl(c); break;
                case 'p': res = (!char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !char.IsControl(c) && c >= 33 && c <= 126); break;
                case 'x': res = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'); break;
                case 'g': res = (c > 32 && c < 127); break;
                default: return cl == c; // not a class letter -> literal
            }
            if (char.IsUpper(cl)) res = !res; // %A %D ... negate
            return res;
        }

        // index just past the single pattern item starting at p
        private static int ClassEnd(MatchState ms, int p)
        {
            char c = ms.p[p++];
            if (c == L_ESC)
            {
                if (p >= ms.p.Length) throw new CheckException("malformed pattern (ends with '%')");
                return p + 1;
            }
            if (c == '[')
            {
                if (p < ms.p.Length && ms.p[p] == '^') p++;
                do // look for ']'
                {
                    if (p >= ms.p.Length) throw new CheckException("malformed pattern (missing ']')");
                    c = ms.p[p++];
                    if (c == L_ESC && p < ms.p.Length) p++; // skip escapes like %]
                } while (p >= ms.p.Length || ms.p[p] != ']');
                return p + 1;
            }
            return p;
        }

        private static bool MatchBracketClass(MatchState ms, char c, int p, int ec)
        {
            bool sig = true;
            p++; // skip '['
            if (ms.p[p] == '^') { sig = false; p++; }
            while (p < ec)
            {
                if (ms.p[p] == L_ESC)
                {
                    p++;
                    if (ClassMatch(c, ms.p[p])) return sig;
                    p++;
                }
                else if (p + 2 < ec && ms.p[p + 1] == '-')
                {
                    if (ms.p[p] <= c && c <= ms.p[p + 2]) return sig;
                    p += 3;
                }
                else
                {
                    if (ms.p[p] == c) return sig;
                    p++;
                }
            }
            return !sig;
        }

        private static bool SingleMatch(MatchState ms, int s, int p, int ep)
        {
            if (s >= ms.src.Length) return false;
            char c = ms.src[s];
            switch (ms.p[p])
            {
                case '.': return true;
                case L_ESC: return ClassMatch(c, ms.p[p + 1]);
                case '[': return MatchBracketClass(ms, c, p, ep - 1);
                default: return ms.p[p] == c;
            }
        }

        private static int MatchBalance(MatchState ms, int s, int p)
        {
            if (p + 1 >= ms.p.Length) throw new CheckException("malformed pattern (missing arguments to '%b')");
            if (s >= ms.src.Length || ms.src[s] != ms.p[p]) return -1;
            char b = ms.p[p], e = ms.p[p + 1];
            int cont = 1;
            s++;
            while (s < ms.src.Length)
            {
                if (ms.src[s] == e) { if (--cont == 0) return s + 1; }
                else if (ms.src[s] == b) cont++;
                s++;
            }
            return -1;
        }

        private static int MaxExpand(MatchState ms, int s, int p, int ep)
        {
            int i = 0;
            while (SingleMatch(ms, s + i, p, ep)) i++;
            while (i >= 0)
            {
                int r = Match(ms, s + i, ep + 1);
                if (r != -1) return r;
                i--;
            }
            return -1;
        }

        private static int MinExpand(MatchState ms, int s, int p, int ep)
        {
            for (; ; )
            {
                int r = Match(ms, s, ep + 1);
                if (r != -1) return r;
                else if (SingleMatch(ms, s, p, ep)) s++;
                else return -1;
            }
        }

        private static int StartCapture(MatchState ms, int s, int p, int what)
        {
            int level = ms.level;
            if (level >= MAXCAPTURES) throw new CheckException("too many captures");
            ms.capStart[level] = s;
            ms.capLen[level] = what;
            ms.level = level + 1;
            int r = Match(ms, s, p);
            if (r == -1) ms.level--;
            return r;
        }

        private static int EndCapture(MatchState ms, int s, int p)
        {
            int l = -1;
            for (int i = ms.level - 1; i >= 0; i--) if (ms.capLen[i] == CAP_UNFINISHED) { l = i; break; }
            if (l < 0) throw new CheckException("invalid pattern capture");
            ms.capLen[l] = s - ms.capStart[l];
            int r = Match(ms, s, p);
            if (r == -1) ms.capLen[l] = CAP_UNFINISHED;
            return r;
        }

        private static int MatchCapture(MatchState ms, int s, int idx)
        {
            if (idx < 0 || idx >= ms.level || ms.capLen[idx] == CAP_UNFINISHED)
                throw new CheckException("invalid capture index %" + (idx + 1));
            int len = ms.capLen[idx];
            if (ms.src.Length - s >= len && string.CompareOrdinal(ms.src, ms.capStart[idx], ms.src, s, len) == 0)
                return s + len;
            return -1;
        }

        // core recursive matcher: returns end index in src, or -1
        private static int Match(MatchState ms, int s, int p)
        {
            if (ms.matchdepth-- == 0) throw new CheckException("pattern too complex");
            try
            {
                while (p != ms.p.Length)
                {
                    char pc = ms.p[p];
                    if (pc == '(')
                    {
                        if (p + 1 < ms.p.Length && ms.p[p + 1] == ')') return StartCapture(ms, s, p + 2, CAP_POSITION);
                        return StartCapture(ms, s, p + 1, CAP_UNFINISHED);
                    }
                    if (pc == ')') return EndCapture(ms, s, p + 1);
                    if (pc == '$' && p + 1 == ms.p.Length) return (s == ms.src.Length) ? s : -1;
                    if (pc == L_ESC && p + 1 < ms.p.Length)
                    {
                        char nx = ms.p[p + 1];
                        if (nx == 'b') { s = MatchBalance(ms, s, p + 2); if (s == -1) return -1; p += 4; continue; }
                        if (nx == 'f')
                        {
                            p += 2;
                            if (p >= ms.p.Length || ms.p[p] != '[') throw new CheckException("missing '[' after '%f' in pattern");
                            int ep2 = ClassEnd(ms, p);
                            char prev = (s == 0) ? '\0' : ms.src[s - 1];
                            char cur = (s < ms.src.Length) ? ms.src[s] : '\0';
                            if (!MatchBracketClass(ms, prev, p, ep2 - 1) && MatchBracketClass(ms, cur, p, ep2 - 1)) { p = ep2; continue; }
                            return -1;
                        }
                        if (nx >= '0' && nx <= '9')
                        {
                            s = MatchCapture(ms, s, nx - '1');
                            if (s == -1) return -1;
                            p += 2; continue;
                        }
                        // else: %x escaped class -> falls through to default single-item handling
                    }

                    // default: a single pattern item (class/set/literal/.) possibly with a quantifier
                    int ep = ClassEnd(ms, p);
                    bool m = SingleMatch(ms, s, p, ep);
                    char q = (ep < ms.p.Length) ? ms.p[ep] : '\0';
                    if (q == '?')
                    {
                        if (m) { int r = Match(ms, s + 1, ep + 1); if (r != -1) return r; }
                        p = ep + 1; continue;
                    }
                    if (q == '+') return m ? MaxExpand(ms, s + 1, p, ep) : -1;
                    if (q == '*') return MaxExpand(ms, s, p, ep);
                    if (q == '-') return MinExpand(ms, s, p, ep);
                    if (!m) return -1;
                    s++; p = ep; // no quantifier: consume one and continue
                }
                return s;
            }
            finally { ms.matchdepth++; }
        }

        // Extract capture i as a value (string or 1-based position number).
        private static object GetCapture(MatchState ms, int i, int s, int e)
        {
            if (i >= ms.level)
            {
                if (i == 0) return ms.src.Substring(s, e - s); // whole match when no captures
                throw new CheckException("invalid capture index %" + (i + 1));
            }
            if (ms.capLen[i] == CAP_POSITION) return (float)(ms.capStart[i] + 1); // 1-based
            return ms.src.Substring(ms.capStart[i], ms.capLen[i]);
        }

        private static List<object> PushCaptures(MatchState ms, int s, int e, bool wholeIfNone)
        {
            int n = (ms.level == 0 && wholeIfNone) ? 1 : ms.level;
            var res = new List<object>(n);
            for (int i = 0; i < n; i++) res.Add(GetCapture(ms, i, s, e));
            return res;
        }

        // ---- public API (indices Lua-style: 1-based init; find returns 1-based start/end) ----

        // string.find: returns {startInt, endInt, caps...} or null (no match). plain = literal find.
        public static List<object> Find(string s, string pat, int init, bool plain)
        {
            init = PosRelat(init, s.Length);
            if (init < 1) init = 1; else if (init > s.Length + 1) return null;
            int sInit = init - 1;

            if (plain || NoSpecials(pat))
            {
                int idx = s.IndexOf(pat, sInit, StringComparison.Ordinal);
                if (idx < 0) return null;
                return new List<object> { (float)(idx + 1), (float)(idx + pat.Length) };
            }

            var ms = new MatchState { src = s, p = pat };
            bool anchor = pat.Length > 0 && pat[0] == '^';
            int pp = anchor ? 1 : 0;
            int sp = sInit;
            do
            {
                ms.level = 0;
                ms.matchdepth = MAXCCALLS;
                int e = Match(ms, sp, pp);
                if (e != -1)
                {
                    var res = new List<object> { (float)(sp + 1), (float)e };
                    res.AddRange(PushCaptures(ms, sp, e, false));
                    return res;
                }
                sp++;
            } while (sp <= s.Length && !anchor);
            return null;
        }

        // string.match: returns {caps...} (or {wholeMatch}) or null.
        public static List<object> MatchOne(string s, string pat, int init)
        {
            init = PosRelat(init, s.Length);
            if (init < 1) init = 1; else if (init > s.Length + 1) return null;
            var ms = new MatchState { src = s, p = pat };
            bool anchor = pat.Length > 0 && pat[0] == '^';
            int pp = anchor ? 1 : 0;
            int sp = init - 1;
            do
            {
                ms.level = 0;
                ms.matchdepth = MAXCCALLS;
                int e = Match(ms, sp, pp);
                if (e != -1) return PushCaptures(ms, sp, e, true);
                sp++;
            } while (sp <= s.Length && !anchor);
            return null;
        }

        // gmatch step: advance from pos (0-based), return captures + new pos, or null if done.
        public static List<object> GMatchStep(string s, string pat, ref int pos)
        {
            var ms = new MatchState { src = s, p = pat };
            while (pos <= s.Length)
            {
                ms.level = 0;
                ms.matchdepth = MAXCCALLS;
                int e = Match(ms, pos, 0);
                if (e != -1)
                {
                    var caps = PushCaptures(ms, pos, e, true);
                    pos = (e > pos) ? e : pos + 1; // advance; force progress on empty match
                    return caps;
                }
                pos++;
            }
            return null;
        }

        // string.gsub with a STRING replacement (%0 = whole match, %1..%9 = captures, %% = literal).
        public static void GSubString(string s, string pat, string repl, int maxN, out string result, out int count)
        {
            var ms = new MatchState { src = s, p = pat };
            bool anchor = pat.Length > 0 && pat[0] == '^';
            int pp = anchor ? 1 : 0;
            var sb = new StringBuilder(s.Length);
            int sp = 0;
            count = 0;
            while (count < maxN)
            {
                ms.level = 0;
                ms.matchdepth = MAXCCALLS;
                int e = Match(ms, sp, pp);
                if (e != -1)
                {
                    count++;
                    AppendReplacement(ms, sb, sp, e, repl);
                }
                if (e != -1 && e > sp) sp = e;                       // matched + advanced
                else if (sp < s.Length) sb.Append(s[sp++]);          // no match / empty: keep one char
                else break;
                if (anchor) break;
            }
            if (sp < s.Length) sb.Append(s.Substring(sp));
            result = sb.ToString();
        }

        // string.gsub with a FUNCTION replacement: repl(captures) -> replacement string (nil/false
        // keeps the original match). The caller supplies the invoke callback (re-entrant into the VM).
        public static void GSubFunc(string s, string pat, Func<List<object>, object> repl, int maxN, out string result, out int count)
        {
            var ms = new MatchState { src = s, p = pat };
            bool anchor = pat.Length > 0 && pat[0] == '^';
            int pp = anchor ? 1 : 0;
            var sb = new StringBuilder(s.Length);
            int sp = 0;
            count = 0;
            while (count < maxN)
            {
                ms.level = 0;
                ms.matchdepth = MAXCCALLS;
                int e = Match(ms, sp, pp);
                if (e != -1)
                {
                    count++;
                    var caps = PushCaptures(ms, sp, e, true);
                    object rep = repl(caps);
                    if (rep == null || rep is LuaNil || (rep is bool b && !b))
                        sb.Append(s.Substring(sp, e - sp)); // nil/false -> keep original
                    else
                        sb.Append(LuaStr(rep));
                }
                if (e != -1 && e > sp) sp = e;
                else if (sp < s.Length) sb.Append(s[sp++]);
                else break;
                if (anchor) break;
            }
            if (sp < s.Length) sb.Append(s.Substring(sp));
            result = sb.ToString();
        }

        private static void AppendReplacement(MatchState ms, StringBuilder sb, int s, int e, string repl)
        {
            for (int i = 0; i < repl.Length; i++)
            {
                char c = repl[i];
                if (c != L_ESC) { sb.Append(c); continue; }
                i++;
                if (i >= repl.Length) break;
                char n = repl[i];
                if (n == L_ESC) { sb.Append(L_ESC); }
                else if (n == '0') { sb.Append(ms.src.Substring(s, e - s)); }
                else if (n >= '1' && n <= '9') { sb.Append(LuaStr(GetCapture(ms, n - '1', s, e))); }
                else { sb.Append(n); }
            }
        }

        // ---- helpers ----
        private static string LuaStr(object o)
        {
            if (o is string str) return str;
            if (o is float f) { double d = f; return (d == Math.Floor(d)) ? ((long)d).ToString(System.Globalization.CultureInfo.InvariantCulture) : d.ToString(System.Globalization.CultureInfo.InvariantCulture); }
            return o == null ? "" : o.ToString();
        }

        private static int PosRelat(int pos, int len)
        {
            if (pos >= 0) return pos;
            if (-pos > len) return 0;
            return len + pos + 1;
        }

        private static bool NoSpecials(string p)
        {
            return p.IndexOfAny(new[] { '^', '$', '*', '+', '?', '.', '(', '[', '%', '-' }) < 0;
        }
    }
}
